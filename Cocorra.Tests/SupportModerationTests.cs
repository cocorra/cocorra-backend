using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Cocorra.BLL.Services.AuditLogs;
using Cocorra.BLL.Services.EventTracking;
using Cocorra.BLL.Services.NotificationService;
using Cocorra.BLL.Services.RealTimeNotifier;
using Cocorra.BLL.Services.Storage;
using Cocorra.BLL.Services.SupportService;
using Cocorra.BLL.Services.Upload;
using Cocorra.DAL.Data;
using Cocorra.DAL.DTOS.ReportDto;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;
using Cocorra.DAL.Repository.AuditLogRepository;
using Cocorra.DAL.Repository.NotificationRepository;
using Cocorra.DAL.Repository.SupportRepository;
using Cocorra.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Cocorra.Tests;

public class SupportModerationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _services;
    private readonly FakePrivateFileUrlIssuer _fileUrls = new();
    private readonly Mock<IUploadImage> _uploadImageMock = new();
    private readonly Mock<IRealTimeNotifier> _realTimeNotifierMock = new();
    private readonly Mock<IPushNotificationService> _pushServiceMock = new();
    private readonly Mock<IEventTracker> _eventTrackerMock = new();
    private readonly Mock<IHttpContextAccessor> _httpContextAccessorMock = new();

    public SupportModerationTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var serviceCollection = new ServiceCollection();
        serviceCollection.AddLogging();
        serviceCollection.AddDbContext<AppDbContext>(options => options.UseSqlite(_connection));
        serviceCollection.AddIdentity<ApplicationUser, IdentityRole<Guid>>(options =>
        {
            options.User.RequireUniqueEmail = false;
            options.Lockout.AllowedForNewUsers = false;
        })
        .AddEntityFrameworkStores<AppDbContext>()
        .AddDefaultTokenProviders();

        _services = serviceCollection.BuildServiceProvider();

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Database.EnsureCreated();

        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        foreach (var role in AppRoles.All)
        {
            roleManager.CreateAsync(new IdentityRole<Guid>(role)).GetAwaiter().GetResult();
        }
    }

    public void Dispose()
    {
        _services.Dispose();
        _connection.Dispose();
    }

    private (IServiceScope Scope, SupportService Service, AppDbContext Db, UserManager<ApplicationUser> UserManager) CreateHarness()
    {
        var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var supportRepo = new SupportRepository(db);
        var notificationRepo = new NotificationRepository(db);
        var auditLogService = new AuditLogService(
            new AuditLogRepository(db),
            _httpContextAccessorMock.Object,
            NullLogger<AuditLogService>.Instance);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AppSettings:BaseUrl"] = "https://api.example.com"
            })
            .Build();

        var service = new SupportService(
            supportRepo,
            _uploadImageMock.Object,
            userManager,
            notificationRepo,
            _realTimeNotifierMock.Object,
            _pushServiceMock.Object,
            _eventTrackerMock.Object,
            NullLogger<SupportService>.Instance,
            _fileUrls,
            config,
            auditLogService);

        return (scope, service, db, userManager);
    }

    private async Task<ApplicationUser> CreateUserWithRoleAsync(
        string role,
        string? emailPrefix = null,
        UserStatus status = UserStatus.Active,
        string? firstName = null,
        string? lastName = null) =>
        await CreateUserWithRolesAsync(new[] { role }, emailPrefix, status, firstName, lastName);

    private async Task<ApplicationUser> CreateUserWithRolesAsync(
        IEnumerable<string> roles,
        string? emailPrefix = null,
        UserStatus status = UserStatus.Active,
        string? firstName = null,
        string? lastName = null)
    {
        using var scope = _services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var roleList = roles.ToList();
        var primaryRole = roleList[0];
        var id = Guid.NewGuid();
        var tag = emailPrefix ?? primaryRole.ToLowerInvariant();
        var user = new ApplicationUser
        {
            Id = id,
            UserName = $"{tag}-{id:N}",
            Email = $"{tag}-{id:N}@cocorra.test",
            FirstName = firstName ?? primaryRole,
            LastName = lastName ?? "Account",
            Status = status,
            RefreshToken = $"refresh-{id:N}",
            FcmToken = $"fcm-{id:N}"
        };

        var createResult = await userManager.CreateAsync(user);
        Assert.True(createResult.Succeeded);

        foreach (var role in roleList)
        {
            var roleResult = await userManager.AddToRoleAsync(user, role);
            Assert.True(roleResult.Succeeded);
        }

        return (await userManager.FindByIdAsync(id.ToString()))!;
    }

    private async Task<Room> SeedRoomAsync(Guid hostId, string title = "Voice Session")
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var room = new Room
        {
            Id = Guid.NewGuid(),
            HostId = hostId,
            RoomTitle = title,
            Category = RoomCategory.MentalHealth,
            Status = RoomStatus.Live,
            StartDate = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow
        };

        db.Rooms.Add(room);
        await db.SaveChangesAsync();
        return room;
    }

    private async Task<Report> SeedReportAsync(
        Guid reporterId,
        Guid? reportedUserId,
        ReportCategory category = ReportCategory.Harassment,
        string status = "Open",
        ReportStatus statusCode = ReportStatus.Open,
        string? screenshotPath = null,
        DateTime? createdAt = null,
        Guid? reportedRoomId = null)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var report = new Report
        {
            Id = Guid.NewGuid(),
            ReporterId = reporterId,
            ReportedUserId = reportedUserId,
            ReportedRoomId = reportedRoomId,
            Category = category,
            Description = "Reported behavior",
            ScreenshotPath = screenshotPath,
            Status = status,
            StatusCode = statusCode,
            CreatedAt = createdAt ?? DateTime.UtcNow
        };

        db.Reports.Add(report);
        await db.SaveChangesAsync();
        return report;
    }

    private async Task<SupportTicket> SeedTicketAsync(
        Guid? userId,
        string? contactEmail,
        SupportTicketType type = SupportTicketType.TechnicalProblem,
        SupportTicketStatus status = SupportTicketStatus.Open,
        DateTime? createdAt = null,
        string? screenshotPath = null)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var ticket = new SupportTicket
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ContactEmail = contactEmail,
            Type = type,
            Message = "Need assistance",
            ScreenshotPath = screenshotPath,
            Status = status.ToString(),
            StatusCode = status,
            CreatedAt = createdAt ?? DateTime.UtcNow
        };

        db.SupportTickets.Add(ticket);
        await db.SaveChangesAsync();
        return ticket;
    }

    private async Task<List<AuditLog>> ReadAuditLogsAsync()
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.AuditLogs.AsNoTracking().OrderBy(a => a.Id).ToListAsync();
    }

    private async Task<Report> ReadReportAsync(Guid reportId)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Reports.AsNoTracking().SingleAsync(r => r.Id == reportId);
    }

    private async Task<SupportTicket> ReadTicketAsync(Guid ticketId)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.SupportTickets.AsNoTracking().SingleAsync(t => t.Id == ticketId);
    }

    private async Task<ApplicationUser> ReadUserAsync(Guid userId)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId);
    }

    private async Task<int> CountNotificationsAsync(Guid userId)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Notifications.CountAsync(n => n.UserId == userId);
    }

    [Fact]
    public async Task GetFilteredReportsAsync_FiltersAndPagesInSql_ReturnsEmptyBeyondEnd()
    {
        var reporter = await CreateUserWithRoleAsync(AppRoles.User, "reporter");
        var target = await CreateUserWithRoleAsync(AppRoles.User, "target");
        var baseTime = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

        var r1 = await SeedReportAsync(reporter.Id, target.Id, ReportCategory.Harassment, "Open", ReportStatus.Open, createdAt: baseTime.AddMinutes(1));
        var r2 = await SeedReportAsync(reporter.Id, target.Id, ReportCategory.Harassment, "Open", ReportStatus.Open, createdAt: baseTime.AddMinutes(2));
        var r3 = await SeedReportAsync(reporter.Id, target.Id, ReportCategory.Harassment, "Open", ReportStatus.Open, createdAt: baseTime.AddMinutes(3));
        await SeedReportAsync(reporter.Id, target.Id, ReportCategory.Harassment, "Resolved", ReportStatus.Resolved, createdAt: baseTime.AddMinutes(4));
        await SeedReportAsync(reporter.Id, target.Id, ReportCategory.Spam, "Open", ReportStatus.Open, createdAt: baseTime.AddMinutes(5));

        var (scope, service, _, _) = CreateHarness();
        using (scope)
        {
            var page1 = await service.GetFilteredReportsAsync(ReportCategory.Harassment, "Open", pageNumber: 1, pageSize: 2);
            var page2 = await service.GetFilteredReportsAsync(ReportCategory.Harassment, "Open", pageNumber: 2, pageSize: 2);
            var pageBeyond = await service.GetFilteredReportsAsync(ReportCategory.Harassment, "Open", pageNumber: 3, pageSize: 2);

            Assert.Equal(3, page1.TotalCount);
            Assert.Equal(2, page1.TotalPages);
            Assert.True(page1.HasNextPage);
            Assert.False(page1.HasPreviousPage);
            Assert.Equal(new[] { r3.Id, r2.Id }, page1.Data!.Select(x => x.Id));

            Assert.Equal(3, page2.TotalCount);
            Assert.False(page2.HasNextPage);
            Assert.True(page2.HasPreviousPage);
            Assert.Equal(new[] { r1.Id }, page2.Data!.Select(x => x.Id));

            Assert.Equal(3, pageBeyond.TotalCount);
            Assert.Empty(pageBeyond.Data!);
            Assert.False(pageBeyond.HasNextPage);
            Assert.True(pageBeyond.HasPreviousPage);
        }
    }

    [Theory]
    [InlineData(1, 0, 1, 1)]
    [InlineData(1, -1, 1, 1)]
    [InlineData(1, 101, 1, 100)]
    [InlineData(0, 20, 1, 20)]
    [InlineData(-4, 500, 1, 100)]
    public async Task GetFilteredReportsAsync_OutOfRangePaging_ClampsPageSizeTo1Through100(
        int requestedPage, int requestedSize, int expectedPage, int expectedSize)
    {
        var reporter = await CreateUserWithRoleAsync(AppRoles.User, "reporter");
        var target = await CreateUserWithRoleAsync(AppRoles.User, "target");
        await SeedReportAsync(reporter.Id, target.Id);
        await SeedReportAsync(reporter.Id, target.Id);

        var (scope, service, _, _) = CreateHarness();
        using (scope)
        {
            var result = await service.GetFilteredReportsAsync(null, null, requestedPage, requestedSize);

            Assert.True(result.Succeeded);
            Assert.Equal(expectedPage, result.CurrentPage);
            Assert.Equal(expectedSize, result.PageSize);
            Assert.Equal(2, result.TotalCount);
            Assert.Equal(Math.Min(2, expectedSize), result.Data!.Count());
        }
    }

    [Fact]
    public async Task GetReportByIdAsync_MissingReport_ReturnsNotFoundAndWritesNoAudit()
    {
        var (scope, service, _, _) = CreateHarness();
        using (scope)
        {
            var result = await service.GetReportByIdAsync(Guid.NewGuid(), Guid.NewGuid(), callerIsAdmin: false);

            Assert.False(result.Succeeded);
            Assert.Equal(HttpStatusCode.NotFound, result.StatusCode);
        }

        Assert.Empty(await ReadAuditLogsAsync());
    }

    [Fact]
    public async Task GetReportByIdAsync_ExistingReport_ReturnsDetailsAndPersistsReportViewedAudit()
    {
        var reporter = await CreateUserWithRoleAsync(AppRoles.User, "reporter");
        var reported = await CreateUserWithRoleAsync(AppRoles.User, "reported");
        var callerId = Guid.NewGuid();
        var report = await SeedReportAsync(reporter.Id, reported.Id, screenshotPath: "Uploads/img/Support/shot.png");

        var (scope, service, _, _) = CreateHarness();
        using (scope)
        {
            var result = await service.GetReportByIdAsync(report.Id, callerId, callerIsAdmin: false);

            Assert.True(result.Succeeded);
            Assert.NotNull(result.Data);
            Assert.Equal(report.Id, result.Data!.Id);
            Assert.Equal(reported.Id, result.Data.ReportedUserId);
            Assert.True(result.Data.HasScreenshot);
            Assert.Null(result.Data.ScreenshotPath);
        }

        var audit = Assert.Single(await ReadAuditLogsAsync());
        Assert.Equal(AuditActions.ReportViewed, audit.Action);
        Assert.Equal(AuditCategories.Report, audit.Category);
        Assert.Equal(AuditEntityTypes.Report, audit.EntityType);
        Assert.Equal(report.Id.ToString(), audit.EntityId);
        Assert.Equal(callerId, audit.ActorUserId);
        Assert.Equal(reported.Id, audit.TargetUserId);
    }

    public static IEnumerable<object?[]> InvalidReasons =>
        new List<object?[]>
        {
            new object?[] { null },
            new object?[] { "" },
            new object?[] { "   " },
            new object?[] { "ab" },
            new object?[] { "  ab  " },
            new object?[] { new string('x', 1001) }
        };

    [Theory]
    [MemberData(nameof(InvalidReasons))]
    public async Task UpdateReportStatusAsync_InvalidReason_ReturnsBadRequestLeavesReportAndAuditUntouched(string? invalidReason)
    {
        var reporter = await CreateUserWithRoleAsync(AppRoles.User, "reporter");
        var reported = await CreateUserWithRoleAsync(AppRoles.User, "reported");
        var report = await SeedReportAsync(reporter.Id, reported.Id);

        var (scope, service, _, _) = CreateHarness();
        using (scope)
        {
            var result = await service.UpdateReportStatusAsync(report.Id, "Resolved", invalidReason, Guid.NewGuid(), callerIsAdmin: true);

            Assert.False(result.Succeeded);
            Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        }

        var persisted = await ReadReportAsync(report.Id);
        Assert.Equal("Open", persisted.Status);
        Assert.Equal(ReportStatus.Open, persisted.StatusCode);
        Assert.Null(persisted.ResolvedAt);
        Assert.Empty(await ReadAuditLogsAsync());
    }

    [Theory]
    [InlineData("  abc  ", "abc")]
    [InlineData("VALID_1000", "VALID_1000")]
    public async Task UpdateReportStatusAsync_BoundaryReasons_PersistsReportAndAuditRow(string rawReason, string expectedReason)
    {
        if (rawReason == "VALID_1000")
        {
            rawReason = new string('r', 1000);
            expectedReason = rawReason;
        }

        var reporter = await CreateUserWithRoleAsync(AppRoles.User, "reporter");
        var reported = await CreateUserWithRoleAsync(AppRoles.User, "reported");
        var callerId = Guid.NewGuid();
        var report = await SeedReportAsync(reporter.Id, reported.Id);

        var (scope, service, _, _) = CreateHarness();
        using (scope)
        {
            var result = await service.UpdateReportStatusAsync(report.Id, "Resolved", rawReason, callerId, callerIsAdmin: true);

            Assert.True(result.Succeeded);
            Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        }

        var persisted = await ReadReportAsync(report.Id);
        Assert.Equal("Resolved", persisted.Status);
        Assert.Equal(ReportStatus.Resolved, persisted.StatusCode);
        Assert.NotNull(persisted.ResolvedAt);
        Assert.NotNull(persisted.UpdatedAt);

        var audit = Assert.Single(await ReadAuditLogsAsync());
        Assert.Equal(AuditActions.ReportStatusChanged, audit.Action);
        Assert.Equal(AuditCategories.Report, audit.Category);
        Assert.Equal(AuditEntityTypes.Report, audit.EntityType);
        Assert.Equal(report.Id.ToString(), audit.EntityId);
        Assert.Equal(callerId, audit.ActorUserId);
        Assert.Equal(reported.Id, audit.TargetUserId);
        Assert.Equal("Open", audit.FromState);
        Assert.Equal("Resolved", audit.ToState);
        Assert.Equal(expectedReason, audit.Reason);
    }

    [Theory]
    [InlineData("Open")]
    [InlineData("open")]
    public async Task UpdateReportStatusAsync_UnchangedStatus_ReturnsBadRequestAndWritesNoAudit(string unchangedStatus)
    {
        var reporter = await CreateUserWithRoleAsync(AppRoles.User, "reporter");
        var reported = await CreateUserWithRoleAsync(AppRoles.User, "reported");
        var report = await SeedReportAsync(reporter.Id, reported.Id);

        var (scope, service, _, _) = CreateHarness();
        using (scope)
        {
            var result = await service.UpdateReportStatusAsync(report.Id, unchangedStatus, "Valid reason text", Guid.NewGuid(), callerIsAdmin: true);

            Assert.False(result.Succeeded);
            Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        }

        var persisted = await ReadReportAsync(report.Id);
        Assert.Equal("Open", persisted.Status);
        Assert.Null(persisted.UpdatedAt);
        Assert.Empty(await ReadAuditLogsAsync());
    }

    public static IEnumerable<object[]> ModeratorProtectedTargetsAndActions()
    {
        var protectedStaffRoles = new[] { AppRoles.Moderator, AppRoles.RecordReviewer, AppRoles.Coach };
        var actions = new[] { AdminReportAction.WarnUser, AdminReportAction.Mute24h, AdminReportAction.BanUser, AdminReportAction.RejectReport };

        foreach (var role in protectedStaffRoles)
        {
            foreach (var action in actions)
            {
                yield return new object[] { role, action };
            }
        }
    }

    [Theory]
    [MemberData(nameof(ModeratorProtectedTargetsAndActions))]
    public async Task TakeActionOnReportAsync_ModeratorAgainstProtectedStaffRole_ReturnsGenericForbiddenAndLeavesStateUntouched(
        string targetRole, AdminReportAction action)
    {
        var reporter = await CreateUserWithRoleAsync(AppRoles.User, "reporter");
        var target = await CreateUserWithRoleAsync(targetRole);
        var originalStamp = target.SecurityStamp;
        var originalRefresh = target.RefreshToken;
        var originalFcm = target.FcmToken;
        var report = await SeedReportAsync(reporter.Id, target.Id);

        var (scope, service, _, _) = CreateHarness();
        using (scope)
        {
            var dto = new TakeReportActionDto
            {
                Action = action,
                Reason = "Attempted moderator action on protected staff"
            };

            var result = await service.TakeActionOnReportAsync(report.Id, dto, Guid.NewGuid(), callerIsAdmin: false);

            Assert.False(result.Succeeded);
            Assert.Equal(HttpStatusCode.Forbidden, result.StatusCode);
            Assert.Equal("Moderators cannot take actions against staff accounts.", result.Message);
        }

        var persistedReport = await ReadReportAsync(report.Id);
        Assert.Equal("Open", persistedReport.Status);
        Assert.Equal(ReportStatus.Open, persistedReport.StatusCode);
        Assert.Null(persistedReport.ResolvedAt);

        var persistedUser = await ReadUserAsync(target.Id);
        Assert.Equal(UserStatus.Active, persistedUser.Status);
        Assert.False(persistedUser.LockoutEnabled);
        Assert.Null(persistedUser.LockoutEnd);
        Assert.Equal(originalStamp, persistedUser.SecurityStamp);
        Assert.Equal(originalRefresh, persistedUser.RefreshToken);
        Assert.Equal(originalFcm, persistedUser.FcmToken);

        Assert.Equal(0, await CountNotificationsAsync(target.Id));
        Assert.Empty(await ReadAuditLogsAsync());
        _realTimeNotifierMock.Verify(n => n.ForceLogoutAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
        _pushServiceMock.Verify(p => p.SendPushNotificationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Dictionary<string, string>>()), Times.Never);
    }

    [Theory]
    [InlineData(AdminReportAction.WarnUser)]
    [InlineData(AdminReportAction.Mute24h)]
    [InlineData(AdminReportAction.BanUser)]
    [InlineData(AdminReportAction.RejectReport)]
    public async Task TakeActionOnReportAsync_AdminAgainstAdmin_ReturnsForbiddenAndLeavesStateUntouched(AdminReportAction action)
    {
        var reporter = await CreateUserWithRoleAsync(AppRoles.User, "reporter");
        var targetAdmin = await CreateUserWithRoleAsync(AppRoles.Admin);
        var originalStamp = targetAdmin.SecurityStamp;
        var report = await SeedReportAsync(reporter.Id, targetAdmin.Id);

        var (scope, service, _, _) = CreateHarness();
        using (scope)
        {
            var dto = new TakeReportActionDto
            {
                Action = action,
                Reason = "Attempted action on admin"
            };

            var result = await service.TakeActionOnReportAsync(report.Id, dto, Guid.NewGuid(), callerIsAdmin: true);

            Assert.False(result.Succeeded);
            Assert.Equal(HttpStatusCode.Forbidden, result.StatusCode);
        }

        var persistedReport = await ReadReportAsync(report.Id);
        Assert.Equal("Open", persistedReport.Status);
        Assert.Null(persistedReport.ResolvedAt);

        var persistedUser = await ReadUserAsync(targetAdmin.Id);
        Assert.Equal(UserStatus.Active, persistedUser.Status);
        Assert.Null(persistedUser.LockoutEnd);
        Assert.Equal(originalStamp, persistedUser.SecurityStamp);

        Assert.Equal(0, await CountNotificationsAsync(targetAdmin.Id));
        Assert.Empty(await ReadAuditLogsAsync());
        _realTimeNotifierMock.Verify(n => n.ForceLogoutAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData(AdminReportAction.WarnUser, ReportStatus.Resolved)]
    [InlineData(AdminReportAction.Mute24h, ReportStatus.Resolved)]
    [InlineData(AdminReportAction.BanUser, ReportStatus.Resolved)]
    [InlineData(AdminReportAction.RejectReport, ReportStatus.Rejected)]
    public async Task TakeActionOnReportAsync_ModeratorAgainstPlainUser_AppliesActionAndPersistsAudit(
        AdminReportAction action, ReportStatus expectedReportStatus)
    {
        var reporter = await CreateUserWithRoleAsync(AppRoles.User, "reporter");
        var plainUser = await CreateUserWithRoleAsync(AppRoles.User, "plain");
        var originalStamp = plainUser.SecurityStamp;
        var moderatorId = Guid.NewGuid();
        var report = await SeedReportAsync(reporter.Id, plainUser.Id);

        var (scope, service, _, _) = CreateHarness();
        using (scope)
        {
            var dto = new TakeReportActionDto
            {
                Action = action,
                AdminNote = "Reviewed chat transcript",
                Reason = "  Confirmed community guideline violation  "
            };

            var result = await service.TakeActionOnReportAsync(report.Id, dto, moderatorId, callerIsAdmin: false);

            Assert.True(result.Succeeded);
            Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        }

        var persistedReport = await ReadReportAsync(report.Id);
        Assert.Equal(expectedReportStatus, persistedReport.StatusCode);
        Assert.Equal(expectedReportStatus.ToString(), persistedReport.Status);
        Assert.NotNull(persistedReport.ResolvedAt);

        var persistedUser = await ReadUserAsync(plainUser.Id);
        switch (action)
        {
            case AdminReportAction.WarnUser:
                Assert.Equal(UserStatus.Active, persistedUser.Status);
                Assert.Equal(1, await CountNotificationsAsync(plainUser.Id));
                _realTimeNotifierMock.Verify(n => n.ForceLogoutAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
                break;

            case AdminReportAction.Mute24h:
                Assert.Equal(UserStatus.Active, persistedUser.Status);
                Assert.True(persistedUser.LockoutEnabled);
                Assert.NotNull(persistedUser.LockoutEnd);
                Assert.True(persistedUser.LockoutEnd > DateTimeOffset.UtcNow.AddHours(23));
                Assert.Equal(1, await CountNotificationsAsync(plainUser.Id));
                _realTimeNotifierMock.Verify(n => n.ForceLogoutAsync(plainUser.Id, It.IsAny<string>()), Times.Once);
                break;

            case AdminReportAction.BanUser:
                Assert.Equal(UserStatus.Banned, persistedUser.Status);
                Assert.True(persistedUser.LockoutEnabled);
                Assert.NotNull(persistedUser.LockoutEnd);
                Assert.True(persistedUser.LockoutEnd > DateTimeOffset.UtcNow.AddYears(99));
                Assert.Null(persistedUser.RefreshToken);
                Assert.Null(persistedUser.FcmToken);
                Assert.NotEqual(originalStamp, persistedUser.SecurityStamp);
                Assert.Equal(1, await CountNotificationsAsync(plainUser.Id));
                _realTimeNotifierMock.Verify(n => n.ForceLogoutAsync(plainUser.Id, It.IsAny<string>()), Times.Once);
                break;

            case AdminReportAction.RejectReport:
                Assert.Equal(UserStatus.Active, persistedUser.Status);
                Assert.Equal(originalStamp, persistedUser.SecurityStamp);
                Assert.Equal(0, await CountNotificationsAsync(plainUser.Id));
                break;
        }

        var audit = Assert.Single(await ReadAuditLogsAsync());
        Assert.Equal(AuditActions.ReportActionTaken, audit.Action);
        Assert.Equal(AuditCategories.Report, audit.Category);
        Assert.Equal(AuditEntityTypes.Report, audit.EntityType);
        Assert.Equal(report.Id.ToString(), audit.EntityId);
        Assert.Equal(moderatorId, audit.ActorUserId);
        Assert.Equal(plainUser.Id, audit.TargetUserId);
        Assert.Equal("Open", audit.FromState);
        Assert.Equal(action.ToString(), audit.ToState);
        Assert.Equal("Confirmed community guideline violation", audit.Reason);
    }

    [Fact]
    public async Task TakeActionOnReportAsync_AdminAgainstModerator_BanSucceedsRotatesStampAndForcesLogout()
    {
        var reporter = await CreateUserWithRoleAsync(AppRoles.User, "reporter");
        var targetModerator = await CreateUserWithRoleAsync(AppRoles.Moderator);
        var originalStamp = targetModerator.SecurityStamp;
        var adminId = Guid.NewGuid();
        var report = await SeedReportAsync(reporter.Id, targetModerator.Id);

        var (scope, service, _, _) = CreateHarness();
        using (scope)
        {
            var dto = new TakeReportActionDto
            {
                Action = AdminReportAction.BanUser,
                Reason = "Abuse of moderator privileges"
            };

            var result = await service.TakeActionOnReportAsync(report.Id, dto, adminId, callerIsAdmin: true);

            Assert.True(result.Succeeded);
            Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        }

        var persistedUser = await ReadUserAsync(targetModerator.Id);
        Assert.Equal(UserStatus.Banned, persistedUser.Status);
        Assert.NotEqual(originalStamp, persistedUser.SecurityStamp);
        Assert.Null(persistedUser.RefreshToken);
        _realTimeNotifierMock.Verify(n => n.ForceLogoutAsync(targetModerator.Id, It.IsAny<string>()), Times.Once);

        var audit = Assert.Single(await ReadAuditLogsAsync());
        Assert.Equal(AuditActions.ReportActionTaken, audit.Action);
        Assert.Equal(adminId, audit.ActorUserId);
        Assert.Equal(targetModerator.Id, audit.TargetUserId);
        Assert.Equal("Open", audit.FromState);
        Assert.Equal(nameof(AdminReportAction.BanUser), audit.ToState);
        Assert.Equal("Abuse of moderator privileges", audit.Reason);
    }

    [Theory]
    [MemberData(nameof(InvalidReasons))]
    public async Task TakeActionOnReportAsync_InvalidReason_ReturnsBadRequestLeavesEverythingUntouched(string? invalidReason)
    {
        var reporter = await CreateUserWithRoleAsync(AppRoles.User, "reporter");
        var target = await CreateUserWithRoleAsync(AppRoles.User, "target");
        var originalStamp = target.SecurityStamp;
        var report = await SeedReportAsync(reporter.Id, target.Id);

        var (scope, service, _, _) = CreateHarness();
        using (scope)
        {
            var dto = new TakeReportActionDto
            {
                Action = AdminReportAction.BanUser,
                Reason = invalidReason
            };

            var result = await service.TakeActionOnReportAsync(report.Id, dto, Guid.NewGuid(), callerIsAdmin: true);

            Assert.False(result.Succeeded);
            Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        }

        var persistedReport = await ReadReportAsync(report.Id);
        Assert.Equal("Open", persistedReport.Status);

        var persistedUser = await ReadUserAsync(target.Id);
        Assert.Equal(UserStatus.Active, persistedUser.Status);
        Assert.Equal(originalStamp, persistedUser.SecurityStamp);
        Assert.Empty(await ReadAuditLogsAsync());
        _realTimeNotifierMock.Verify(n => n.ForceLogoutAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData("  yes  ", "yes")]
    [InlineData("VALID_1000", "VALID_1000")]
    public async Task TakeActionOnReportAsync_BoundaryReasons_SucceedsAndStoresTrimmedReason(string rawReason, string expectedReason)
    {
        if (rawReason == "VALID_1000")
        {
            rawReason = new string('a', 1000);
            expectedReason = rawReason;
        }

        var reporter = await CreateUserWithRoleAsync(AppRoles.User, "reporter");
        var target = await CreateUserWithRoleAsync(AppRoles.User, "target");
        var report = await SeedReportAsync(reporter.Id, target.Id);

        var (scope, service, _, _) = CreateHarness();
        using (scope)
        {
            var dto = new TakeReportActionDto
            {
                Action = AdminReportAction.RejectReport,
                Reason = rawReason
            };

            var result = await service.TakeActionOnReportAsync(report.Id, dto, Guid.NewGuid(), callerIsAdmin: false);

            Assert.True(result.Succeeded);
        }

        var audit = Assert.Single(await ReadAuditLogsAsync());
        Assert.Equal(expectedReason, audit.Reason);
        Assert.Equal(nameof(AdminReportAction.RejectReport), audit.ToState);
    }

    [Fact]
    public async Task GetAdminTicketsAsync_MasksEmailsForModeratorAndExposesForAdmin()
    {
        var owner = await CreateUserWithRoleAsync(AppRoles.User, "ticketowner");
        var ownedTicket = await SeedTicketAsync(owner.Id, "contact-owned@cocorra.test", createdAt: DateTime.UtcNow.AddMinutes(-1));
        var anonTicket = await SeedTicketAsync(null, "anon-contact@cocorra.test", createdAt: DateTime.UtcNow);

        var (scope, service, _, _) = CreateHarness();
        using (scope)
        {
            var modPage = await service.GetAdminTicketsAsync(null, null, 1, 10, callerIsAdmin: false);
            var adminPage = await service.GetAdminTicketsAsync(null, null, 1, 10, callerIsAdmin: true);

            var modItems = modPage.Data!.ToDictionary(t => t.Id);
            Assert.Null(modItems[ownedTicket.Id].UserEmail);
            Assert.Null(modItems[ownedTicket.Id].ContactEmail);
            Assert.Null(modItems[anonTicket.Id].UserEmail);
            Assert.Null(modItems[anonTicket.Id].ContactEmail);

            var adminItems = adminPage.Data!.ToDictionary(t => t.Id);
            Assert.Equal(owner.Email, adminItems[ownedTicket.Id].UserEmail);
            Assert.Equal("contact-owned@cocorra.test", adminItems[ownedTicket.Id].ContactEmail);
            Assert.Null(adminItems[anonTicket.Id].UserEmail);
            Assert.Equal("anon-contact@cocorra.test", adminItems[anonTicket.Id].ContactEmail);
        }
    }

    [Fact]
    public async Task GetTicketByIdAsync_MissingTicket_ReturnsNotFoundAndWritesNoAudit()
    {
        var (scope, service, _, _) = CreateHarness();
        using (scope)
        {
            var result = await service.GetTicketByIdAsync(Guid.NewGuid(), Guid.NewGuid(), callerIsAdmin: false);

            Assert.False(result.Succeeded);
            Assert.Equal(HttpStatusCode.NotFound, result.StatusCode);
        }

        Assert.Empty(await ReadAuditLogsAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetTicketByIdAsync_MasksEmailsByRoleAndPersistsTicketViewedAudit(bool callerIsAdmin)
    {
        var owner = await CreateUserWithRoleAsync(AppRoles.User, "ticketowner");
        var callerId = Guid.NewGuid();
        var ticket = await SeedTicketAsync(owner.Id, "contact@cocorra.test");

        var (scope, service, _, _) = CreateHarness();
        using (scope)
        {
            var result = await service.GetTicketByIdAsync(ticket.Id, callerId, callerIsAdmin);

            Assert.True(result.Succeeded);
            Assert.NotNull(result.Data);
            if (callerIsAdmin)
            {
                Assert.Equal(owner.Email, result.Data!.UserEmail);
                Assert.Equal("contact@cocorra.test", result.Data.ContactEmail);
            }
            else
            {
                Assert.Null(result.Data!.UserEmail);
                Assert.Null(result.Data.ContactEmail);
            }
        }

        var audit = Assert.Single(await ReadAuditLogsAsync());
        Assert.Equal(AuditActions.TicketViewed, audit.Action);
        Assert.Equal(AuditCategories.Ticket, audit.Category);
        Assert.Equal(AuditEntityTypes.Ticket, audit.EntityType);
        Assert.Equal(ticket.Id.ToString(), audit.EntityId);
        Assert.Equal(callerId, audit.ActorUserId);
        Assert.Equal(owner.Id, audit.TargetUserId);
    }

    [Theory]
    [MemberData(nameof(InvalidReasons))]
    public async Task UpdateTicketStatusAsync_InvalidReason_ReturnsBadRequestLeavesTicketAndAuditUntouched(string? invalidReason)
    {
        var owner = await CreateUserWithRoleAsync(AppRoles.User, "owner");
        var ticket = await SeedTicketAsync(owner.Id, "contact@cocorra.test");

        var (scope, service, _, _) = CreateHarness();
        using (scope)
        {
            var result = await service.UpdateTicketStatusAsync(ticket.Id, SupportTicketStatus.Resolved, invalidReason, Guid.NewGuid(), callerIsAdmin: false);

            Assert.False(result.Succeeded);
            Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        }

        var persisted = await ReadTicketAsync(ticket.Id);
        Assert.Equal(SupportTicketStatus.Open, persisted.StatusCode);
        Assert.Equal("Open", persisted.Status);
        Assert.Null(persisted.ResolvedAt);
        Assert.Empty(await ReadAuditLogsAsync());
    }

    [Fact]
    public async Task UpdateTicketStatusAsync_UnchangedStatus_ReturnsBadRequestAndWritesNoAudit()
    {
        var owner = await CreateUserWithRoleAsync(AppRoles.User, "owner");
        var ticket = await SeedTicketAsync(owner.Id, "contact@cocorra.test", status: SupportTicketStatus.Open);

        var (scope, service, _, _) = CreateHarness();
        using (scope)
        {
            var result = await service.UpdateTicketStatusAsync(ticket.Id, SupportTicketStatus.Open, "Already open", Guid.NewGuid(), callerIsAdmin: false);

            Assert.False(result.Succeeded);
            Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        }

        var persisted = await ReadTicketAsync(ticket.Id);
        Assert.Equal(SupportTicketStatus.Open, persisted.StatusCode);
        Assert.Null(persisted.UpdatedAt);
        Assert.Empty(await ReadAuditLogsAsync());
    }

    [Theory]
    [InlineData("  fix  ", "fix")]
    [InlineData("VALID_1000", "VALID_1000")]
    public async Task UpdateTicketStatusAsync_ValidChange_PersistsTicketAndAuditRow(string rawReason, string expectedReason)
    {
        if (rawReason == "VALID_1000")
        {
            rawReason = new string('t', 1000);
            expectedReason = rawReason;
        }

        var owner = await CreateUserWithRoleAsync(AppRoles.User, "owner");
        var callerId = Guid.NewGuid();
        var ticket = await SeedTicketAsync(owner.Id, "contact@cocorra.test", status: SupportTicketStatus.Open);

        var (scope, service, _, _) = CreateHarness();
        using (scope)
        {
            var result = await service.UpdateTicketStatusAsync(ticket.Id, SupportTicketStatus.Resolved, rawReason, callerId, callerIsAdmin: false);

            Assert.True(result.Succeeded);
            Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        }

        var persisted = await ReadTicketAsync(ticket.Id);
        Assert.Equal(SupportTicketStatus.Resolved, persisted.StatusCode);
        Assert.Equal("Resolved", persisted.Status);
        Assert.NotNull(persisted.ResolvedAt);
        Assert.NotNull(persisted.UpdatedAt);

        var audit = Assert.Single(await ReadAuditLogsAsync());
        Assert.Equal(AuditActions.TicketStatusChanged, audit.Action);
        Assert.Equal(AuditCategories.Ticket, audit.Category);
        Assert.Equal(AuditEntityTypes.Ticket, audit.EntityType);
        Assert.Equal(ticket.Id.ToString(), audit.EntityId);
        Assert.Equal(callerId, audit.ActorUserId);
        Assert.Equal(owner.Id, audit.TargetUserId);
        Assert.Equal("Open", audit.FromState);
        Assert.Equal("Resolved", audit.ToState);
        Assert.Equal(expectedReason, audit.Reason);
    }

    [Fact]
    public async Task GetScreenshotUrls_WithRealIssuer_WritesCategoryEntityAndTargetAuditRows()
    {
        var reporter = await CreateUserWithRoleAsync(AppRoles.User, "reporter");
        var reported = await CreateUserWithRoleAsync(AppRoles.User, "reported");
        var ticketOwner = await CreateUserWithRoleAsync(AppRoles.User, "ticketowner");
        var staffId = Guid.NewGuid();

        var report = await SeedReportAsync(reporter.Id, reported.Id, screenshotPath: "Uploads/img/Support/rep.png");
        var ticket = await SeedTicketAsync(ticketOwner.Id, "owner@cocorra.test", screenshotPath: "Uploads/img/Support/tic.png");

        var httpContext = new DefaultHttpContext
        {
            User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(new[]
            {
                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, staffId.ToString()),
                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, AppRoles.Moderator)
            }, "TestAuth"))
        };
        _httpContextAccessorMock.Setup(h => h.HttpContext).Returns(httpContext);

        var signerMock = new Mock<IStorageUrlSigner>();
        signerMock.Setup(s => s.DefaultTtl).Returns(TimeSpan.FromMinutes(15));
        signerMock.Setup(s => s.GetObjectKey(It.IsAny<string?>())).Returns<string?>(p => p);
        signerMock.Setup(s => s.GetReadUrl(It.IsAny<string?>(), It.IsAny<TimeSpan?>()))
            .Returns<string?, TimeSpan?>((k, _) => $"https://signed/{k}");

        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var auditService = new AuditLogService(
                new AuditLogRepository(db),
                _httpContextAccessorMock.Object,
                NullLogger<AuditLogService>.Instance);
            var issuer = new PrivateFileUrlIssuer(signerMock.Object, auditService, _httpContextAccessorMock.Object);
            var service = new SupportService(
                new SupportRepository(db),
                _uploadImageMock.Object,
                scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>(),
                new NotificationRepository(db),
                _realTimeNotifierMock.Object,
                _pushServiceMock.Object,
                _eventTrackerMock.Object,
                NullLogger<SupportService>.Instance,
                issuer,
                new ConfigurationBuilder().Build(),
                auditService);

            var reportUrl = await service.GetReportScreenshotUrlAsync(report.Id, callerIsAdmin: false);
            var ticketUrl = await service.GetTicketScreenshotUrlAsync(ticket.Id, callerIsAdmin: false);

            Assert.True(reportUrl.Succeeded);
            Assert.True(ticketUrl.Succeeded);
        }

        var audits = await ReadAuditLogsAsync();
        Assert.Equal(2, audits.Count);

        var reportAudit = audits[0];
        Assert.Equal(AuditActions.ScreenshotUrlIssued, reportAudit.Action);
        Assert.Equal(AuditCategories.Report, reportAudit.Category);
        Assert.Equal(AuditEntityTypes.Report, reportAudit.EntityType);
        Assert.Equal(report.Id.ToString(), reportAudit.EntityId);
        Assert.Equal(staffId, reportAudit.ActorUserId);
        Assert.Equal(reported.Id, reportAudit.TargetUserId);

        var ticketAudit = audits[1];
        Assert.Equal(AuditActions.ScreenshotUrlIssued, ticketAudit.Action);
        Assert.Equal(AuditCategories.Ticket, ticketAudit.Category);
        Assert.Equal(AuditEntityTypes.Ticket, ticketAudit.EntityType);
        Assert.Equal(ticket.Id.ToString(), ticketAudit.EntityId);
        Assert.Equal(staffId, ticketAudit.ActorUserId);
        Assert.Equal(ticketOwner.Id, ticketAudit.TargetUserId);
    }

    [Theory]
    [InlineData(UserStatus.Pending, AdminReportAction.BanUser)]
    [InlineData(UserStatus.Pending, AdminReportAction.Mute24h)]
    [InlineData(UserStatus.Pending, AdminReportAction.WarnUser)]
    [InlineData(UserStatus.ReRecord, AdminReportAction.BanUser)]
    [InlineData(UserStatus.ReRecord, AdminReportAction.Mute24h)]
    [InlineData(UserStatus.ReRecord, AdminReportAction.WarnUser)]
    public async Task TakeActionOnReportAsync_ModeratorAgainstVoiceVerificationUser_Returns403AndLeavesEverythingUntouched(
        UserStatus targetStatus, AdminReportAction action)
    {
        var reporter = await CreateUserWithRoleAsync(AppRoles.User, "reporter");
        var applicant = await CreateUserWithRoleAsync(AppRoles.User, "applicant", targetStatus);
        var originalStamp = applicant.SecurityStamp;
        var report = await SeedReportAsync(reporter.Id, applicant.Id);

        var (scope, service, _, _) = CreateHarness();
        using (scope)
        {
            var result = await service.TakeActionOnReportAsync(
                report.Id,
                new TakeReportActionDto { Action = action, Reason = "Moderator attempted action on applicant" },
                Guid.NewGuid(),
                callerIsAdmin: false);

            Assert.False(result.Succeeded);
            Assert.Equal(HttpStatusCode.Forbidden, result.StatusCode);
            Assert.Equal("Moderators cannot take actions against accounts in voice verification.", result.Message);
        }

        var persistedReport = await ReadReportAsync(report.Id);
        Assert.Equal(ReportStatus.Open, persistedReport.StatusCode);
        Assert.Equal("Open", persistedReport.Status);
        Assert.Null(persistedReport.ResolvedAt);

        var persistedUser = await ReadUserAsync(applicant.Id);
        Assert.Equal(targetStatus, persistedUser.Status);
        Assert.False(persistedUser.LockoutEnabled);
        Assert.Null(persistedUser.LockoutEnd);
        Assert.Equal(originalStamp, persistedUser.SecurityStamp);

        Assert.Equal(0, await CountNotificationsAsync(applicant.Id));
        Assert.Empty(await ReadAuditLogsAsync());
        _realTimeNotifierMock.Verify(n => n.ForceLogoutAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData(UserStatus.Pending, AdminReportAction.BanUser)]
    [InlineData(UserStatus.Pending, AdminReportAction.Mute24h)]
    [InlineData(UserStatus.Pending, AdminReportAction.WarnUser)]
    [InlineData(UserStatus.ReRecord, AdminReportAction.BanUser)]
    [InlineData(UserStatus.ReRecord, AdminReportAction.Mute24h)]
    [InlineData(UserStatus.ReRecord, AdminReportAction.WarnUser)]
    public async Task TakeActionOnReportAsync_AdminAgainstVoiceVerificationUser_SucceedsAndWritesAudit(
        UserStatus targetStatus, AdminReportAction action)
    {
        var reporter = await CreateUserWithRoleAsync(AppRoles.User, "reporter");
        var applicant = await CreateUserWithRoleAsync(AppRoles.User, "applicant", targetStatus);
        var report = await SeedReportAsync(reporter.Id, applicant.Id);
        var adminId = Guid.NewGuid();

        var (scope, service, _, _) = CreateHarness();
        using (scope)
        {
            var result = await service.TakeActionOnReportAsync(
                report.Id,
                new TakeReportActionDto { Action = action, Reason = "Admin action on applicant" },
                adminId,
                callerIsAdmin: true);

            Assert.True(result.Succeeded);
            Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        }

        var persistedReport = await ReadReportAsync(report.Id);
        Assert.Equal(ReportStatus.Resolved, persistedReport.StatusCode);
        Assert.Equal("Resolved", persistedReport.Status);

        var audit = Assert.Single(await ReadAuditLogsAsync());
        Assert.Equal(AuditActions.ReportActionTaken, audit.Action);
        Assert.Equal(adminId, audit.ActorUserId);
        Assert.Equal(applicant.Id, audit.TargetUserId);
        Assert.Equal(action.ToString(), audit.ToState);
    }

    [Theory]
    [InlineData(AppRoles.Moderator)]
    [InlineData(AppRoles.RecordReviewer)]
    [InlineData(AppRoles.Coach)]
    public async Task AuthorizeReportTargetAsync_NonAdminCallerAgainstAnyProtectedRole_ReturnsExactGenericMessage(string protectedRole)
    {
        var target = await CreateUserWithRoleAsync(protectedRole);

        var (scope, service, _, _) = CreateHarness();
        using (scope)
        {
            var (_, denial) = await service.AuthorizeReportTargetAsync(target.Id, callerIsAdmin: false);

            Assert.NotNull(denial);
            Assert.Equal(HttpStatusCode.Forbidden, denial!.StatusCode);
            Assert.Equal("Moderators cannot take actions against staff accounts.", denial.Message);
        }
    }

    public static TheoryData<string[]> AdminTargetRoles => new()
    {
        { new[] { AppRoles.Admin } },
        { new[] { AppRoles.Coach, AppRoles.Admin } }
    };

    [Theory]
    [MemberData(nameof(AdminTargetRoles))]
    public async Task AuthorizeReportTargetAsync_NonAdminCallerAgainstAdminTarget_ReturnsNotFoundAndNullUser(string[] roles)
    {
        var adminTarget = await CreateUserWithRolesAsync(roles);

        var (scope, service, _, _) = CreateHarness();
        using (scope)
        {
            var (user, denial) = await service.AuthorizeReportTargetAsync(adminTarget.Id, callerIsAdmin: false);

            Assert.Null(user);
            Assert.NotNull(denial);
            Assert.False(denial!.Succeeded);
            Assert.Equal(HttpStatusCode.NotFound, denial.StatusCode);
            Assert.Equal("Reported user not found.", denial.Message);
        }
    }

    [Fact]
    public async Task AuthorizeReportTargetAsync_AdminCallerAgainstAdminTarget_ReturnsAdminSpecificMessage()
    {
        var adminTarget = await CreateUserWithRoleAsync(AppRoles.Admin);

        var (scope, service, _, _) = CreateHarness();
        using (scope)
        {
            var (_, denial) = await service.AuthorizeReportTargetAsync(adminTarget.Id, callerIsAdmin: true);

            Assert.NotNull(denial);
            Assert.Equal(HttpStatusCode.Forbidden, denial!.StatusCode);
            Assert.Equal("Cannot take disciplinary actions on an Admin account.", denial.Message);
        }
    }

    public static IEnumerable<object[]> TerminalStatusesAndActions()
    {
        var terminalStatuses = new[] { ReportStatus.Resolved, ReportStatus.Rejected };
        var actions = new[] { AdminReportAction.WarnUser, AdminReportAction.Mute24h, AdminReportAction.BanUser, AdminReportAction.RejectReport };
        foreach (var status in terminalStatuses)
        {
            foreach (var action in actions)
            {
                yield return new object[] { status, action };
            }
        }
    }

    [Theory]
    [MemberData(nameof(TerminalStatusesAndActions))]
    public async Task TakeActionOnReportAsync_TerminalReport_Returns409ConflictAndWritesNothing(
        ReportStatus terminalStatus, AdminReportAction action)
    {
        var reporter = await CreateUserWithRoleAsync(AppRoles.User, "reporter");
        var target = await CreateUserWithRoleAsync(AppRoles.User, "target");
        var originalStamp = target.SecurityStamp;
        var report = await SeedReportAsync(reporter.Id, target.Id, status: terminalStatus.ToString(), statusCode: terminalStatus);

        var (scope, service, _, _) = CreateHarness();
        using (scope)
        {
            var result = await service.TakeActionOnReportAsync(
                report.Id,
                new TakeReportActionDto { Action = action, Reason = "Duplicate moderation attempt" },
                Guid.NewGuid(),
                callerIsAdmin: true);

            Assert.False(result.Succeeded);
            Assert.Equal(HttpStatusCode.Conflict, result.StatusCode);
            Assert.Equal("This report has already been handled.", result.Message);
        }

        var persistedReport = await ReadReportAsync(report.Id);
        Assert.Equal(terminalStatus, persistedReport.StatusCode);
        Assert.Equal(terminalStatus.ToString(), persistedReport.Status);

        var persistedUser = await ReadUserAsync(target.Id);
        Assert.Equal(UserStatus.Active, persistedUser.Status);
        Assert.Equal(originalStamp, persistedUser.SecurityStamp);

        Assert.Equal(0, await CountNotificationsAsync(target.Id));
        Assert.Empty(await ReadAuditLogsAsync());
        _realTimeNotifierMock.Verify(n => n.ForceLogoutAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task TakeActionOnReportAsync_SequentialRepeatOnOpenReport_SecondCallReturns409WithSingleAuditAndNotification()
    {
        var reporter = await CreateUserWithRoleAsync(AppRoles.User, "reporter");
        var target = await CreateUserWithRoleAsync(AppRoles.User, "target");
        var report = await SeedReportAsync(reporter.Id, target.Id);

        var (scope1, service1, _, _) = CreateHarness();
        using (scope1)
        {
            var first = await service1.TakeActionOnReportAsync(
                report.Id,
                new TakeReportActionDto { Action = AdminReportAction.WarnUser, Reason = "First warning" },
                Guid.NewGuid(),
                callerIsAdmin: false);
            Assert.True(first.Succeeded);
        }

        var (scope2, service2, _, _) = CreateHarness();
        using (scope2)
        {
            var second = await service2.TakeActionOnReportAsync(
                report.Id,
                new TakeReportActionDto { Action = AdminReportAction.BanUser, Reason = "Second attempt after resolution" },
                Guid.NewGuid(),
                callerIsAdmin: true);

            Assert.False(second.Succeeded);
            Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
            Assert.Equal("This report has already been handled.", second.Message);
        }

        Assert.Equal(UserStatus.Active, (await ReadUserAsync(target.Id)).Status);
        Assert.Equal(1, await CountNotificationsAsync(target.Id));
        var audit = Assert.Single(await ReadAuditLogsAsync());
        Assert.Equal(AuditActions.ReportActionTaken, audit.Action);
        Assert.Equal(nameof(AdminReportAction.WarnUser), audit.ToState);
    }

    [Theory]
    [InlineData("Investigating")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1")]
    public async Task UpdateReportStatusAsync_UnknownOrEmptyStatus_Returns400AndLeavesReportAndAuditUntouched(string invalidStatus)
    {
        var reporter = await CreateUserWithRoleAsync(AppRoles.User, "reporter");
        var target = await CreateUserWithRoleAsync(AppRoles.User, "target");
        var report = await SeedReportAsync(reporter.Id, target.Id);

        var (scope, service, _, _) = CreateHarness();
        using (scope)
        {
            var result = await service.UpdateReportStatusAsync(report.Id, invalidStatus, "Attempting invalid status", Guid.NewGuid(), callerIsAdmin: true);

            Assert.False(result.Succeeded);
            Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
            Assert.Equal("Invalid report status. Allowed values: Open, Resolved, Rejected.", result.Message);
        }

        var persisted = await ReadReportAsync(report.Id);
        Assert.Equal("Open", persisted.Status);
        Assert.Equal(ReportStatus.Open, persisted.StatusCode);
        Assert.Null(persisted.ResolvedAt);
        Assert.Empty(await ReadAuditLogsAsync());
    }

    [Theory]
    [InlineData("resolved", "Resolved", ReportStatus.Resolved)]
    [InlineData("  rEjEcTeD  ", "Rejected", ReportStatus.Rejected)]
    public async Task UpdateReportStatusAsync_CaseInsensitiveValidStatus_CanonicalizesStatusAndSyncsStatusCode(
        string inputStatus, string expectedCanonicalStatus, ReportStatus expectedStatusCode)
    {
        var reporter = await CreateUserWithRoleAsync(AppRoles.User, "reporter");
        var target = await CreateUserWithRoleAsync(AppRoles.User, "target");
        var report = await SeedReportAsync(reporter.Id, target.Id);

        var (scope, service, _, _) = CreateHarness();
        using (scope)
        {
            var result = await service.UpdateReportStatusAsync(report.Id, inputStatus, "Canonical status check", Guid.NewGuid(), callerIsAdmin: false);

            Assert.True(result.Succeeded);
            Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        }

        var persisted = await ReadReportAsync(report.Id);
        Assert.Equal(expectedCanonicalStatus, persisted.Status);
        Assert.Equal(expectedStatusCode, persisted.StatusCode);
        Assert.NotNull(persisted.ResolvedAt);

        var audit = Assert.Single(await ReadAuditLogsAsync());
        Assert.Equal(AuditActions.ReportStatusChanged, audit.Action);
        Assert.Equal(expectedCanonicalStatus, audit.ToState);
    }

    [Fact]
    public async Task ModeratorCannotSeeOrMutateReportsInvolvingAdmins_AdminSeesAll()
    {
        var admin = await CreateUserWithRoleAsync(AppRoles.Admin, "adminparty");
        var userA = await CreateUserWithRoleAsync(AppRoles.User, "usera");
        var userB = await CreateUserWithRoleAsync(AppRoles.User, "userb");
        var mod = await CreateUserWithRoleAsync(AppRoles.Moderator, "modparty");

        var plainReport = await SeedReportAsync(userA.Id, userB.Id, screenshotPath: "Uploads/img/Support/plain.png");
        var modTargetReport = await SeedReportAsync(userA.Id, mod.Id, screenshotPath: "Uploads/img/Support/mod.png");
        var adminReporterReport = await SeedReportAsync(admin.Id, userA.Id, screenshotPath: "Uploads/img/Support/adminrep.png");
        var adminTargetReport = await SeedReportAsync(userA.Id, admin.Id, screenshotPath: "Uploads/img/Support/admintgt.png");

        var (scope, service, _, _) = CreateHarness();
        using (scope)
        {
            var modList = await service.GetFilteredReportsAsync(null, null, 1, 10, callerIsAdmin: false);
            Assert.Equal(2, modList.TotalCount);
            var modIds = modList.Data!.Select(r => r.Id).ToHashSet();
            Assert.Contains(plainReport.Id, modIds);
            Assert.Contains(modTargetReport.Id, modIds);
            Assert.DoesNotContain(adminReporterReport.Id, modIds);
            Assert.DoesNotContain(adminTargetReport.Id, modIds);

            foreach (var hiddenId in new[] { adminReporterReport.Id, adminTargetReport.Id })
            {
                Assert.Equal(HttpStatusCode.NotFound, (await service.GetReportByIdAsync(hiddenId, Guid.NewGuid(), callerIsAdmin: false)).StatusCode);
                Assert.Equal(HttpStatusCode.NotFound, (await service.GetReportScreenshotUrlAsync(hiddenId, callerIsAdmin: false)).StatusCode);
                Assert.Equal(HttpStatusCode.NotFound, (await service.UpdateReportStatusAsync(hiddenId, "Resolved", "Should fail", Guid.NewGuid(), callerIsAdmin: false)).StatusCode);
                Assert.Equal(HttpStatusCode.NotFound, (await service.TakeActionOnReportAsync(
                    hiddenId,
                    new TakeReportActionDto { Action = AdminReportAction.BanUser, Reason = "Should fail" },
                    Guid.NewGuid(),
                    callerIsAdmin: false)).StatusCode);
            }

            Assert.Empty(_fileUrls.Calls);
            Assert.Empty(await ReadAuditLogsAsync());
            Assert.Equal(ReportStatus.Open, (await ReadReportAsync(adminReporterReport.Id)).StatusCode);
            Assert.Equal(ReportStatus.Open, (await ReadReportAsync(adminTargetReport.Id)).StatusCode);
            Assert.Equal(UserStatus.Active, (await ReadUserAsync(userA.Id)).Status);
            Assert.Equal(UserStatus.Active, (await ReadUserAsync(admin.Id)).Status);

            var adminList = await service.GetFilteredReportsAsync(null, null, 1, 10, callerIsAdmin: true);
            Assert.Equal(4, adminList.TotalCount);
            Assert.True((await service.GetReportByIdAsync(adminReporterReport.Id, admin.Id, callerIsAdmin: true)).Succeeded);
            Assert.True((await service.GetReportByIdAsync(adminTargetReport.Id, admin.Id, callerIsAdmin: true)).Succeeded);
            Assert.True((await service.GetReportScreenshotUrlAsync(adminTargetReport.Id, callerIsAdmin: true)).Succeeded);
            Assert.True((await service.UpdateReportStatusAsync(adminReporterReport.Id, "Resolved", "Admin triage", admin.Id, callerIsAdmin: true)).Succeeded);
        }
    }

    [Fact]
    public async Task ModeratorCannotSeeOrMutateAdminTickets_AdminSeesAll()
    {
        var admin = await CreateUserWithRoleAsync(AppRoles.Admin, "adminticket");
        var user = await CreateUserWithRoleAsync(AppRoles.User, "userticket");

        var userTicket = await SeedTicketAsync(user.Id, "u@cocorra.test", screenshotPath: "Uploads/img/Support/ut.png");
        var anonTicket = await SeedTicketAsync(null, "anon@cocorra.test", screenshotPath: "Uploads/img/Support/at.png");
        var adminTicket = await SeedTicketAsync(admin.Id, "admin@cocorra.test", screenshotPath: "Uploads/img/Support/admint.png");

        var (scope, service, _, _) = CreateHarness();
        using (scope)
        {
            var modList = await service.GetAdminTicketsAsync(null, null, 1, 10, callerIsAdmin: false);
            Assert.Equal(2, modList.TotalCount);
            var modIds = modList.Data!.Select(t => t.Id).ToHashSet();
            Assert.Contains(userTicket.Id, modIds);
            Assert.Contains(anonTicket.Id, modIds);
            Assert.DoesNotContain(adminTicket.Id, modIds);

            Assert.Equal(HttpStatusCode.NotFound, (await service.GetTicketByIdAsync(adminTicket.Id, Guid.NewGuid(), callerIsAdmin: false)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await service.GetTicketScreenshotUrlAsync(adminTicket.Id, callerIsAdmin: false)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await service.UpdateTicketStatusAsync(adminTicket.Id, SupportTicketStatus.Resolved, "Should fail", Guid.NewGuid(), callerIsAdmin: false)).StatusCode);

            Assert.Empty(_fileUrls.Calls);
            Assert.Empty(await ReadAuditLogsAsync());
            Assert.Equal(SupportTicketStatus.Open, (await ReadTicketAsync(adminTicket.Id)).StatusCode);

            var adminList = await service.GetAdminTicketsAsync(null, null, 1, 10, callerIsAdmin: true);
            Assert.Equal(3, adminList.TotalCount);
            Assert.True((await service.GetTicketByIdAsync(adminTicket.Id, admin.Id, callerIsAdmin: true)).Succeeded);
            Assert.True((await service.GetTicketScreenshotUrlAsync(adminTicket.Id, callerIsAdmin: true)).Succeeded);
            Assert.True((await service.UpdateTicketStatusAsync(adminTicket.Id, SupportTicketStatus.Resolved, "Admin closed", admin.Id, callerIsAdmin: true)).Succeeded);
        }
    }

    private static async Task AssertModeratorReportEndpointsReturnNotFoundAsync(
        SupportService service,
        Guid reportId,
        AdminReportAction attemptedAction = AdminReportAction.RejectReport)
    {
        var detail = await service.GetReportByIdAsync(reportId, Guid.NewGuid(), callerIsAdmin: false);
        Assert.False(detail.Succeeded);
        Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode);
        Assert.Equal("Report not found.", detail.Message);

        var screenshot = await service.GetReportScreenshotUrlAsync(reportId, callerIsAdmin: false);
        Assert.False(screenshot.Succeeded);
        Assert.Equal(HttpStatusCode.NotFound, screenshot.StatusCode);
        Assert.Equal("Report not found.", screenshot.Message);

        var statusUpdate = await service.UpdateReportStatusAsync(reportId, "Resolved", "Should fail", Guid.NewGuid(), callerIsAdmin: false);
        Assert.False(statusUpdate.Succeeded);
        Assert.Equal(HttpStatusCode.NotFound, statusUpdate.StatusCode);
        Assert.Equal("Report not found.", statusUpdate.Message);

        var action = await service.TakeActionOnReportAsync(
            reportId,
            new TakeReportActionDto { Action = attemptedAction, Reason = "Should fail" },
            Guid.NewGuid(),
            callerIsAdmin: false);
        Assert.False(action.Succeeded);
        Assert.Equal(HttpStatusCode.NotFound, action.StatusCode);
        Assert.Equal("Report not found.", action.Message);
    }

    [Fact]
    public async Task TakeActionOnReportAsync_ModeratorAgainstMultiRoleAdminAndCoach_ReturnsNotFoundAndLeavesStateUntouched()
    {
        var reporter = await CreateUserWithRoleAsync(AppRoles.User, "reporter");
        var multiRoleAdmin = await CreateUserWithRolesAsync(new[] { AppRoles.Coach, AppRoles.Admin }, "admincoach");
        var originalStamp = multiRoleAdmin.SecurityStamp;
        var originalRefresh = multiRoleAdmin.RefreshToken;
        var originalFcm = multiRoleAdmin.FcmToken;
        var report = await SeedReportAsync(reporter.Id, multiRoleAdmin.Id);

        var (scope, service, _, _) = CreateHarness();
        using (scope)
        {
            var result = await service.TakeActionOnReportAsync(
                report.Id,
                new TakeReportActionDto { Action = AdminReportAction.BanUser, Reason = "Attempt on dual-role admin" },
                Guid.NewGuid(),
                callerIsAdmin: false);

            Assert.False(result.Succeeded);
            Assert.Equal(HttpStatusCode.NotFound, result.StatusCode);
            Assert.Equal("Report not found.", result.Message);
        }

        var persistedReport = await ReadReportAsync(report.Id);
        Assert.Equal("Open", persistedReport.Status);
        Assert.Equal(ReportStatus.Open, persistedReport.StatusCode);
        Assert.Null(persistedReport.ResolvedAt);

        var persistedUser = await ReadUserAsync(multiRoleAdmin.Id);
        Assert.Equal(UserStatus.Active, persistedUser.Status);
        Assert.False(persistedUser.LockoutEnabled);
        Assert.Null(persistedUser.LockoutEnd);
        Assert.Equal(originalStamp, persistedUser.SecurityStamp);
        Assert.Equal(originalRefresh, persistedUser.RefreshToken);
        Assert.Equal(originalFcm, persistedUser.FcmToken);

        Assert.Equal(0, await CountNotificationsAsync(multiRoleAdmin.Id));
        Assert.Empty(await ReadAuditLogsAsync());
        _realTimeNotifierMock.Verify(n => n.ForceLogoutAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
        _pushServiceMock.Verify(
            p => p.SendPushNotificationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Dictionary<string, string>>()),
            Times.Never);
    }

    [Fact]
    public async Task ReportEndpoints_AdminHostedRoom_HiddenFromModeratorAndVisibleToAdminWithHostName()
    {
        var adminHost = await CreateUserWithRoleAsync(AppRoles.Admin, "adminhost", firstName: "Nour", lastName: "AdminHost");
        var coachHost = await CreateUserWithRoleAsync(AppRoles.Coach, "coachhost", firstName: "Tariq", lastName: "CoachHost");
        var plainHost = await CreateUserWithRoleAsync(AppRoles.User, "plainhost", firstName: "Maya", lastName: "PlainHost");
        var reporter = await CreateUserWithRoleAsync(AppRoles.User, "reporter", firstName: "Rami", lastName: "Reporter");

        var adminRoom = await SeedRoomAsync(adminHost.Id, "Admin Lounge");
        var coachRoom = await SeedRoomAsync(coachHost.Id, "Coach Session");
        var plainRoom = await SeedRoomAsync(plainHost.Id, "Community Hangout");

        var adminRoomReport = await SeedReportAsync(
            reporter.Id,
            reportedUserId: null,
            screenshotPath: "Uploads/img/Support/admin-room.png",
            reportedRoomId: adminRoom.Id);
        var coachRoomReport = await SeedReportAsync(
            reporter.Id,
            reportedUserId: null,
            screenshotPath: "Uploads/img/Support/coach-room.png",
            reportedRoomId: coachRoom.Id);
        var plainRoomReport = await SeedReportAsync(
            reporter.Id,
            reportedUserId: null,
            screenshotPath: "Uploads/img/Support/plain-room.png",
            reportedRoomId: plainRoom.Id);

        var (scope, service, _, _) = CreateHarness();
        using (scope)
        {
            var modList = await service.GetFilteredReportsAsync(null, null, 1, 10, callerIsAdmin: false);
            var modItems = modList.Data!.ToList();

            Assert.True(modList.Succeeded);
            Assert.Equal(2, modList.TotalCount);
            Assert.Equal(2, modItems.Count);
            Assert.DoesNotContain(modItems, r => r.Id == adminRoomReport.Id);
            Assert.DoesNotContain(modItems, r => r.ReportedRoomHostName == "Nour AdminHost");

            var coachItem = Assert.Single(modItems, r => r.Id == coachRoomReport.Id);
            Assert.Equal("Tariq CoachHost", coachItem.ReportedRoomHostName);

            var plainItem = Assert.Single(modItems, r => r.Id == plainRoomReport.Id);
            Assert.Equal("Maya PlainHost", plainItem.ReportedRoomHostName);

            await AssertModeratorReportEndpointsReturnNotFoundAsync(service, adminRoomReport.Id);

            Assert.Empty(_fileUrls.Calls);
            Assert.Empty(await ReadAuditLogsAsync());
            var persistedAfterMod = await ReadReportAsync(adminRoomReport.Id);
            Assert.Equal("Open", persistedAfterMod.Status);
            Assert.Equal(ReportStatus.Open, persistedAfterMod.StatusCode);
            Assert.Null(persistedAfterMod.ResolvedAt);

            var adminList = await service.GetFilteredReportsAsync(null, null, 1, 10, callerIsAdmin: true);
            Assert.Equal(3, adminList.TotalCount);
            var adminListItem = Assert.Single(adminList.Data!, r => r.Id == adminRoomReport.Id);
            Assert.Equal("Nour AdminHost", adminListItem.ReportedRoomHostName);

            var adminDetail = await service.GetReportByIdAsync(adminRoomReport.Id, adminHost.Id, callerIsAdmin: true);
            Assert.True(adminDetail.Succeeded);
            Assert.Equal("Nour AdminHost", adminDetail.Data!.ReportedRoomHostName);

            Assert.True((await service.GetReportScreenshotUrlAsync(adminRoomReport.Id, callerIsAdmin: true)).Succeeded);
            Assert.True((await service.UpdateReportStatusAsync(adminRoomReport.Id, "Resolved", "Admin resolved room report", adminHost.Id, callerIsAdmin: true)).Succeeded);
        }
    }

    [Fact]
    public async Task GetFilteredReportsAsync_ModeratorPagingAcrossAdminHostedRoom_ExcludesHiddenFromPagesAndTotals()
    {
        var adminHost = await CreateUserWithRoleAsync(AppRoles.Admin, "pagingadmin");
        var coachHost = await CreateUserWithRoleAsync(AppRoles.Coach, "pagingcoach");
        var reporter = await CreateUserWithRoleAsync(AppRoles.User, "pagingreporter");

        var adminRoom = await SeedRoomAsync(adminHost.Id, "Hidden Room");
        var coachRoom = await SeedRoomAsync(coachHost.Id, "Visible Room");
        var baseTime = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);

        var visibleOlder = await SeedReportAsync(reporter.Id, null, createdAt: baseTime.AddMinutes(1), reportedRoomId: coachRoom.Id);
        var hiddenMiddle = await SeedReportAsync(reporter.Id, null, createdAt: baseTime.AddMinutes(2), reportedRoomId: adminRoom.Id);
        var visibleNewer = await SeedReportAsync(reporter.Id, null, createdAt: baseTime.AddMinutes(3), reportedRoomId: coachRoom.Id);

        var (scope, service, _, _) = CreateHarness();
        using (scope)
        {
            var modPage1 = await service.GetFilteredReportsAsync(null, null, pageNumber: 1, pageSize: 1, callerIsAdmin: false);
            var modPage2 = await service.GetFilteredReportsAsync(null, null, pageNumber: 2, pageSize: 1, callerIsAdmin: false);
            var modPage3 = await service.GetFilteredReportsAsync(null, null, pageNumber: 3, pageSize: 1, callerIsAdmin: false);

            Assert.Equal(2, modPage1.TotalCount);
            Assert.Equal(2, modPage1.TotalPages);
            Assert.True(modPage1.HasNextPage);
            Assert.False(modPage1.HasPreviousPage);
            Assert.Equal(visibleNewer.Id, Assert.Single(modPage1.Data!).Id);

            Assert.Equal(2, modPage2.TotalCount);
            Assert.Equal(2, modPage2.TotalPages);
            Assert.False(modPage2.HasNextPage);
            Assert.True(modPage2.HasPreviousPage);
            Assert.Equal(visibleOlder.Id, Assert.Single(modPage2.Data!).Id);

            Assert.Equal(2, modPage3.TotalCount);
            Assert.Equal(2, modPage3.TotalPages);
            Assert.Empty(modPage3.Data!);

            var adminPage1 = await service.GetFilteredReportsAsync(null, null, pageNumber: 1, pageSize: 1, callerIsAdmin: true);
            var adminPage2 = await service.GetFilteredReportsAsync(null, null, pageNumber: 2, pageSize: 1, callerIsAdmin: true);
            var adminPage3 = await service.GetFilteredReportsAsync(null, null, pageNumber: 3, pageSize: 1, callerIsAdmin: true);

            Assert.Equal(3, adminPage1.TotalCount);
            Assert.Equal(3, adminPage1.TotalPages);
            Assert.Equal(visibleNewer.Id, Assert.Single(adminPage1.Data!).Id);
            Assert.Equal(hiddenMiddle.Id, Assert.Single(adminPage2.Data!).Id);
            Assert.Equal(visibleOlder.Id, Assert.Single(adminPage3.Data!).Id);
        }
    }

    [Fact]
    public async Task ReportEndpoints_MultiRoleAdminAndCoachHostedRoom_HiddenFromModeratorAndVisibleToAdmin()
    {
        var multiRoleHost = await CreateUserWithRolesAsync(
            new[] { AppRoles.Coach, AppRoles.Admin },
            "multihost",
            firstName: "Layla",
            lastName: "MultiHost");
        var reporter = await CreateUserWithRoleAsync(AppRoles.User, "reporter");
        var room = await SeedRoomAsync(multiRoleHost.Id, "Dual-Role Host Room");
        var report = await SeedReportAsync(
            reporter.Id,
            reportedUserId: null,
            screenshotPath: "Uploads/img/Support/multi-host.png",
            reportedRoomId: room.Id);

        var (scope, service, _, _) = CreateHarness();
        using (scope)
        {
            var modList = await service.GetFilteredReportsAsync(null, null, 1, 10, callerIsAdmin: false);
            Assert.Equal(0, modList.TotalCount);
            Assert.Empty(modList.Data!);

            await AssertModeratorReportEndpointsReturnNotFoundAsync(service, report.Id);

            Assert.Empty(_fileUrls.Calls);
            Assert.Empty(await ReadAuditLogsAsync());
            Assert.Equal(ReportStatus.Open, (await ReadReportAsync(report.Id)).StatusCode);

            var adminList = await service.GetFilteredReportsAsync(null, null, 1, 10, callerIsAdmin: true);
            Assert.Equal(1, adminList.TotalCount);
            Assert.Equal(report.Id, Assert.Single(adminList.Data!).Id);

            var adminDetail = await service.GetReportByIdAsync(report.Id, multiRoleHost.Id, callerIsAdmin: true);
            Assert.True(adminDetail.Succeeded);
            Assert.Equal("Layla MultiHost", adminDetail.Data!.ReportedRoomHostName);
        }
    }

    [Fact]
    public async Task ReportEndpoints_AdminHostedRoomWithPlainReportedUser_HiddenFromModeratorAndLeavesUserUntouched()
    {
        var adminHost = await CreateUserWithRoleAsync(AppRoles.Admin, "adminhost", firstName: "Omar", lastName: "AdminHost");
        var reporter = await CreateUserWithRoleAsync(AppRoles.User, "reporter");
        var plainReported = await CreateUserWithRoleAsync(AppRoles.User, "plainreported");
        var originalStamp = plainReported.SecurityStamp;

        var adminRoom = await SeedRoomAsync(adminHost.Id, "Admin Mixed Room");
        var report = await SeedReportAsync(
            reporter.Id,
            reportedUserId: plainReported.Id,
            screenshotPath: "Uploads/img/Support/mixed-room.png",
            reportedRoomId: adminRoom.Id);

        var (scope, service, _, _) = CreateHarness();
        using (scope)
        {
            var modList = await service.GetFilteredReportsAsync(null, null, 1, 10, callerIsAdmin: false);
            Assert.Equal(0, modList.TotalCount);
            Assert.Empty(modList.Data!);

            await AssertModeratorReportEndpointsReturnNotFoundAsync(service, report.Id, AdminReportAction.BanUser);

            Assert.Empty(_fileUrls.Calls);
            Assert.Empty(await ReadAuditLogsAsync());
            Assert.Equal(ReportStatus.Open, (await ReadReportAsync(report.Id)).StatusCode);

            var persistedUser = await ReadUserAsync(plainReported.Id);
            Assert.Equal(UserStatus.Active, persistedUser.Status);
            Assert.Equal(originalStamp, persistedUser.SecurityStamp);
            Assert.Equal(0, await CountNotificationsAsync(plainReported.Id));
            _realTimeNotifierMock.Verify(n => n.ForceLogoutAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);

            var adminList = await service.GetFilteredReportsAsync(null, null, 1, 10, callerIsAdmin: true);
            Assert.Equal(1, adminList.TotalCount);
            Assert.Equal(report.Id, Assert.Single(adminList.Data!).Id);

            var adminDetail = await service.GetReportByIdAsync(report.Id, adminHost.Id, callerIsAdmin: true);
            Assert.True(adminDetail.Succeeded);
            Assert.Equal(plainReported.Id, adminDetail.Data!.ReportedUserId);
            Assert.Equal("Omar AdminHost", adminDetail.Data.ReportedRoomHostName);
        }
    }

    [Fact]
    public async Task ReportEndpoints_NonAdminHostedRoomFiledByAdmin_HiddenFromModeratorAndVisibleToAdmin()
    {
        var adminReporter = await CreateUserWithRoleAsync(AppRoles.Admin, "adminreporter");
        var coachHost = await CreateUserWithRoleAsync(AppRoles.Coach, "coachhost", firstName: "Hana", lastName: "CoachHost");
        var coachRoom = await SeedRoomAsync(coachHost.Id, "Coach Hosted Room");
        var report = await SeedReportAsync(
            adminReporter.Id,
            reportedUserId: null,
            screenshotPath: "Uploads/img/Support/admin-reporter-room.png",
            reportedRoomId: coachRoom.Id);

        var (scope, service, _, _) = CreateHarness();
        using (scope)
        {
            var modList = await service.GetFilteredReportsAsync(null, null, 1, 10, callerIsAdmin: false);
            Assert.Equal(0, modList.TotalCount);
            Assert.Empty(modList.Data!);

            await AssertModeratorReportEndpointsReturnNotFoundAsync(service, report.Id);

            Assert.Empty(_fileUrls.Calls);
            Assert.Empty(await ReadAuditLogsAsync());
            Assert.Equal(ReportStatus.Open, (await ReadReportAsync(report.Id)).StatusCode);

            var adminList = await service.GetFilteredReportsAsync(null, null, 1, 10, callerIsAdmin: true);
            Assert.Equal(1, adminList.TotalCount);
            Assert.Equal(report.Id, Assert.Single(adminList.Data!).Id);

            var adminDetail = await service.GetReportByIdAsync(report.Id, adminReporter.Id, callerIsAdmin: true);
            Assert.True(adminDetail.Succeeded);
            Assert.Equal("Hana CoachHost", adminDetail.Data!.ReportedRoomHostName);
        }
    }
}
