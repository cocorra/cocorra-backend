using Cocorra.BLL.Services.AdminService;
using Cocorra.BLL.Services.AuthServices;
using Cocorra.BLL.Services.ChatService;
using Cocorra.BLL.Services.Email;
using Cocorra.BLL.Services.EventTracking;
using Cocorra.BLL.Services.NotificationService;
using Cocorra.BLL.Services.Upload;
using Cocorra.DAL.Data;
using Cocorra.DAL.DTOS.AdminDto;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;
using Cocorra.DAL.Repository.BlockedDevicesRepository;
using Cocorra.DAL.Repository.MessageRepository;
using Cocorra.DAL.Repository.NotificationRepository;
using Cocorra.DAL.Repository.RoomRepository;
using Cocorra.DAL.Repository.UserBlockRepository;
using Cocorra.DAL.Repository.UserRepository;
using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Cocorra.Tests;

public class FcmTokenLifecycleTests
{
    private static Mock<UserManager<ApplicationUser>> CreateMockUserManager()
    {
        var store = new Mock<IUserStore<ApplicationUser>>();
        return new Mock<UserManager<ApplicationUser>>(
            store.Object, null!, null!, null!, null!, null!, null!, null!, null!);
    }

    [Fact]
    public async Task RevokeTokenAsync_ClearsBothRefreshTokenAndFcmToken()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var user = new ApplicationUser
        {
            Id = userId,
            UserName = "testuser",
            RefreshToken = "existing_refresh_token",
            FcmToken = "device_fcm_token_123"
        };

        var userManagerMock = CreateMockUserManager();
        userManagerMock.Setup(m => m.FindByIdAsync(userId.ToString())).ReturnsAsync(user);
        userManagerMock.Setup(m => m.UpdateAsync(user)).ReturnsAsync(IdentityResult.Success);

        var authService = new AuthServices(
            userManagerMock.Object,
            null!,
            new Mock<IConfiguration>().Object,
            new Mock<IUploadVoice>().Object,
            new Mock<IEmailService>().Object,
            new Mock<IUploadImage>().Object,
            null!,
            new Mock<IRoomRepository>().Object,
            new Mock<IEventTracker>().Object
        );

        // Act
        var result = await authService.RevokeTokenAsync(userId);

