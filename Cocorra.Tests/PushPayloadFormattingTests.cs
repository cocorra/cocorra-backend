using Cocorra.BLL.Services.ChatService;
using Cocorra.BLL.Services.EventTracking;
using Cocorra.BLL.Services.NotificationService;
using Cocorra.BLL.Services.RealTimeNotifier;
using Cocorra.BLL.Services.SupportService;
using Cocorra.BLL.Services.Upload;
using Cocorra.DAL.DTOS.ReportDto;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;
using Cocorra.DAL.Repository.MessageRepository;
using Cocorra.DAL.Repository.NotificationRepository;
using Cocorra.DAL.Repository.SupportRepository;
using Cocorra.DAL.Repository.UserBlockRepository;
using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Cocorra.Tests;

/// <summary>
/// A closed app never runs Flutter's message handlers: Android and iOS render
/// notification.body straight into the tray. Anything that is not prose — a JSON
/// envelope from a rich chat message, most of all — is therefore shown to the user
/// exactly as it was sent. These tests pin the two places that now prevent that.
/// </summary>
public class PushPayloadFormattingTests
{
    // ── The wire-level guard in PushNotificationService ──────────────────────────

    private static PushNotificationService CreatePushService() =>
        new(new Mock<ILogger<PushNotificationService>>().Object);

    [Theory]
    [InlineData("{\"type\":\"voice\",\"url\":\"https://cdn/x.m4a\",\"duration\":12}")]
    [InlineData("  {\"text\":\"hi\"}  ")]
    [InlineData("[{\"id\":1},{\"id\":2}]")]
    public void BuildDisplayBody_ReplacesSerializedPayloads(string body)
    {
        var result = CreatePushService().BuildDisplayBody(body, "chat");

        Assert.Equal(PushNotificationService.OpaqueBodyFallback, result);
    }

    [Theory]
    [InlineData("Your account has been permanently banned.")]
    [InlineData("The room 'Late night {jazz}' has just started. Join now!")]
    [InlineData("Account Verified ✅")]
    public void BuildDisplayBody_LeavesProseAlone(string body)
    {
        var result = CreatePushService().BuildDisplayBody(body, "general");

        Assert.Equal(body, result);
    }

    [Fact]
    public void BuildDisplayBody_PreservesEmptyBody_SoDataOnlyPushesStaySilent()
    {
        // account_locked and account_rejected are sent with "" title and body on purpose:
        // an empty alert is what keeps Firebase from attaching a Notification object.
        Assert.Equal("", CreatePushService().BuildDisplayBody("", "account_locked"));
        Assert.Null(CreatePushService().BuildDisplayBody(null, "account_rejected"));
    }

    [Fact]
    public void BuildDisplayBody_TruncatesOverlongBodies()
    {
        var body = new string('a', PushNotificationService.MaxBodyLength + 50);

        var result = CreatePushService().BuildDisplayBody(body, "chat");

        Assert.NotNull(result);
        Assert.Equal(PushNotificationService.MaxBodyLength, result!.Length);
        Assert.EndsWith("…", result);
    }

    [Fact]
    public void BuildDisplayBody_DoesNotTruncateAtTheBoundary()
    {
        var body = new string('a', PushNotificationService.MaxBodyLength);

        var result = CreatePushService().BuildDisplayBody(body, "chat");

        Assert.Equal(body, result);
    }

    // ── The chat preview, which is where the reported JSON actually came from ────

    private static Mock<UserManager<ApplicationUser>> CreateMockUserManager()
    {
        var store = new Mock<IUserStore<ApplicationUser>>();
        return new Mock<UserManager<ApplicationUser>>(
            store.Object, null!, null!, null!, null!, null!, null!, null!, null!);
    }

    /// <summary>
    /// Sends <paramref name="content"/> through the real chat path and returns the body
    /// that reached the push service.
    /// </summary>
    private static async Task<string> CapturePushedChatBodyAsync(string content)
    {
        var senderId = Guid.NewGuid();
        var receiverId = Guid.NewGuid();

        var userManagerMock = CreateMockUserManager();
        userManagerMock.Setup(m => m.FindByIdAsync(senderId.ToString()))
            .ReturnsAsync(new ApplicationUser { Id = senderId, FirstName = "Alice", LastName = "Smith" });
        userManagerMock.Setup(m => m.FindByIdAsync(receiverId.ToString()))
            .ReturnsAsync(new ApplicationUser { Id = receiverId, FcmToken = "receiver_token" });

        var userBlockRepoMock = new Mock<IUserBlockRepository>();
        userBlockRepoMock.Setup(b => b.IsBlockedAsync(senderId, receiverId)).ReturnsAsync(false);

        string? capturedBody = null;
        var pushServiceMock = new Mock<IPushNotificationService>();
        pushServiceMock
            .Setup(p => p.SendPushNotificationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<Dictionary<string, string>>()))
            .Callback<string, string, string, Dictionary<string, string>>(
                (_, _, body, _) => capturedBody = body)
            .Returns(Task.CompletedTask);

        var chatService = new ChatService(
            userBlockRepoMock.Object,
            new Mock<IMessageRepository>().Object,
            pushServiceMock.Object,
            userManagerMock.Object,
            new Mock<IMediator>().Object,
            new Mock<IEventTracker>().Object,
            new Mock<ILogger<ChatService>>().Object);

