using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using Cocorra.BLL.Services.AdminService;
using Cocorra.BLL.Services.Email;
using Cocorra.BLL.Services.EventTracking;
using Cocorra.BLL.Services.NotificationService;
using Cocorra.BLL.Services.Upload;
using Cocorra.DAL.Data;
using Cocorra.DAL.DTOS.AdminDto;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;
using Cocorra.DAL.Repository.BlockedDevicesRepository;
using Cocorra.DAL.Repository.NotificationRepository;
using Cocorra.DAL.Repository.UserRepository;
using Cocorra.Tests.Helpers;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Moq;
using Xunit;

namespace Cocorra.Tests;

public class AdminServiceTests : IDisposable
{
    private readonly SqliteTestHost _host = new();
    private readonly Mock<UserManager<ApplicationUser>> _userManagerMock = TestIdentityHelper.CreateMockUserManager();
    private readonly Mock<IUploadVoice> _uploadVoiceMock = new();
    private readonly Mock<IConfiguration> _configMock = new();
    private readonly Mock<IEmailService> _emailServiceMock = new();
    private readonly Mock<IUserRepository> _userRepoMock = new();
    private readonly Mock<IPushNotificationService> _pushServiceMock = new();
    private readonly Mock<IBlockedDevicesRepository> _blockedDevicesRepoMock = new();
    private readonly Mock<IEventTracker> _eventTrackerMock = new();
    private readonly Mock<INotificationRepository> _notificationRepoMock = new();

    public void Dispose() => _host.Dispose();

    private AdminService CreateService() => CreateService(out _);

    private AdminService CreateService(out AppDbContext db)
    {
        var scope = _host.CreateScope();
        db = (AppDbContext)scope.ServiceProvider.GetService(typeof(AppDbContext))!;
        _configMock.Setup(c => c["AppSettings:BaseUrl"]).Returns("https://api.cocorra.com");

        return new AdminService(
            _userManagerMock.Object,
            _uploadVoiceMock.Object,
            _configMock.Object,
            _emailServiceMock.Object,
            _userRepoMock.Object,
            _pushServiceMock.Object,
            _blockedDevicesRepoMock.Object,
            _eventTrackerMock.Object,
            db,
            _notificationRepoMock.Object
        );
    }

    [Fact]
    public async Task ChangeUserStatusAsync_UserNotFound_ReturnsBadRequest()
    {
        var userId = Guid.NewGuid();
        _userManagerMock.Setup(m => m.FindByIdAsync(userId.ToString())).ReturnsAsync((ApplicationUser?)null);

        var service = CreateService();
        var result = await service.ChangeUserStatusAsync(userId, UserStatus.Active, Guid.NewGuid());

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        Assert.Equal("User not found", result.Message);
    }

    [Fact]
    public async Task ChangeUserStatusAsync_AlreadyInTargetStatus_ReturnsBadRequest()
    {
        var userId = Guid.NewGuid();
        var user = new ApplicationUser { Id = userId, Status = UserStatus.Active };
        _userManagerMock.Setup(m => m.FindByIdAsync(userId.ToString())).ReturnsAsync(user);

        var service = CreateService();
        var result = await service.ChangeUserStatusAsync(userId, UserStatus.Active, Guid.NewGuid());

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        Assert.Contains("already Active", result.Message);
    }

    [Fact]
    public async Task ChangeUserStatusAsync_ActivatePendingUser_ClearsLockoutDeletesVoiceNotifiesAndTracksEvent()
    {
        var userId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var user = new ApplicationUser
        {
            Id = userId,
            Status = UserStatus.Pending,
            VoiceVerificationPath = "voices/test.mp3",
            Email = "user@cocorra.com",
            FirstName = "Sami",
            FcmToken = "fcm_token_123"
        };
        _userManagerMock.Setup(m => m.FindByIdAsync(userId.ToString())).ReturnsAsync(user);
        _userManagerMock.Setup(m => m.UpdateAsync(user)).ReturnsAsync(IdentityResult.Success);

        var service = CreateService();
        var result = await service.ChangeUserStatusAsync(userId, UserStatus.Active, adminId);

        Assert.True(result.Succeeded);
        Assert.Equal(UserStatus.Active, user.Status);
        Assert.Null(user.VoiceVerificationPath);

        _uploadVoiceMock.Verify(u => u.DeleteVoice("voices/test.mp3"), Times.Once);
        _notificationRepoMock.Verify(n => n.AddAsync(It.Is<Notification>(notif =>
            notif.UserId == userId && notif.Title.Contains("Verified"))), Times.Once);
        _emailServiceMock.Verify(e => e.SendEmailAsync(
            "user@cocorra.com",
            It.Is<string>(s => s.Contains("Verified") || s.Contains("توثيق")),
            It.Is<string>(html => html.Contains("role=\"presentation\"") && html.Contains(Cocorra.BLL.Services.Email.EmailTemplates.DefaultLogoUrl) && html.Contains("Sami")),
            It.IsAny<System.Threading.CancellationToken>()), Times.Once);
        _pushServiceMock.Verify(p => p.SendPushNotificationAsync(
            "fcm_token_123",
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<Dictionary<string, string>>()), Times.Once);

        _eventTrackerMock.Verify(t => t.Track(
            EventTypes.VoiceVerificationResult,
            userId,
            It.IsAny<object>()), Times.Once);
        _eventTrackerMock.Verify(t => t.Track(
            EventTypes.ActivationCompleted,
            userId,
            null,
            $"{EventTypes.ActivationCompleted}:{userId}",
            null,
            null,
            1), Times.Once);
    }

