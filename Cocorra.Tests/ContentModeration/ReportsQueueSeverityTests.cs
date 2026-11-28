using Cocorra.DAL.DTOS.ReportDto;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Cocorra.BLL.Services.EventTracking;
using Cocorra.BLL.Services.NotificationService;
using Cocorra.BLL.Services.RealTimeNotifier;
using Cocorra.BLL.Services.SupportService;
using Cocorra.BLL.Services.Upload;
using Cocorra.DAL.Data;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;
using Cocorra.DAL.Repository.NotificationRepository;
using Cocorra.DAL.Repository.SupportRepository;
using Cocorra.Tests.Helpers;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Cocorra.Tests.ContentModeration;

public class ReportsQueueSeverityTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _services;
    private readonly FakePrivateFileUrlIssuer _fileUrls = new();

    public ReportsQueueSeverityTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AppDbContext>(o => o.UseSqlite(_connection));
        services.AddIdentityCore<ApplicationUser>()
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<AppDbContext>();

        _services = services.BuildServiceProvider();

        using var scope = _services.CreateScope();
        scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        foreach (var role in AppRoles.All)
            roles.CreateAsync(new IdentityRole<Guid>(role)).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _services.Dispose();
        _connection.Dispose();
    }

    private (IServiceScope Scope, SupportService Service) CreateHarness()
    {
        var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var repo = new SupportRepository(db);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AppSettings:BaseUrl"] = "https://api.example.com"
            })
            .Build();

        var service = new SupportService(
            repo,
            Mock.Of<IUploadImage>(),
            userManager,
            Mock.Of<INotificationRepository>(),
            Mock.Of<IRealTimeNotifier>(),
            Mock.Of<IPushNotificationService>(),
            Mock.Of<IEventTracker>(),
            NullLogger<SupportService>.Instance,
            _fileUrls,
            config);

        return (scope, service);
    }

    private async Task<ApplicationUser> CreateUserAsync(string role, string firstName, string lastName)
    {
        using var scope = _services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var id = Guid.NewGuid();
        var user = new ApplicationUser
        {
            Id = id,
            UserName = $"u-{id:N}",
            Email = $"u-{id:N}@example.test",
            FirstName = firstName,
            LastName = lastName,
            Status = UserStatus.Active
        };
        var created = await userManager.CreateAsync(user);
        Assert.True(created.Succeeded);
        var roled = await userManager.AddToRoleAsync(user, role);
        Assert.True(roled.Succeeded);
        return user;
    }

    private async Task<Report> SeedReportAsync(
        Guid reporterId,
        Guid? reportedUserId,
        ModerationSeverity? severity,
        DateTime createdAt,
        string description,
        ReportSource source = ReportSource.User,
        Guid? textRoomId = null,
        long? textRoomMessageId = null,
        string? matchedTerm = null,
        DateTime? contextCompletedAt = null,
        bool addContextEntry = false)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var report = new Report
        {
            Id = Guid.NewGuid(),
            ReporterId = reporterId,
            ReportedUserId = reportedUserId,
            Category = ReportCategory.Harassment,
            Description = description,
            Status = "Open",
            StatusCode = ReportStatus.Open,
            Severity = severity,
            Source = source,
            ReportedTextRoomId = textRoomId,
            ReportedTextRoomMessageId = textRoomMessageId,
            MatchedTerm = matchedTerm,
            ContextCompletedAt = contextCompletedAt,
            CreatedAt = createdAt
        };
        db.Reports.Add(report);

        if (addContextEntry && textRoomMessageId.HasValue)
        {
            db.ReportMessageContextEntries.Add(new ReportMessageContextEntry
            {
                ReportId = report.Id,
                TextRoomMessageId = textRoomMessageId.Value,
                Offset = 0,
                SenderId = reportedUserId ?? reporterId,
                SenderDisplayName = "Target User",
                Content = "Sample context message",
                SentAt = createdAt
            });
        }

        await db.SaveChangesAsync();
        return report;
    }

    [Fact]
    public async Task GetFilteredReportsAsync_SeverityFilter_ReturnsOnlyMatchingReports()
    {
        var reporter = await CreateUserAsync(AppRoles.User, "Rep", "One");
        var target = await CreateUserAsync(AppRoles.User, "Tar", "Get");
        var now = new DateTime(2026, 11, 28, 12, 0, 0, DateTimeKind.Utc);

        await SeedReportAsync(reporter.Id, target.Id, ModerationSeverity.Low, now.AddMinutes(1), "low");
        await SeedReportAsync(reporter.Id, target.Id, ModerationSeverity.Medium, now.AddMinutes(2), "med");
        var high = await SeedReportAsync(reporter.Id, target.Id, ModerationSeverity.High, now.AddMinutes(3), "high");
        var critical = await SeedReportAsync(reporter.Id, target.Id, ModerationSeverity.Critical, now.AddMinutes(4), "crit");
        await SeedReportAsync(reporter.Id, target.Id, null, now.AddMinutes(5), "legacy");

        var (scope, service) = CreateHarness();
        using (scope)
        {
            var highOnly = await service.GetFilteredReportsAsync(new ReportListQuery(null, null, ModerationSeverity.High, false, 1, 20), true);
            Assert.Equal(1, highOnly.TotalCount);
            Assert.Equal(high.Id, Assert.Single(highOnly.Data!).Id);

            var critOnly = await service.GetFilteredReportsAsync(new ReportListQuery(null, null, ModerationSeverity.Critical, false, 1, 20), true);
            Assert.Equal(1, critOnly.TotalCount);
            Assert.Equal(critical.Id, Assert.Single(critOnly.Data!).Id);

            var all = await service.GetFilteredReportsAsync(new ReportListQuery(null, null, null, false, 1, 20), true);
            Assert.Equal(5, all.TotalCount);
        }
    }

    [Fact]
    public async Task GetFilteredReportsAsync_PrioritySort_OrdersCriticalToLowWithNullLast_AndDefaultSortRemainsCreatedAtDesc()
    {
        var reporter = await CreateUserAsync(AppRoles.User, "Rep", "One");
        var target = await CreateUserAsync(AppRoles.User, "Tar", "Get");
        var t0 = new DateTime(2026, 11, 28, 12, 0, 0, DateTimeKind.Utc);

        var critOlder = await SeedReportAsync(reporter.Id, target.Id, ModerationSeverity.Critical, t0.AddMinutes(1), "crit-old");
        var critNewer = await SeedReportAsync(reporter.Id, target.Id, ModerationSeverity.Critical, t0.AddMinutes(5), "crit-new");
        var highOlder = await SeedReportAsync(reporter.Id, target.Id, ModerationSeverity.High, t0.AddMinutes(10), "high-old");
        var highNewer = await SeedReportAsync(reporter.Id, target.Id, ModerationSeverity.High, t0.AddMinutes(20), "high-new");
        var medium = await SeedReportAsync(reporter.Id, target.Id, ModerationSeverity.Medium, t0.AddMinutes(30), "medium");
        var low = await SeedReportAsync(reporter.Id, target.Id, ModerationSeverity.Low, t0.AddMinutes(40), "low");
        var legacyNewest = await SeedReportAsync(reporter.Id, target.Id, null, t0.AddMinutes(50), "legacy");

        var (scope, service) = CreateHarness();
        using (scope)
        {
            var byPriority = await service.GetFilteredReportsAsync(new ReportListQuery(null, null, null, true, 1, 20), true);
            Assert.Equal(
                new[] { critNewer.Id, critOlder.Id, highNewer.Id, highOlder.Id, medium.Id, low.Id, legacyNewest.Id },
                byPriority.Data!.Select(r => r.Id).ToArray());

            var byDefaultNewOverload = await service.GetFilteredReportsAsync(new ReportListQuery(null, null, null, false, 1, 20), true);
            var expectedChronological = new[] { legacyNewest.Id, low.Id, medium.Id, highNewer.Id, highOlder.Id, critNewer.Id, critOlder.Id };
            Assert.Equal(expectedChronological, byDefaultNewOverload.Data!.Select(r => r.Id).ToArray());

            var byLegacyOverload = await service.GetFilteredReportsAsync(null, null, 1, 20, callerIsAdmin: true);
            Assert.Equal(expectedChronological, byLegacyOverload.Data!.Select(r => r.Id).ToArray());
        }
    }

    [Fact]
    public async Task GetFilteredReportsAsync_ModeratorList_HidesAdminInvolvedTextRoomReportsAndShowsRegularOnes()
    {
        var admin = await CreateUserAsync(AppRoles.Admin, "Site", "Admin");
        var userA = await CreateUserAsync(AppRoles.User, "Alice", "User");
        var userB = await CreateUserAsync(AppRoles.User, "Bob", "User");

        Guid roomId;
        long msg1Id, msg2Id, msg3Id;
        using (var seedScope = _services.CreateScope())
        {
            var db = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var room = new TextRoom
            {
                Id = Guid.NewGuid(),
                Title = "Admin-Created Room",
                CreatedById = admin.Id,
                Status = TextRoomStatus.Active,
                Settings = new TextRoomSettings()
            };
            db.TextRooms.Add(room);
            var m1 = new TextRoomMessage { TextRoomId = room.Id, SenderId = admin.Id, Content = "Admin msg" };
            var m2 = new TextRoomMessage { TextRoomId = room.Id, SenderId = userA.Id, Content = "User msg 1" };
            var m3 = new TextRoomMessage { TextRoomId = room.Id, SenderId = userB.Id, Content = "User msg 2" };
            db.TextRoomMessages.AddRange(m1, m2, m3);
            await db.SaveChangesAsync();
            roomId = room.Id;
            msg1Id = m1.Id;
            msg2Id = m2.Id;
            msg3Id = m3.Id;
        }

        var now = new DateTime(2026, 11, 28, 12, 0, 0, DateTimeKind.Utc);
        var adminReported = await SeedReportAsync(userA.Id, admin.Id, ModerationSeverity.High, now.AddMinutes(1), "admin-reported", textRoomId: roomId, textRoomMessageId: msg1Id);
        var adminReporter = await SeedReportAsync(admin.Id, userA.Id, ModerationSeverity.High, now.AddMinutes(2), "admin-reporter", textRoomId: roomId, textRoomMessageId: msg2Id);
        var regularOnly = await SeedReportAsync(userA.Id, userB.Id, ModerationSeverity.Medium, now.AddMinutes(3), "regular-only", textRoomId: roomId, textRoomMessageId: msg3Id);

        var (scope, service) = CreateHarness();
        using (scope)
        {
            var moderatorView = await service.GetFilteredReportsAsync(new ReportListQuery(null, null, null, true, 1, 20), false);
            Assert.Equal(1, moderatorView.TotalCount);
            Assert.Equal(regularOnly.Id, Assert.Single(moderatorView.Data!).Id);

            var adminView = await service.GetFilteredReportsAsync(new ReportListQuery(null, null, null, true, 1, 20), true);
            Assert.Equal(3, adminView.TotalCount);
            Assert.Contains(adminView.Data!, r => r.Id == adminReported.Id);
            Assert.Contains(adminView.Data!, r => r.Id == adminReporter.Id);
            Assert.Contains(adminView.Data!, r => r.Id == regularOnly.Id);
        }
    }

    [Fact]
    public async Task GetFilteredReportsAsync_MapsTextRoomMetadataContextFlagsSameMessageCountAndAutoModerationReporterName()
    {
        var sender = await CreateUserAsync(AppRoles.User, "Msg", "Sender");
        var reporter1 = await CreateUserAsync(AppRoles.User, "First", "Reporter");
        var reporter2 = await CreateUserAsync(AppRoles.User, "Second", "Reporter");

        Guid roomId;
        long msgId;
        using (var seedScope = _services.CreateScope())
        {
            var db = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var room = new TextRoom
            {
                Id = Guid.NewGuid(),
                Title = "Late Night Lounge",
                Status = TextRoomStatus.Active,
                Settings = new TextRoomSettings()
            };
            db.TextRooms.Add(room);
            var msg = new TextRoomMessage { TextRoomId = room.Id, SenderId = sender.Id, Content = "Flagged message" };
            db.TextRoomMessages.Add(msg);
            await db.SaveChangesAsync();
            roomId = room.Id;
            msgId = msg.Id;
        }

        var now = new DateTime(2026, 11, 28, 12, 0, 0, DateTimeKind.Utc);
        var completeUserReport = await SeedReportAsync(
            reporter1.Id,
            sender.Id,
            ModerationSeverity.Medium,
            now.AddMinutes(1),
            "User report 1",
            source: ReportSource.User,
            textRoomId: roomId,
            textRoomMessageId: msgId,
            contextCompletedAt: now.AddMinutes(2),
            addContextEntry: true);

        var pendingUserReport = await SeedReportAsync(
            reporter2.Id,
            sender.Id,
            ModerationSeverity.Medium,
            now.AddMinutes(2),
            "User report 2",
            source: ReportSource.User,
            textRoomId: roomId,
            textRoomMessageId: msgId,
            contextCompletedAt: null,
            addContextEntry: true);

        // AutoModeration uses sender as ReporterId; must not be counted in SameMessageReportCount (User source only).
        var autoReport = await SeedReportAsync(
            sender.Id,
            sender.Id,
            ModerationSeverity.High,
            now.AddMinutes(3),
            "Auto-flagged",
            source: ReportSource.AutoModeration,
            textRoomId: roomId,
            textRoomMessageId: msgId,
            matchedTerm: "badword",
            contextCompletedAt: null,
            addContextEntry: false);

        var (scope, service) = CreateHarness();
        using (scope)
        {
            var result = await service.GetFilteredReportsAsync(new ReportListQuery(null, null, null, false, 1, 20), true);
            var byId = result.Data!.ToDictionary(r => r.Id);

            var r1 = byId[completeUserReport.Id];
            Assert.Equal("First Reporter", r1.ReporterName);
            Assert.Equal(nameof(ReportSource.User), r1.Source);
            Assert.Equal(ModerationSeverity.Medium, r1.Severity);
            Assert.Equal(nameof(ModerationSeverity.Medium), r1.SeverityName);
            Assert.Equal(roomId, r1.ReportedTextRoomId);
            Assert.Equal("Late Night Lounge", r1.ReportedTextRoomTitle);
            Assert.Equal(msgId, r1.ReportedTextRoomMessageId);
            Assert.Equal(2, r1.SameMessageReportCount);
            Assert.True(r1.HasMessageContext);
            Assert.True(r1.IsMessageContextComplete);

            var r2 = byId[pendingUserReport.Id];
            Assert.Equal(2, r2.SameMessageReportCount);
            Assert.True(r2.HasMessageContext);
            Assert.False(r2.IsMessageContextComplete);

            var auto = byId[autoReport.Id];
            Assert.Equal("Auto-moderation", auto.ReporterName);
            Assert.Equal(nameof(ReportSource.AutoModeration), auto.Source);
            Assert.Equal(ModerationSeverity.High, auto.Severity);
            Assert.Equal(nameof(ModerationSeverity.High), auto.SeverityName);
            Assert.Equal("badword", auto.MatchedTerm);
            Assert.Equal(2, auto.SameMessageReportCount);
            Assert.False(auto.HasMessageContext);
            Assert.False(auto.IsMessageContextComplete);
        }
    }
}