        // Assert
        Assert.True(result.Succeeded);
        Assert.Null(user.RefreshToken);
        Assert.Null(user.FcmToken);
        userManagerMock.Verify(m => m.UpdateAsync(user), Times.Once);
    }

    [Fact]
    public async Task RevokeTokenAsync_ClearsFcmToken_EvenWhenRefreshTokenIsNull()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var user = new ApplicationUser
        {
            Id = userId,
            UserName = "testuser",
            RefreshToken = null,
            FcmToken = "device_fcm_token_123"
        };

        var userManagerMock = CreateMockUserManager();
        userManagerMock.Setup(m => m.FindByIdAsync(userId.ToString())).ReturnsAsync(user);
        userManagerMock.Setup(m => m.UpdateAsync(user)).ReturnsAsync(IdentityResult.Success);

        var authService = new AuthServices(
            userManagerMock.Object,
            null!,
            new Mock<IConfiguration>().Object,
            new Mock<IUploadVoice>().Object,
            new Mock<IEmailService>().Object,
            new Mock<IUploadImage>().Object,
            null!,
            new Mock<IRoomRepository>().Object,
            new Mock<IEventTracker>().Object
        );

        // Act
        var result = await authService.RevokeTokenAsync(userId);

        // Assert
        Assert.True(result.Succeeded);
        Assert.Null(user.FcmToken);
        userManagerMock.Verify(m => m.UpdateAsync(user), Times.Once);
    }

    [Fact]
    public async Task SaveMessageAsync_DoesNotThrow_WhenPushServiceFails()
    {
        // Arrange
        var senderId = Guid.NewGuid();
        var receiverId = Guid.NewGuid();
        var sender = new ApplicationUser { Id = senderId, FirstName = "Alice", LastName = "Smith" };
        var receiver = new ApplicationUser { Id = receiverId, FirstName = "Bob", LastName = "Jones", FcmToken = "receiver_token" };

        var userManagerMock = CreateMockUserManager();
        userManagerMock.Setup(m => m.FindByIdAsync(senderId.ToString())).ReturnsAsync(sender);
        userManagerMock.Setup(m => m.FindByIdAsync(receiverId.ToString())).ReturnsAsync(receiver);

        var messageRepoMock = new Mock<IMessageRepository>();
        var userBlockRepoMock = new Mock<IUserBlockRepository>();
        userBlockRepoMock.Setup(b => b.IsBlockedAsync(senderId, receiverId)).ReturnsAsync(false);

        var pushServiceMock = new Mock<IPushNotificationService>();
        pushServiceMock.Setup(p => p.SendPushNotificationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Dictionary<string, string>>()))
            .ThrowsAsync(new Exception("FCM Network Error"));

        var loggerMock = new Mock<ILogger<ChatService>>();

        var chatService = new ChatService(
            userBlockRepoMock.Object,
            messageRepoMock.Object,
            pushServiceMock.Object,
            userManagerMock.Object,
            new Mock<IMediator>().Object,
            new Mock<IEventTracker>().Object,
            loggerMock.Object
        );

        // Act
        var response = await chatService.SaveMessageAsync(senderId, receiverId, "Hello Bob!");

        // Assert
        Assert.True(response.Succeeded);
        Assert.NotNull(response.Data);
        Assert.Equal("Hello Bob!", response.Data.Content);
    }

    [Fact]
    public async Task ChangeUserStatusAsync_ClearsFcmToken_WhenUserIsBanned()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var user = new ApplicationUser
        {
            Id = userId,
            Status = UserStatus.Active,
            FcmToken = "fcm_token_to_clear"
        };

        var userManagerMock = CreateMockUserManager();
        userManagerMock.Setup(m => m.FindByIdAsync(userId.ToString())).ReturnsAsync(user);
        userManagerMock.Setup(m => m.UpdateAsync(user)).ReturnsAsync(IdentityResult.Success);
        userManagerMock.Setup(m => m.SetLockoutEnabledAsync(user, true)).ReturnsAsync(IdentityResult.Success);
        userManagerMock.Setup(m => m.SetLockoutEndDateAsync(user, It.IsAny<DateTimeOffset?>())).ReturnsAsync(IdentityResult.Success);

        var pushServiceMock = new Mock<IPushNotificationService>();
        var notificationRepoMock = new Mock<INotificationRepository>();
        var blockedDevicesRepoMock = new Mock<IBlockedDevicesRepository>();
        var configMock = new Mock<IConfiguration>();
        configMock.Setup(c => c["AppSettings:BaseUrl"]).Returns("https://api.test.com");

        var adminService = new AdminService(
            userManagerMock.Object,
            new Mock<IUploadVoice>().Object,
            configMock.Object,
            new Mock<IEmailService>().Object,
            new Mock<IUserRepository>().Object,
            pushServiceMock.Object,
            blockedDevicesRepoMock.Object,
            new Mock<IEventTracker>().Object,
            null!,
            notificationRepoMock.Object
        );

        // Act
        var result = await adminService.ChangeUserStatusAsync(userId, UserStatus.Banned);

        // Assert
        Assert.True(result.Succeeded);
        // Verify push was sent with original token
        pushServiceMock.Verify(p => p.SendPushNotificationAsync(
            "fcm_token_to_clear", "", "", It.Is<Dictionary<string, string>>(d => d["type"] == "account_locked")),
            Times.Once);
        // Verify token was wiped afterwards
        Assert.Null(user.FcmToken);
    }

    [Fact]
    public async Task BlockDeviceAndEmailAsync_ClearsFcmToken_WhenBanningUser()
    {
        // Arrange
        var email = "badactor@test.com";
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            Email = email,
            Status = UserStatus.Active,
            FcmToken = "stale_fcm_token",
            RefreshToken = "some_refresh_token"
        };

        var userManagerMock = CreateMockUserManager();
        userManagerMock.Setup(m => m.FindByEmailAsync(email)).ReturnsAsync(user);
        userManagerMock.Setup(m => m.UpdateAsync(user)).ReturnsAsync(IdentityResult.Success);
        userManagerMock.Setup(m => m.SetLockoutEnabledAsync(user, true)).ReturnsAsync(IdentityResult.Success);
        userManagerMock.Setup(m => m.SetLockoutEndDateAsync(user, It.IsAny<DateTimeOffset?>())).ReturnsAsync(IdentityResult.Success);

        var blockedDevicesRepoMock = new Mock<IBlockedDevicesRepository>();
        blockedDevicesRepoMock.Setup(b => b.GetByDeviceIdAsync(It.IsAny<string>())).ReturnsAsync((BlockedDevices?)null);

        var configMock = new Mock<IConfiguration>();
        configMock.Setup(c => c["AppSettings:BaseUrl"]).Returns("https://api.test.com");

        var adminService = new AdminService(
            userManagerMock.Object,
            new Mock<IUploadVoice>().Object,
            configMock.Object,
            new Mock<IEmailService>().Object,
            new Mock<IUserRepository>().Object,
            new Mock<IPushNotificationService>().Object,
            blockedDevicesRepoMock.Object,
            new Mock<IEventTracker>().Object,
            null!,
            new Mock<INotificationRepository>().Object
        );

        var dto = new BlockDeviceAndEmailDto
        {
            Email = email,
            DeviceId = "device_xyz_123",
            DeviceName = "Pixel 7",
            DeviceModel = "Google",
            DeviceType = "Android",
            DeviceOs = "14"
        };

        // Act
        var result = await adminService.BlockDeviceAndEmailAsync(dto);

        // Assert
        Assert.True(result.Succeeded);
        Assert.Null(user.FcmToken);
        Assert.Null(user.RefreshToken);
        Assert.Equal(UserStatus.Banned, user.Status);
    }
}