    [Fact]
    public async Task ChangeUserStatusAsync_BanActiveUser_LocksOutUserClearsTokensAndPushes()
    {
        var userId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var user = new ApplicationUser
        {
            Id = userId,
            Status = UserStatus.Active,
            RefreshToken = "token_abc",
            FcmToken = "fcm_xyz"
        };
        _userManagerMock.Setup(m => m.FindByIdAsync(userId.ToString())).ReturnsAsync(user);
        _userManagerMock.Setup(m => m.UpdateAsync(user)).ReturnsAsync(IdentityResult.Success);

        var service = CreateService();
        var result = await service.ChangeUserStatusAsync(userId, UserStatus.Banned, adminId);

        Assert.True(result.Succeeded);
        Assert.Equal(UserStatus.Banned, user.Status);
        Assert.Null(user.RefreshToken);
        Assert.Null(user.FcmToken); // Cleared after notification

        _notificationRepoMock.Verify(n => n.AddAsync(It.Is<Notification>(notif =>
            notif.UserId == userId && notif.Type == NotificationType.AdminWarning)), Times.Once);
        _pushServiceMock.Verify(p => p.SendPushNotificationAsync(
            "fcm_xyz",
            "",
            "",
            It.Is<Dictionary<string, string>>(d => d["type"] == "account_locked")), Times.Once);
    }

    [Fact]
    public async Task BulkChangeUserStatusAsync_EmptyUserList_ReturnsBadRequest()
    {
        var service = CreateService();
        var dto = new BulkChangeStatusDto { UserIds = new List<Guid>(), NewStatus = UserStatus.Active };

        var result = await service.BulkChangeUserStatusAsync(dto, Guid.NewGuid());

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
    }

    [Fact]
    public async Task BulkChangeUserStatusAsync_AdminCannotChangeSelfStatus_RecordsItemFailure()
    {
        var adminId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();
        var otherUser = new ApplicationUser { Id = otherUserId, Status = UserStatus.Pending };

        _userManagerMock.Setup(m => m.FindByIdAsync(otherUserId.ToString())).ReturnsAsync(otherUser);
        _userManagerMock.Setup(m => m.UpdateAsync(otherUser)).ReturnsAsync(IdentityResult.Success);

        var service = CreateService();
        var dto = new BulkChangeStatusDto
        {
            UserIds = new List<Guid> { adminId, otherUserId },
            NewStatus = UserStatus.Active
        };

        var result = await service.BulkChangeUserStatusAsync(dto, adminId);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Data);
        Assert.Equal(2, result.Data.TotalRequested);
        Assert.Equal(1, result.Data.SucceededCount);
        Assert.Equal(1, result.Data.FailedCount);

