using Cocorra.BLL.Services.EventTracking;
using Cocorra.BLL.Services.NotificationService;
using Cocorra.BLL.Services.RealTimeNotifier;
using Cocorra.BLL.Services.SupportService;
using Cocorra.BLL.Services.Upload;
using Cocorra.DAL.DTOS.SupportChatDto;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;
using Cocorra.DAL.Repository.NotificationRepository;
using Cocorra.DAL.Repository.SupportRepository;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Cocorra.Tests;

/// <summary>
/// SignalR only reaches a live connection, so support chat also pushes over FCM to cover a
/// backgrounded, locked or terminated app. The app keys off data.type == "support_chat" to
/// deep-link, and off chatId to open the right conversation — both are pinned here.
/// </summary>
public class SupportChatPushTests
{
    private const string SupportChatType = "support_chat";

    private static Mock<UserManager<ApplicationUser>> CreateMockUserManager()
    {
        var store = new Mock<IUserStore<ApplicationUser>>();
        return new Mock<UserManager<ApplicationUser>>(
            store.Object, null!, null!, null!, null!, null!, null!, null!, null!);
    }

    private sealed class Harness
    {
        public Mock<ISupportRepository> Repo { get; } = new();
        public Mock<UserManager<ApplicationUser>> Users { get; } = CreateMockUserManager();
        public Mock<IPushNotificationService> Push { get; } = new();

        public (string Token, string Title, string Body, Dictionary<string, string> Data)? Sent;

        public Harness()
        {
            Push.Setup(p => p.SendPushNotificationAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<Dictionary<string, string>>()))
                .Callback<string, string, string, Dictionary<string, string>>(
                    (token, title, body, data) => Sent = (token, title, body, data))
                .Returns(Task.CompletedTask);
        }

