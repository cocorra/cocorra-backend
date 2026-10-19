using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Cocorra.API.Controllers;
using Cocorra.BLL.Base;
using Cocorra.BLL.Services.AuditLogs;
using Cocorra.BLL.Services.NotificationService;
using Cocorra.DAL.Data;
using Cocorra.DAL.DTOS.NotificationDto;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;
using Cocorra.Tests.Helpers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Cocorra.Tests;

public class BroadcastNotificationTests : IDisposable
{
    private readonly SqliteTestHost _host = new();
    private readonly Mock<IBroadcastPushQueue> _queueMock = new();
    private readonly Mock<IAuditLogService> _auditMock = new();
    private readonly List<BroadcastPushJob> _queuedJobs = new();

    private readonly Guid _activeWithToken = Guid.NewGuid();
    private readonly Guid _activeWithoutToken = Guid.NewGuid();
    private readonly Guid _pendingWithToken = Guid.NewGuid();
    private readonly Guid _bannedUser = Guid.NewGuid();

    public BroadcastNotificationTests()
    {
        _queueMock.Setup(q => q.TryEnqueue(It.IsAny<BroadcastPushJob>()))
            .Callback<BroadcastPushJob>(_queuedJobs.Add)
            .Returns(true);

        using var scope = _host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Users.AddRange(
            new ApplicationUser { Id = _activeWithToken, UserName = "a1", FirstName = "A1", LastName = "User", Status = UserStatus.Active, FcmToken = "token-a1" },
            new ApplicationUser { Id = _activeWithoutToken, UserName = "a2", FirstName = "A2", LastName = "User", Status = UserStatus.Active, FcmToken = null },
            new ApplicationUser { Id = _pendingWithToken, UserName = "p1", FirstName = "P1", LastName = "User", Status = UserStatus.Pending, FcmToken = "token-p1" },
            new ApplicationUser { Id = _bannedUser, UserName = "b1", FirstName = "B1", LastName = "User", Status = UserStatus.Banned }
        );
        db.SaveChanges();
    }

    public void Dispose() => _host.Dispose();

    private BroadcastNotificationService CreateService(IServiceScope scope) => new(
        scope.ServiceProvider.GetRequiredService<AppDbContext>(),
        _queueMock.Object,
        NullLogger<BroadcastNotificationService>.Instance,
        _auditMock.Object);

    private async Task<Response<BroadcastNotificationResultDto>> BroadcastAsync(string title, string message)
    {
        using var scope = _host.CreateScope();
        return await CreateService(scope).BroadcastAsync(new BroadcastNotificationDto { Title = title, Message = message }, Guid.NewGuid());
    }

    private List<Notification> SavedNotifications()
    {
        using var scope = _host.CreateScope();
        return scope.ServiceProvider.GetRequiredService<AppDbContext>().Notifications.ToList();
    }

    [Fact]
    public async Task Broadcast_SavesSystemNotificationForEveryActiveUserOnly()
    {
        var result = await BroadcastAsync("  Update  ", "  New rooms are live.  ");

        Assert.True(result.Succeeded);
        var saved = SavedNotifications();
        Assert.Equal(new[] { _activeWithToken, _activeWithoutToken }.OrderBy(x => x), saved.Select(n => n.UserId).OrderBy(x => x));
        Assert.All(saved, n =>
        {
            Assert.Equal("Update", n.Title);
            Assert.Equal("New rooms are live.", n.Message);
            Assert.Equal(NotificationType.System, n.Type);
            Assert.Equal(result.Data!.BroadcastId, n.ReferenceId);
            Assert.False(n.IsRead);
        });
        Assert.Equal(2, result.Data!.RecipientCount);
    }

    [Fact]
    public async Task Broadcast_QueuesPushOnlyForActiveUsersWithToken()
    {
        var result = await BroadcastAsync("Update", "Hello");

        var job = Assert.Single(_queuedJobs);
        var recipient = Assert.Single(job.Recipients);
        Assert.Equal(_activeWithToken, recipient.UserId);
        Assert.Equal("token-a1", recipient.FcmToken);
        Assert.Equal(result.Data!.BroadcastId, job.BroadcastId);
        Assert.Equal("Update", job.Title);
        Assert.Equal("Hello", job.Message);
        Assert.Equal(1, result.Data.PushTargetCount);
        Assert.True(result.Data.PushQueued);
    }

    [Theory]
    [InlineData("   ", "Hello")]
    [InlineData("Update", "   ")]
    [InlineData("", "")]
    public async Task Broadcast_BlankTitleOrMessage_ReturnsBadRequestAndSendsNothing(string title, string message)
    {
        var result = await BroadcastAsync(title, message);

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        Assert.Empty(SavedNotifications());
        _queueMock.Verify(q => q.TryEnqueue(It.IsAny<BroadcastPushJob>()), Times.Never);
        _auditMock.Verify(a => a.RecordAsync(It.IsAny<AuditEntry>()), Times.Never);
    }

    [Fact]
    public async Task Broadcast_TooLongMessage_ReturnsBadRequest()
    {
        var result = await BroadcastAsync("Update", new string('x', BroadcastNotificationService.MaxMessageLength + 1));

        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        Assert.Empty(SavedNotifications());
    }