        var selfResult = result.Data.Results.Find(r => r.UserId == adminId);
        Assert.NotNull(selfResult);
        Assert.False(selfResult.Succeeded);
        Assert.Contains("cannot change your own status", selfResult.Message);
    }

    [Fact]
    public async Task GetUserByIdAsync_UserNotFound_ReturnsBadRequest()
    {
        var userId = Guid.NewGuid();
        _userManagerMock.Setup(m => m.FindByIdAsync(userId.ToString())).ReturnsAsync((ApplicationUser?)null);

        var service = CreateService();
        var result = await service.GetUserByIdAsync(userId);

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
    }

    [Fact]
    public async Task GetUserByIdAsync_ValidUser_MapsFieldsAndRoles()
    {
        var userId = Guid.NewGuid();
        var user = new ApplicationUser
        {
            Id = userId,
            FirstName = "David",
            LastName = "Miller",
            Email = "david@cocorra.com",
            Age = 28,
            MBTI = "ENFJ",
            Status = UserStatus.Active,
            VoiceVerificationPath = "voices/david.mp3"
        };
        _userManagerMock.Setup(m => m.FindByIdAsync(userId.ToString())).ReturnsAsync(user);
        _userManagerMock.Setup(m => m.GetRolesAsync(user)).ReturnsAsync(new List<string> { "Member" });

        var service = CreateService();
        var result = await service.GetUserByIdAsync(userId);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Data);
        Assert.Equal("David Miller", result.Data.FullName);
        Assert.Equal("https://api.cocorra.com/voices/david.mp3", result.Data.VoicePath);
        Assert.Contains("Member", result.Data.Roles);
    }

    [Fact]
    public async Task BlockDeviceAndEmailAsync_UserNotFound_ReturnsNotFound()
    {
        _userManagerMock.Setup(m => m.FindByEmailAsync("missing@cocorra.com")).ReturnsAsync((ApplicationUser?)null);

        var service = CreateService();
        var dto = new BlockDeviceAndEmailDto { Email = "missing@cocorra.com" };

        var result = await service.BlockDeviceAndEmailAsync(dto, Guid.NewGuid());

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.NotFound, result.StatusCode);
    }

    [Fact]
    public async Task BlockDeviceAndEmailAsync_ValidUser_BansUserAndBlocksDevice()
    {
        var userId = Guid.NewGuid();
        var user = new ApplicationUser
        {
            Id = userId,
            Email = "spammer@cocorra.com",
            RefreshToken = "token",
            FcmToken = "fcm"
        };
        _userManagerMock.Setup(m => m.FindByEmailAsync("spammer@cocorra.com")).ReturnsAsync(user);
        _userManagerMock.Setup(m => m.UpdateAsync(user)).ReturnsAsync(IdentityResult.Success);
        _blockedDevicesRepoMock.Setup(b => b.BlockAllDevicesForUserAsync(userId))
            .ReturnsAsync(2);

        var service = CreateService();
        var dto = new BlockDeviceAndEmailDto { Email = "spammer@cocorra.com" };

        var result = await service.BlockDeviceAndEmailAsync(dto, Guid.NewGuid());

        Assert.True(result.Succeeded);
        Assert.Equal(UserStatus.Banned, user.Status);
        Assert.Null(user.RefreshToken);
        Assert.Null(user.FcmToken);
        Assert.Equal(2, result.Data!.DevicesBlocked);

        // The devices come from the login registry, not the request. This assertion used to
        // check a device id carried on the DTO — which could only ever have been the acting
        // admin's own, the reason those fields were removed.
        _blockedDevicesRepoMock.Verify(b => b.BlockAllDevicesForUserAsync(userId), Times.Once);
    }

    [Fact]
    public async Task BlockDeviceAndEmailAsync_ReportsHonestly_WhenUserHasNoRegisteredDevices()
    {
        var userId = Guid.NewGuid();
        var user = new ApplicationUser { Id = userId, Email = "ghost@cocorra.com" };

        _userManagerMock.Setup(m => m.FindByEmailAsync("ghost@cocorra.com")).ReturnsAsync(user);
        _userManagerMock.Setup(m => m.UpdateAsync(user)).ReturnsAsync(IdentityResult.Success);
        _blockedDevicesRepoMock.Setup(b => b.BlockAllDevicesForUserAsync(userId)).ReturnsAsync(0);

        var service = CreateService();

        var result = await service.BlockDeviceAndEmailAsync(
            new BlockDeviceAndEmailDto { Email = "ghost@cocorra.com" }, Guid.NewGuid());

        // The ban still stands; only the device claim is withheld. A user who only ever signed
        // in from a client that omits X-Device-Id has nothing in the registry.
        Assert.True(result.Succeeded);
        Assert.Equal(UserStatus.Banned, user.Status);
        Assert.Equal(0, result.Data!.DevicesBlocked);
        Assert.Contains("No registered devices", result.Message);
    }

    [Fact]
    public async Task ChangeUserStatusAsync_TargetIsAdmin_ReturnsBadRequest()
    {
        var userId = Guid.NewGuid();
        var user = new ApplicationUser { Id = userId, Status = UserStatus.Active };
        _userManagerMock.Setup(m => m.FindByIdAsync(userId.ToString())).ReturnsAsync(user);
        _userManagerMock.Setup(m => m.IsInRoleAsync(user, "Admin")).ReturnsAsync(true);

        var service = CreateService();
        var result = await service.ChangeUserStatusAsync(userId, UserStatus.Banned, Guid.NewGuid());

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        Assert.Contains("Cannot perform actions on an Admin account", result.Message);
    }

    [Fact]
    public async Task BlockDeviceAndEmailAsync_TargetIsAdmin_ReturnsBadRequest()
    {
        var user = new ApplicationUser { Id = Guid.NewGuid(), Email = "admin@cocorra.com" };
        _userManagerMock.Setup(m => m.FindByEmailAsync("admin@cocorra.com")).ReturnsAsync(user);
        _userManagerMock.Setup(m => m.IsInRoleAsync(user, "Admin")).ReturnsAsync(true);

        var service = CreateService();
        var result = await service.BlockDeviceAndEmailAsync(
            new BlockDeviceAndEmailDto { Email = "admin@cocorra.com" }, Guid.NewGuid());

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        Assert.Contains("Cannot perform actions on an Admin account", result.Message);
    }

    [Fact]
    public async Task BroadcastNotificationAsync_MissingTitleOrBody_ReturnsBadRequest()
    {
        var service = CreateService();
        var adminId = Guid.NewGuid();

        var noTitle = await service.BroadcastNotificationAsync(new SendBroadcastNotificationDto { Title = "", Body = "Hello" }, adminId);
        Assert.False(noTitle.Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, noTitle.StatusCode);

        var noBody = await service.BroadcastNotificationAsync(new SendBroadcastNotificationDto { Title = "Title", Body = "" }, adminId);
        Assert.False(noBody.Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, noBody.StatusCode);
    }

    [Fact]
    public async Task BroadcastNotificationAsync_NoEligibleUsers_ReturnsSuccessWithZeroCounts()
    {
        var service = CreateService(out var db);
        var adminId = Guid.NewGuid();

        var result = await service.BroadcastNotificationAsync(
            new SendBroadcastNotificationDto { Title = "Broadcast", Body = "Hello Everyone" }, adminId);

        Assert.True(result.Succeeded);
        Assert.Equal(0, result.Data!.TotalTargetUsers);
        Assert.Equal(0, result.Data!.TokensCount);
        Assert.Equal(0, result.Data!.SuccessCount);
    }

    [Fact]
    public async Task BroadcastNotificationAsync_SuccessfulBroadcast_SendsMulticastAndSavesInAppNotifications()
    {
        var service = CreateService(out var db);
        var adminId = Guid.NewGuid();

        var user1 = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = "user1@test.com",
            Email = "user1@test.com",
            Status = UserStatus.Active,
            FcmToken = "fcm_token_1"
        };
        var user2 = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = "user2@test.com",
            Email = "user2@test.com",
            Status = UserStatus.Active,
            FcmToken = "fcm_token_2"
        };
        var bannedUser = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = "banned@test.com",
            Email = "banned@test.com",
            Status = UserStatus.Banned,
            FcmToken = "fcm_banned"
        };

        db.Users.AddRange(user1, user2, bannedUser);
        await db.SaveChangesAsync();

        _pushServiceMock
            .Setup(p => p.SendMulticastPushNotificationAsync(
                It.Is<IReadOnlyList<string>>(tokens => tokens.Count == 2 && tokens.Contains("fcm_token_1") && tokens.Contains("fcm_token_2")),
                "Important Update",
                "App will update tonight",
                null))
            .ReturnsAsync(new MulticastPushResult(2, 0, Array.Empty<string>()));

        var result = await service.BroadcastNotificationAsync(new SendBroadcastNotificationDto
        {
            Title = "Important Update",
            Body = "App will update tonight",
            SaveInAppNotification = true
        }, adminId);

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.Data!.TotalTargetUsers);
        Assert.Equal(2, result.Data!.TokensCount);
        Assert.Equal(2, result.Data!.SuccessCount);
        Assert.Equal(0, result.Data!.FailureCount);
        Assert.Equal(2, result.Data!.InAppNotificationsCreated);

        _notificationRepoMock.Verify(n => n.AddRangeAsync(
            It.Is<ICollection<Notification>>(notifs => notifs.Count == 2 && notifs.All(x => x.Title == "Important Update"))),
            Times.Once);
    }
}