        public SupportService Build() => new(
            Repo.Object,
            new Mock<IUploadImage>().Object,
            Users.Object,
            new Mock<INotificationRepository>().Object,
            new Mock<IRealTimeNotifier>().Object,
            Push.Object,
            new Mock<IEventTracker>().Object,
            new Mock<ILogger<SupportService>>().Object);
    }

    // ── Admin reply → the user, which is the case the mobile app cares about ─────

    [Fact]
    public async Task AdminReply_PushesToTheUser_WithSupportChatTypeAndChatId()
    {
        const string adminId = "admin-1";
        const string userId = "user-1";
        var chat = new SupportChat
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            AdminId = adminId,
            Status = SupportChatStatus.Active
        };

        var harness = new Harness();
        harness.Repo.Setup(r => r.GetChatByIdAsync(chat.Id)).ReturnsAsync(chat);
        harness.Users.Setup(m => m.FindByIdAsync(userId))
            .ReturnsAsync(new ApplicationUser { FcmToken = "user_device_token" });

        var result = await harness.Build().AdminReplyAsync(
            chat.Id, adminId, new SendMessageDto { Content = "We are looking into it." });

        Assert.True(result.Succeeded);
        Assert.NotNull(harness.Sent);

        var (token, title, body, data) = harness.Sent!.Value;
        Assert.Equal("user_device_token", token);
        Assert.Equal("Cocorra Support", title);
        Assert.Equal("We are looking into it.", body);
        Assert.Equal(SupportChatType, data["type"]);
        Assert.Equal(chat.Id.ToString(), data["chatId"]);
    }

    [Fact]
    public async Task AdminReply_DoesNotPush_WhenTheUserHasNoToken()
    {
        const string adminId = "admin-1";
        var chat = new SupportChat
        {
            Id = Guid.NewGuid(),
            UserId = "user-1",
            AdminId = adminId,
            Status = SupportChatStatus.Active
        };

        var harness = new Harness();
        harness.Repo.Setup(r => r.GetChatByIdAsync(chat.Id)).ReturnsAsync(chat);
        harness.Users.Setup(m => m.FindByIdAsync("user-1"))
            .ReturnsAsync(new ApplicationUser { FcmToken = null });

        var result = await harness.Build().AdminReplyAsync(
            chat.Id, adminId, new SendMessageDto { Content = "Hello?" });

        Assert.True(result.Succeeded);
        Assert.Null(harness.Sent);
    }

    [Fact]
    public async Task AdminReply_StillSucceeds_WhenThePushThrows()
    {
        // The message is already written; a dead FCM path must not turn that into a failure.
        const string adminId = "admin-1";
        var chat = new SupportChat
        {
            Id = Guid.NewGuid(),
            UserId = "user-1",
            AdminId = adminId,
            Status = SupportChatStatus.Active
        };

        var harness = new Harness();
        harness.Repo.Setup(r => r.GetChatByIdAsync(chat.Id)).ReturnsAsync(chat);
        harness.Users.Setup(m => m.FindByIdAsync("user-1"))
            .ReturnsAsync(new ApplicationUser { FcmToken = "user_device_token" });
        harness.Push.Setup(p => p.SendPushNotificationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<Dictionary<string, string>>()))
            .ThrowsAsync(new Exception("FCM unreachable"));

        var result = await harness.Build().AdminReplyAsync(
            chat.Id, adminId, new SendMessageDto { Content = "Still there?" });

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task AdminReply_SendsAPreview_NotARawJsonEnvelope()
    {
        const string adminId = "admin-1";
        var chat = new SupportChat
        {
            Id = Guid.NewGuid(),
            UserId = "user-1",
            AdminId = adminId,
            Status = SupportChatStatus.Active
        };

        var harness = new Harness();
        harness.Repo.Setup(r => r.GetChatByIdAsync(chat.Id)).ReturnsAsync(chat);
        harness.Users.Setup(m => m.FindByIdAsync("user-1"))
            .ReturnsAsync(new ApplicationUser { FcmToken = "user_device_token" });

        await harness.Build().AdminReplyAsync(
            chat.Id, adminId,
            new SendMessageDto { Content = "{\"type\":\"image\",\"url\":\"https://cdn/x.png\"}" });

        Assert.NotNull(harness.Sent);
        Assert.DoesNotContain("{", harness.Sent!.Value.Body);
    }

    // ── User message → the admin who owns the chat ───────────────────────────────

    [Fact]
    public async Task UserMessage_PushesToTheAssignedAdmin_OnAnActiveChat()
    {
        const string userId = "user-1";
        var chat = new SupportChat
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            AdminId = "admin-1",
            Status = SupportChatStatus.Active
        };

        var harness = new Harness();
        harness.Repo.Setup(r => r.GetUserOpenChatAsync(userId)).ReturnsAsync(chat);
        harness.Users.Setup(m => m.FindByIdAsync(userId))
            .ReturnsAsync(new ApplicationUser { FirstName = "Nadia", LastName = "Farouk" });
        harness.Users.Setup(m => m.FindByIdAsync("admin-1"))
            .ReturnsAsync(new ApplicationUser { FcmToken = "admin_device_token" });

        var result = await harness.Build().SendMessageAsync(
            userId, new SendMessageDto { Content = "Any update?" });

        Assert.True(result.Succeeded);
        Assert.NotNull(harness.Sent);

        var (token, title, body, data) = harness.Sent!.Value;
        Assert.Equal("admin_device_token", token);
        Assert.Equal("Nadia Farouk", title);
        Assert.Equal("Any update?", body);
        Assert.Equal(SupportChatType, data["type"]);
        Assert.Equal(chat.Id.ToString(), data["chatId"]);
    }

    [Fact]
    public async Task UserMessage_DoesNotPush_WhileTheChatIsStillUnclaimed()
    {
        // A Pending chat belongs to no admin. Fanning out to every admin would notify
        // people about a conversation none of them owns; the dashboard's SignalR
        // NewPendingChatAlert is the signal for that state.
        const string userId = "user-1";
        var chat = new SupportChat
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            AdminId = null,
            Status = SupportChatStatus.Pending
        };

        var harness = new Harness();
        harness.Repo.Setup(r => r.GetUserOpenChatAsync(userId)).ReturnsAsync(chat);
        harness.Repo.Setup(r => r.GetPendingUserMessageCountAsync(chat.Id)).ReturnsAsync(0);

        var result = await harness.Build().SendMessageAsync(
            userId, new SendMessageDto { Content = "Hello, I need help." });

        Assert.True(result.Succeeded);
        Assert.Null(harness.Sent);
    }

    [Fact]
    public async Task UserMessage_DoesNotPush_WhenTheMessageIsRejected()
    {
        // Fourth pending message: rejected, nothing stored, so nothing to announce.
        const string userId = "user-1";
        var chat = new SupportChat
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            AdminId = "admin-1",
            Status = SupportChatStatus.Pending
        };

        var harness = new Harness();
        harness.Repo.Setup(r => r.GetUserOpenChatAsync(userId)).ReturnsAsync(chat);
        harness.Repo.Setup(r => r.GetPendingUserMessageCountAsync(chat.Id)).ReturnsAsync(3);

        var result = await harness.Build().SendMessageAsync(
            userId, new SendMessageDto { Content = "Hello??" });

        Assert.False(result.Succeeded);
        Assert.Null(harness.Sent);
    }
}
