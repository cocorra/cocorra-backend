using System;
using System.Collections.Generic;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Cocorra.BLL.Services.AdminService;
using Cocorra.BLL.Services.AuditLogs;
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

public class AdminServiceAuditTests : IDisposable
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
    private readonly Mock<IAuditLogService> _auditLogMock = new();

    public void Dispose() => _host.Dispose();

    private AdminService CreateService()
    {
        var scope = _host.CreateScope();
        var db = (AppDbContext)scope.ServiceProvider.GetService(typeof(AppDbContext))!;
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
            _notificationRepoMock.Object,
            _auditLogMock.Object
        );
    }

    [Fact]
    public async Task ChangeUserStatusAsync_SuccessfulChange_RecordsOneUserStatusChangedAuditEntry()
    {
        var userId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var user = new ApplicationUser
        {
            Id = userId,
            Status = UserStatus.Pending,
            Email = "member@cocorra.com",
            FirstName = "Member"
        };

        _userManagerMock.Setup(m => m.FindByIdAsync(userId.ToString())).ReturnsAsync(user);
        _userManagerMock.Setup(m => m.IsInRoleAsync(user, "Admin")).ReturnsAsync(false);
        _userManagerMock.Setup(m => m.UpdateAsync(user)).ReturnsAsync(IdentityResult.Success);

        AuditEntry? capturedEntry = null;
        _auditLogMock.Setup(a => a.RecordAsync(It.IsAny<AuditEntry>()))
            .Callback<AuditEntry>(e => capturedEntry = e)
            .Returns(Task.CompletedTask);

        var service = CreateService();
        var result = await service.ChangeUserStatusAsync(userId, UserStatus.Active, adminId, isBulk: false);

        Assert.True(result.Succeeded);
        _auditLogMock.Verify(a => a.RecordAsync(It.IsAny<AuditEntry>()), Times.Once);

        Assert.NotNull(capturedEntry);
        Assert.Equal(AuditActions.UserStatusChanged, capturedEntry!.Action);
        Assert.Equal(adminId, capturedEntry.ActorUserId);
        Assert.Equal(userId, capturedEntry.TargetUserId);
        Assert.Equal("member@cocorra.com", capturedEntry.TargetDisplay);

        var json = JsonSerializer.Serialize(capturedEntry.Details, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("Pending", doc.RootElement.GetProperty("fromStatus").GetString());
        Assert.Equal("Active", doc.RootElement.GetProperty("toStatus").GetString());
        Assert.False(doc.RootElement.GetProperty("isBulk").GetBoolean());
    }

    [Fact]
    public async Task ChangeUserStatusAsync_TargetIsAdmin_RecordsNothing()
    {
        var userId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var user = new ApplicationUser { Id = userId, Status = UserStatus.Active };

        _userManagerMock.Setup(m => m.FindByIdAsync(userId.ToString())).ReturnsAsync(user);
        _userManagerMock.Setup(m => m.IsInRoleAsync(user, "Admin")).ReturnsAsync(true);

        var service = CreateService();
        var result = await service.ChangeUserStatusAsync(userId, UserStatus.Banned, adminId);

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        _auditLogMock.Verify(a => a.RecordAsync(It.IsAny<AuditEntry>()), Times.Never);
    }

    [Fact]
    public async Task ChangeUserStatusAsync_AlreadyInTargetStatus_RecordsNothing()
    {
        var userId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var user = new ApplicationUser { Id = userId, Status = UserStatus.Active };

        _userManagerMock.Setup(m => m.FindByIdAsync(userId.ToString())).ReturnsAsync(user);
        _userManagerMock.Setup(m => m.IsInRoleAsync(user, "Admin")).ReturnsAsync(false);

        var service = CreateService();
        var result = await service.ChangeUserStatusAsync(userId, UserStatus.Active, adminId);

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        _auditLogMock.Verify(a => a.RecordAsync(It.IsAny<AuditEntry>()), Times.Never);
    }

    [Fact]
    public async Task BulkChangeUserStatusAsync_OverTwoUsersAndAdminSelf_RecordsTwoEntriesWithIsBulkTrueAndSkipsAdminSelf()
    {
        var adminId = Guid.NewGuid();
        var user1Id = Guid.NewGuid();
        var user2Id = Guid.NewGuid();

        var user1 = new ApplicationUser { Id = user1Id, Status = UserStatus.Pending, Email = "u1@cocorra.com" };
        var user2 = new ApplicationUser { Id = user2Id, Status = UserStatus.Pending, Email = "u2@cocorra.com" };

        _userManagerMock.Setup(m => m.FindByIdAsync(user1Id.ToString())).ReturnsAsync(user1);
        _userManagerMock.Setup(m => m.FindByIdAsync(user2Id.ToString())).ReturnsAsync(user2);
        _userManagerMock.Setup(m => m.IsInRoleAsync(It.IsAny<ApplicationUser>(), "Admin")).ReturnsAsync(false);
        _userManagerMock.Setup(m => m.UpdateAsync(It.IsAny<ApplicationUser>())).ReturnsAsync(IdentityResult.Success);

        var capturedEntries = new List<AuditEntry>();
        _auditLogMock.Setup(a => a.RecordAsync(It.IsAny<AuditEntry>()))
            .Callback<AuditEntry>(e => capturedEntries.Add(e))
            .Returns(Task.CompletedTask);

        var service = CreateService();
        var dto = new BulkChangeStatusDto
        {
            UserIds = new List<Guid> { adminId, user1Id, user2Id },
            NewStatus = UserStatus.Active
        };

        var result = await service.BulkChangeUserStatusAsync(dto, adminId);

        Assert.True(result.Succeeded);
        Assert.Equal(2, capturedEntries.Count);
        _auditLogMock.Verify(a => a.RecordAsync(It.IsAny<AuditEntry>()), Times.Exactly(2));

        Assert.DoesNotContain(capturedEntries, e => e.TargetUserId == adminId);

        foreach (var entry in capturedEntries)
        {
            Assert.Equal(AuditActions.UserStatusChanged, entry.Action);
            Assert.Equal(adminId, entry.ActorUserId);

            var json = JsonSerializer.Serialize(entry.Details, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            using var doc = JsonDocument.Parse(json);
            Assert.True(doc.RootElement.GetProperty("isBulk").GetBoolean());
            Assert.Equal("Pending", doc.RootElement.GetProperty("fromStatus").GetString());
            Assert.Equal("Active", doc.RootElement.GetProperty("toStatus").GetString());
        }

        Assert.Contains(capturedEntries, e => e.TargetUserId == user1Id && e.TargetDisplay == "u1@cocorra.com");
        Assert.Contains(capturedEntries, e => e.TargetUserId == user2Id && e.TargetDisplay == "u2@cocorra.com");
    }

    [Fact]
    public async Task BlockDeviceAndEmailAsync_SuccessfulBanAndBlock_RecordsUserDeviceAndEmailBlockedAuditEntry()
    {
        var adminId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var user = new ApplicationUser
        {
            Id = userId,
            Email = "malicious@cocorra.com",
            Status = UserStatus.Active
        };

        _userManagerMock.Setup(m => m.FindByEmailAsync("malicious@cocorra.com")).ReturnsAsync(user);
        _userManagerMock.Setup(m => m.IsInRoleAsync(user, "Admin")).ReturnsAsync(false);
        _userManagerMock.Setup(m => m.UpdateAsync(user)).ReturnsAsync(IdentityResult.Success);
        _blockedDevicesRepoMock.Setup(b => b.BlockAllDevicesForUserAsync(userId)).ReturnsAsync(4);

        AuditEntry? capturedEntry = null;
        _auditLogMock.Setup(a => a.RecordAsync(It.IsAny<AuditEntry>()))
            .Callback<AuditEntry>(e => capturedEntry = e)
            .Returns(Task.CompletedTask);

        var service = CreateService();
        var dto = new BlockDeviceAndEmailDto { Email = "malicious@cocorra.com" };
        var result = await service.BlockDeviceAndEmailAsync(dto, adminId);

        Assert.True(result.Succeeded);
        _auditLogMock.Verify(a => a.RecordAsync(It.IsAny<AuditEntry>()), Times.Once);

        Assert.NotNull(capturedEntry);
        Assert.Equal(AuditActions.UserDeviceAndEmailBlocked, capturedEntry!.Action);
        Assert.Equal(adminId, capturedEntry.ActorUserId);
        Assert.Equal(userId, capturedEntry.TargetUserId);
        Assert.Equal("malicious@cocorra.com", capturedEntry.TargetDisplay);

        var json = JsonSerializer.Serialize(capturedEntry.Details, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("Active", doc.RootElement.GetProperty("fromStatus").GetString());
        Assert.Equal("Banned", doc.RootElement.GetProperty("toStatus").GetString());
        Assert.Equal(4, doc.RootElement.GetProperty("devicesBlocked").GetInt32());
    }
}