        var response = await chatService.SaveMessageAsync(senderId, receiverId, content);

        Assert.True(response.Succeeded);
        Assert.NotNull(capturedBody);
        return capturedBody!;
    }

    [Fact]
    public async Task ChatPush_DoesNotForwardRawJsonEnvelopeToTheTray()
    {
        var body = await CapturePushedChatBodyAsync(
            "{\"type\":\"voice\",\"url\":\"https://cdn.cocorra.app/v/1.m4a\",\"duration\":12}");

        Assert.DoesNotContain("{", body);
        Assert.DoesNotContain("cdn.cocorra.app", body);
    }

    [Fact]
    public async Task ChatPush_UsesTheEnvelopeTextWhenTheClientProvidesOne()
    {
        var body = await CapturePushedChatBodyAsync(
            "{\"type\":\"reply\",\"text\":\"See you at 8\",\"replyTo\":\"abc\"}");

        Assert.Equal("See you at 8", body);
    }

    [Fact]
    public async Task ChatPush_ForwardsPlainTextUnchanged()
    {
        var body = await CapturePushedChatBodyAsync("Hello Bob!");

        Assert.Equal("Hello Bob!", body);
    }

    [Fact]
    public async Task ChatPush_ForwardsTextThatMerelyStartsWithABrace()
    {
        // Not JSON, just a message someone typed — it must not be swallowed.
        var body = await CapturePushedChatBodyAsync("{this is not json, it's how I type");

        Assert.Equal("{this is not json, it's how I type", body);
    }

    // ── account_locked must stay data-only on every path that sends it ───────────

    [Fact]
    public async Task Mute24h_SendsAccountLockedAsDataOnly_WithLockoutEnd()
    {
        // An empty title and body are what keep Firebase from attaching a Notification
        // object. Attach one and the OS shows a pop-up instead of the app silently
        // routing to BannedScreen, which is what the payload contract requires.
        var reportedUserId = Guid.NewGuid();
        var report = new Report
        {
            Id = Guid.NewGuid(),
            ReportedUserId = reportedUserId,
            CreatedAt = DateTime.UtcNow.AddHours(-1)
        };

        var supportRepoMock = new Mock<ISupportRepository>();
        supportRepoMock.Setup(r => r.GetReportByIdAsync(report.Id)).ReturnsAsync(report);

        var userManagerMock = CreateMockUserManager();
        var mutedUser = new ApplicationUser { Id = reportedUserId, FcmToken = "muted_device_token" };
        userManagerMock.Setup(m => m.FindByIdAsync(reportedUserId.ToString())).ReturnsAsync(mutedUser);
        userManagerMock.Setup(m => m.SetLockoutEnabledAsync(mutedUser, true)).ReturnsAsync(IdentityResult.Success);
        userManagerMock.Setup(m => m.SetLockoutEndDateAsync(mutedUser, It.IsAny<DateTimeOffset?>()))
            .ReturnsAsync(IdentityResult.Success);

        var pushServiceMock = new Mock<IPushNotificationService>();

        var supportService = new SupportService(
            supportRepoMock.Object,
            new Mock<IUploadImage>().Object,
            userManagerMock.Object,
            new Mock<INotificationRepository>().Object,
            new Mock<IRealTimeNotifier>().Object,
            pushServiceMock.Object,
            new Mock<IEventTracker>().Object,
            new Mock<ILogger<SupportService>>().Object);

        var result = await supportService.TakeActionOnReportAsync(
            report.Id, new TakeReportActionDto { Action = AdminReportAction.Mute24h });

        Assert.True(result.Succeeded);

        pushServiceMock.Verify(p => p.SendPushNotificationAsync(
                "muted_device_token",
                "",
                "",
                It.Is<Dictionary<string, string>>(d =>
                    d["type"] == "account_locked"
                    && d.ContainsKey("lockout_end")
                    && DateTimeOffset.Parse(d["lockout_end"]) > DateTimeOffset.UtcNow.AddHours(23))),
            Times.Once);
    }

    [Fact]
    public async Task ChatPush_StoresTheOriginalContentEvenWhenThePreviewDiffers()
    {
        // The preview is a display concern; the message itself must survive intact.
        const string envelope = "{\"type\":\"image\",\"url\":\"https://cdn/x.png\"}";

        var senderId = Guid.NewGuid();
        var receiverId = Guid.NewGuid();

        var userManagerMock = CreateMockUserManager();
        userManagerMock.Setup(m => m.FindByIdAsync(It.IsAny<string>()))
            .ReturnsAsync(new ApplicationUser { FcmToken = "receiver_token" });

        var userBlockRepoMock = new Mock<IUserBlockRepository>();
        userBlockRepoMock.Setup(b => b.IsBlockedAsync(senderId, receiverId)).ReturnsAsync(false);

        var chatService = new ChatService(
            userBlockRepoMock.Object,
            new Mock<IMessageRepository>().Object,
            new Mock<IPushNotificationService>().Object,
            userManagerMock.Object,
            new Mock<IMediator>().Object,
            new Mock<IEventTracker>().Object,
            new Mock<ILogger<ChatService>>().Object);

        var response = await chatService.SaveMessageAsync(senderId, receiverId, envelope);

        Assert.True(response.Succeeded);
        Assert.Equal(envelope, response.Data!.Content);
    }
}