    [Fact]
    public async Task Broadcast_QueueFull_StillSavesInAppNotifications()
    {
        _queueMock.Setup(q => q.TryEnqueue(It.IsAny<BroadcastPushJob>())).Returns(false);

        var result = await BroadcastAsync("Update", "Hello");

        Assert.True(result.Succeeded);
        Assert.False(result.Data!.PushQueued);
        Assert.Equal(1, result.Data.PushTargetCount);
        Assert.Equal(2, SavedNotifications().Count);
    }

    [Fact]
    public async Task Broadcast_NoRegisteredDevices_DoesNotQueue()
    {
        using (var scope = _host.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var user = db.Users.Single(u => u.Id == _activeWithToken);
            user.FcmToken = null;
            db.SaveChanges();
        }

        var result = await BroadcastAsync("Update", "Hello");

        Assert.True(result.Succeeded);
        Assert.False(result.Data!.PushQueued);
        Assert.Equal(0, result.Data.PushTargetCount);
        _queueMock.Verify(q => q.TryEnqueue(It.IsAny<BroadcastPushJob>()), Times.Never);
    }

    [Fact]
    public async Task Broadcast_RecordsAuditEntryForActingAdmin()
    {
        var adminId = Guid.NewGuid();
        using var scope = _host.CreateScope();

        await CreateService(scope).BroadcastAsync(new BroadcastNotificationDto { Title = "Update", Message = "Hello" }, adminId);

        _auditMock.Verify(a => a.RecordAsync(It.Is<AuditEntry>(e =>
            e.Action == AuditActions.NotificationBroadcast
            && e.ActorUserId == adminId
            && e.TargetUserId == null)), Times.Once);
    }

    [Fact]
    public void Queue_RejectsJobsBeyondCapacity()
    {
        var queue = new BroadcastPushQueue();
        var job = new BroadcastPushJob(Guid.NewGuid(), "t", "m", Array.Empty<PushRecipient>());

        for (var i = 0; i < BroadcastPushQueue.Capacity; i++)
            Assert.True(queue.TryEnqueue(job));

        Assert.False(queue.TryEnqueue(job));
    }

    [Fact]
    public async Task Sender_PushesEveryRecipientWithBroadcastData()
    {
        var pushMock = new Mock<IPushNotificationService>();
        var sent = new System.Collections.Concurrent.ConcurrentBag<(string Token, string Title, string Body, Dictionary<string, string> Data)>();
        pushMock.Setup(p => p.SendPushNotificationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Dictionary<string, string>>()))
            .Callback<string, string, string, Dictionary<string, string>>((t, ti, b, d) => sent.Add((t, ti, b, d)))
            .Returns(Task.CompletedTask);

        var services = new ServiceCollection().AddSingleton(pushMock.Object).BuildServiceProvider();
        var sender = new BroadcastPushSender(new BroadcastPushQueue(), services.GetRequiredService<IServiceScopeFactory>(), NullLogger<BroadcastPushSender>.Instance);

        var broadcastId = Guid.NewGuid();
        var recipients = Enumerable.Range(0, 25).Select(i => new PushRecipient(Guid.NewGuid(), $"token-{i}")).ToList();

        await sender.SendAsync(new BroadcastPushJob(broadcastId, "Update", "Hello", recipients), CancellationToken.None);

        Assert.Equal(25, sent.Count);
        Assert.Equal(recipients.Select(r => r.FcmToken).OrderBy(x => x), sent.Select(s => s.Token).OrderBy(x => x));
        foreach (var push in sent)
        {
            var recipient = recipients.Single(r => r.FcmToken == push.Token);
            Assert.Equal("Update", push.Title);
            Assert.Equal("Hello", push.Body);
            Assert.Equal("general", push.Data["type"]);
            Assert.Equal(broadcastId.ToString(), push.Data["broadcastId"]);
            Assert.Equal(recipient.UserId.ToString(), push.Data["userId"]);
        }
    }

    [Fact]
    public void Controller_IsAdminOnly()
    {
        var roles = typeof(AdminNotificationsController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .OfType<AuthorizeAttribute>()
            .Select(a => a.Roles)
            .ToList();

        Assert.Equal(new[] { "Admin" }, roles);
    }

    [Fact]
    public async Task Controller_PassesActingAdminToService()
    {
        var adminId = Guid.NewGuid();
        var serviceMock = new Mock<IBroadcastNotificationService>();
        serviceMock.Setup(s => s.BroadcastAsync(It.IsAny<BroadcastNotificationDto>(), adminId))
            .ReturnsAsync(new Response<BroadcastNotificationResultDto> { StatusCode = HttpStatusCode.OK, Succeeded = true });

        var controller = new AdminNotificationsController(serviceMock.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, adminId.ToString()) }, "test"))
                }
            }
        };

        var result = await controller.Broadcast(new BroadcastNotificationDto { Title = "t", Message = "m" });

        Assert.Equal(200, Assert.IsType<ObjectResult>(result).StatusCode);
        serviceMock.Verify(s => s.BroadcastAsync(It.IsAny<BroadcastNotificationDto>(), adminId), Times.Once);
    }

    [Fact]
    public async Task Controller_WithoutUserId_ReturnsUnauthorized()
    {
        var serviceMock = new Mock<IBroadcastNotificationService>();
        var controller = new AdminNotificationsController(serviceMock.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var result = await controller.Broadcast(new BroadcastNotificationDto { Title = "t", Message = "m" });

        Assert.IsType<UnauthorizedResult>(result);
        serviceMock.Verify(s => s.BroadcastAsync(It.IsAny<BroadcastNotificationDto>(), It.IsAny<Guid>()), Times.Never);
    }
}
