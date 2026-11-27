using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Cocorra.BLL.Services.StaffActivityService;
using Cocorra.DAL.Data;
using Cocorra.DAL.DTOS.StaffActivityDto;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;
using Cocorra.DAL.Repository.AuditLogRepository;
using Cocorra.DAL.Repository.UserRepository;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cocorra.Tests;

public class StaffActivityServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _services;
    private readonly IConfiguration _configuration;

    public StaffActivityServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AppSettings:BaseUrl"] = "https://api.cocorra.test"
            })
            .Build();

        var sc = new ServiceCollection();
        sc.AddLogging();
        sc.AddDbContext<AppDbContext>(options => options.UseSqlite(_connection));
        sc.AddIdentity<ApplicationUser, IdentityRole<Guid>>(options =>
        {
            options.User.RequireUniqueEmail = false;
        })
        .AddEntityFrameworkStores<AppDbContext>()
        .AddDefaultTokenProviders();

        _services = sc.BuildServiceProvider();

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

    private async Task<ApplicationUser> SeedUserAsync(
        string firstName,
        string lastName,
        string email,
        string? role,
        string? profilePicturePath = null)
    {
        using var scope = _services.CreateScope();
        var um = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = email,
            Email = email,
            FirstName = firstName,
            LastName = lastName,
            Status = UserStatus.Active,
            ProfilePicturePath = profilePicturePath,
            CreatedAt = new DateTime(2026, 1, 10, 8, 0, 0, DateTimeKind.Utc)
        };
        Assert.True((await um.CreateAsync(user)).Succeeded);
        if (role != null)
        {
            Assert.True((await um.AddToRoleAsync(user, role)).Succeeded);
        }
        return user;
    }

    private async Task SeedAuditLogsAsync(params AuditLog[] logs)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.AuditLogs.AddRange(logs);
        await db.SaveChangesAsync();
    }

    private (StaffActivityService service, IServiceScope scope) CreateServiceScope()
    {
        var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var um = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var auditRepo = new AuditLogRepository(db);
        var userRepo = new UserRepository(db);
        var service = new StaffActivityService(um, auditRepo, userRepo, _configuration, NullLogger<StaffActivityService>.Instance);
        return (service, scope);
    }

    [Fact]
    public async Task GetStaffActivityAsync_MissingUser_Returns404()
    {
        var (service, scope) = CreateServiceScope();
        using (scope)
        {
            var result = await service.GetStaffActivityAsync(Guid.NewGuid(), new StaffActivityQueryDto());
            Assert.False(result.Succeeded);
            Assert.Equal(HttpStatusCode.NotFound, result.StatusCode);
        }
    }

    [Theory]
    [InlineData(AppRoles.User)]
    [InlineData(AppRoles.Admin)]
    [InlineData(null)]
    public async Task GetStaffActivityAsync_NonStaffRole_Returns404(string? role)
    {
        var user = await SeedUserAsync("Non", "Staff", $"nonstaff_{role ?? "none"}@cocorra.test", role);

        var (service, scope) = CreateServiceScope();
        using (scope)
        {
            var result = await service.GetStaffActivityAsync(user.Id, new StaffActivityQueryDto());
            Assert.False(result.Succeeded);
            Assert.Equal(HttpStatusCode.NotFound, result.StatusCode);
        }
    }

    [Theory]
    [InlineData(AppRoles.Moderator)]
    [InlineData(AppRoles.RecordReviewer)]
    [InlineData(AppRoles.Coach)]
    public async Task GetStaffActivityAsync_StaffRoleWithNoRows_Returns200WithProfileAndZeroSummary(string staffRole)
    {
        var staff = await SeedUserAsync("Lina", "Staff", $"{staffRole.ToLowerInvariant()}@cocorra.test", staffRole, "uploads/avatars/lina.png");

        var (service, scope) = CreateServiceScope();
        using (scope)
        {
            var result = await service.GetStaffActivityAsync(staff.Id, null);

            Assert.True(result.Succeeded);
            Assert.Equal(HttpStatusCode.OK, result.StatusCode);
            var data = result.Data!;
            Assert.Equal(staff.Id, data.Staff.UserId);
            Assert.Equal("Lina Staff", data.Staff.FullName);
            Assert.Equal(staff.Email, data.Staff.Email);
            Assert.Contains(staffRole, data.Staff.Roles);
            Assert.Equal("https://api.cocorra.test/uploads/avatars/lina.png", data.Staff.ProfilePicture);
            Assert.Equal(0, data.Summary.TotalActions);
            Assert.Null(data.Summary.LastActivityAtUtc);
            Assert.Empty(data.Items);
            Assert.Equal(0, data.TotalCount);
        }
    }

    [Fact]
    public async Task GetStaffActivityAsync_InvalidCategory_Returns400ListingAllowedCategories()
    {
        var staff = await SeedUserAsync("Mod", "One", "mod1@cocorra.test", AppRoles.Moderator);

        var (service, scope) = CreateServiceScope();
        using (scope)
        {
            var result = await service.GetStaffActivityAsync(staff.Id, new StaffActivityQueryDto { Category = "BogusCategory" });

            Assert.False(result.Succeeded);
            Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
            foreach (var allowed in AuditCategories.All)
            {
                Assert.Contains(allowed, result.Message);
            }
        }
    }

    [Fact]
    public async Task GetStaffActivityAsync_InvalidActionType_Returns400ListingAllowedActions()
    {
        var staff = await SeedUserAsync("Mod", "Two", "mod2@cocorra.test", AppRoles.Moderator);

        var (service, scope) = CreateServiceScope();
        using (scope)
        {
            var result = await service.GetStaffActivityAsync(staff.Id, new StaffActivityQueryDto { ActionType = "Bogus.Action" });

            Assert.False(result.Succeeded);
            Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
            foreach (var allowed in AuditActions.All)
            {
                Assert.Contains(allowed, result.Message);
            }
        }
    }

    [Fact]
    public async Task GetStaffActivityAsync_FromUtcLaterThanToUtc_Returns400()
    {
        var staff = await SeedUserAsync("Mod", "Dates", "moddates@cocorra.test", AppRoles.Moderator);
        var now = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

        var (service, scope) = CreateServiceScope();
        using (scope)
        {
            var result = await service.GetStaffActivityAsync(staff.Id, new StaffActivityQueryDto
            {
                FromUtc = now.AddHours(2),
                ToUtc = now
            });

            Assert.False(result.Succeeded);
            Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
            Assert.Contains("fromUtc", result.Message);
        }
    }

    [Fact]
    public async Task GetStaffActivityAsync_FiltersInclusiveDateBoundsOrdersNewestFirstClampsPaginationAndExcludesOtherActors()
    {
        var staff = await SeedUserAsync("Mod", "Filter", "modfilter@cocorra.test", AppRoles.Moderator);
        var otherStaff = await SeedUserAsync("Other", "Staff", "other@cocorra.test", AppRoles.Moderator);
        var targetA = await SeedUserAsync("Target", "Alpha", "alpha@cocorra.test", AppRoles.User);
        var targetB = await SeedUserAsync("Target", "Beta", "beta@cocorra.test", AppRoles.User);

        var t0 = new DateTime(2026, 6, 1, 10, 0, 0, DateTimeKind.Utc);
        await SeedAuditLogsAsync(
            new AuditLog { ActorUserId = staff.Id, Action = AuditActions.ReportViewed, Category = AuditCategories.Report, TargetUserId = targetA.Id, OccurredAtUtc = t0.AddSeconds(-1) },
            new AuditLog { ActorUserId = staff.Id, Action = AuditActions.ReportViewed, Category = AuditCategories.Report, TargetUserId = targetA.Id, OccurredAtUtc = t0 },
            new AuditLog { ActorUserId = staff.Id, Action = AuditActions.TicketViewed, Category = AuditCategories.Ticket, TargetUserId = targetB.Id, OccurredAtUtc = t0.AddMinutes(30) },
            new AuditLog { ActorUserId = staff.Id, Action = AuditActions.ReportStatusChanged, Category = AuditCategories.Report, TargetUserId = targetA.Id, ToState = nameof(ReportStatus.Resolved), OccurredAtUtc = t0.AddHours(1) },
            new AuditLog { ActorUserId = staff.Id, Action = AuditActions.ReportViewed, Category = AuditCategories.Report, TargetUserId = targetA.Id, OccurredAtUtc = t0.AddHours(1).AddSeconds(1) },
            new AuditLog { ActorUserId = otherStaff.Id, Action = AuditActions.ReportViewed, Category = AuditCategories.Report, TargetUserId = targetA.Id, OccurredAtUtc = t0.AddMinutes(45) }
        );

        var (service, scope) = CreateServiceScope();
        using (scope)
        {
            var dateFiltered = await service.GetStaffActivityAsync(staff.Id, new StaffActivityQueryDto
            {
                FromUtc = t0,
                ToUtc = t0.AddHours(1),
                PageNumber = 0,
                PageSize = 500
            });

            Assert.True(dateFiltered.Succeeded);
            var page = dateFiltered.Data!;
            Assert.Equal(1, page.PageNumber);
            Assert.Equal(100, page.PageSize);
            Assert.Equal(3, page.TotalCount);
            Assert.Equal(1, page.TotalPages);
            Assert.Equal(
                new[] { t0.AddHours(1), t0.AddMinutes(30), t0 },
                page.Items.Select(i => i.OccurredAtUtc));
            Assert.Equal(3, page.Summary.TotalActions);
            Assert.Equal(t0.AddHours(1), page.Summary.LastActivityAtUtc);

            var categoryAndTargetFiltered = await service.GetStaffActivityAsync(staff.Id, new StaffActivityQueryDto
            {
                Category = AuditCategories.Report,
                ActionType = AuditActions.ReportStatusChanged,
                TargetUserId = targetA.Id,
                FromUtc = t0,
                ToUtc = t0.AddHours(1),
                PageNumber = 1,
                PageSize = 0
            });

            var filteredPage = categoryAndTargetFiltered.Data!;
            Assert.Equal(1, filteredPage.PageSize);
            Assert.Equal(1, filteredPage.TotalCount);
            var onlyItem = Assert.Single(filteredPage.Items);
            Assert.Equal(AuditActions.ReportStatusChanged, onlyItem.Action);
            Assert.Equal(targetA.Id, onlyItem.TargetUserId);

            Assert.Equal(3, filteredPage.Summary.TotalActions);
            Assert.Equal(1, filteredPage.Summary.ReportsViewed);
            Assert.Equal(1, filteredPage.Summary.TicketsViewed);
            Assert.Equal(1, filteredPage.Summary.ReportStatusChanges);
        }
    }

    [Fact]
    public async Task GetStaffActivityAsync_SummaryCountsMapAllAuditActionsAccuratelyAndIgnoreOtherActors()
    {
        var staff = await SeedUserAsync("All", "Actions", "allactions@cocorra.test", AppRoles.Moderator);
        var otherActor = await SeedUserAsync("Noise", "Actor", "noise@cocorra.test", AppRoles.Coach);
        var baseTime = new DateTime(2026, 7, 1, 9, 0, 0, DateTimeKind.Utc);
        var seq = 0;

        AuditLog Row(Guid actorId, string action, string category, string? toState = null) => new()
        {
            ActorUserId = actorId,
            Action = action,
            Category = category,
            ToState = toState,
            OccurredAtUtc = baseTime.AddMinutes(++seq)
        };

        var rows = new List<AuditLog>
        {
            Row(staff.Id, AuditActions.VoiceUrlIssued, AuditCategories.VoiceReview),
            Row(staff.Id, AuditActions.VoiceUrlIssued, AuditCategories.VoiceReview),
            Row(staff.Id, AuditActions.VoiceReviewDecision, AuditCategories.VoiceReview, nameof(UserStatus.Active)),
            Row(staff.Id, AuditActions.VoiceReviewDecision, AuditCategories.VoiceReview, nameof(UserStatus.Active)),
            Row(staff.Id, AuditActions.VoiceReviewDecision, AuditCategories.VoiceReview, nameof(UserStatus.ReRecord)),
            Row(staff.Id, AuditActions.VoiceReviewDecision, AuditCategories.VoiceReview, nameof(UserStatus.Rejected)),

            Row(staff.Id, AuditActions.TicketViewed, AuditCategories.Ticket),
            Row(staff.Id, AuditActions.TicketViewed, AuditCategories.Ticket),
            Row(staff.Id, AuditActions.TicketStatusChanged, AuditCategories.Ticket, nameof(SupportTicketStatus.Resolved)),

            Row(staff.Id, AuditActions.ReportViewed, AuditCategories.Report),
            Row(staff.Id, AuditActions.ReportViewed, AuditCategories.Report),
            Row(staff.Id, AuditActions.ReportViewed, AuditCategories.Report),
            Row(staff.Id, AuditActions.ReportStatusChanged, AuditCategories.Report, nameof(ReportStatus.Resolved)),
            Row(staff.Id, AuditActions.ReportStatusChanged, AuditCategories.Report, nameof(ReportStatus.Rejected)),
            Row(staff.Id, AuditActions.ReportStatusChanged, AuditCategories.Report, nameof(ReportStatus.Open)),
            Row(staff.Id, AuditActions.ReportActionTaken, AuditCategories.Report, nameof(AdminReportAction.WarnUser)),
            Row(staff.Id, AuditActions.ReportActionTaken, AuditCategories.Report, nameof(AdminReportAction.Mute24h)),
            Row(staff.Id, AuditActions.ReportActionTaken, AuditCategories.Report, nameof(AdminReportAction.Mute24h)),
            Row(staff.Id, AuditActions.ReportActionTaken, AuditCategories.Report, nameof(AdminReportAction.BanUser)),
            Row(staff.Id, AuditActions.ReportActionTaken, AuditCategories.Report, nameof(AdminReportAction.RejectReport)),

            Row(staff.Id, AuditActions.UserStatusChanged, AuditCategories.UserModeration, nameof(UserStatus.Banned)),
            Row(staff.Id, AuditActions.UserStatusChanged, AuditCategories.UserModeration, nameof(UserStatus.Active)),
            Row(staff.Id, AuditActions.UserDeviceAndEmailBlocked, AuditCategories.UserModeration, nameof(UserStatus.Banned)),

            Row(staff.Id, AuditActions.SupportChatClaimed, AuditCategories.SupportChat),
            Row(staff.Id, AuditActions.SupportChatReplied, AuditCategories.SupportChat),
            Row(staff.Id, AuditActions.SupportChatReplied, AuditCategories.SupportChat),
            Row(staff.Id, AuditActions.SupportChatClosed, AuditCategories.SupportChat),

            Row(staff.Id, AuditActions.RoomCreated, AuditCategories.Room),
            Row(staff.Id, AuditActions.RoomStarted, AuditCategories.Room),
            Row(staff.Id, AuditActions.RoomEnded, AuditCategories.Room),
            Row(staff.Id, AuditActions.RoomUserApproved, AuditCategories.Room),
            Row(staff.Id, AuditActions.RoomStageApproved, AuditCategories.Room),
            Row(staff.Id, AuditActions.RoomMovedToAudience, AuditCategories.Room),
            Row(staff.Id, AuditActions.RoomUserKicked, AuditCategories.Room),
            Row(staff.Id, AuditActions.RoomUserMuted, AuditCategories.Room),
            Row(staff.Id, AuditActions.RoomExtraTimeGranted, AuditCategories.Room)
        };

        var expectedLastStaffActivity = rows.Max(r => r.OccurredAtUtc);
        var expectedTotalStaffRows = rows.Count;

        rows.Add(new AuditLog
        {
            ActorUserId = otherActor.Id,
            Action = AuditActions.RoomCreated,
            Category = AuditCategories.Room,
            OccurredAtUtc = expectedLastStaffActivity.AddHours(5)
        });

        await SeedAuditLogsAsync(rows.ToArray());

        var (service, scope) = CreateServiceScope();
        using (scope)
        {
            var result = await service.GetStaffActivityAsync(staff.Id, new StaffActivityQueryDto
            {
                Category = AuditCategories.Room,
                PageSize = 5
            });

            Assert.True(result.Succeeded);
            var summary = result.Data!.Summary;

            Assert.Equal(expectedTotalStaffRows, summary.TotalActions);
            Assert.Equal(expectedLastStaffActivity, summary.LastActivityAtUtc);
            Assert.Equal(2, summary.VoiceUrlsIssued);
            Assert.Equal(4, summary.VoiceReviews);
            Assert.Equal(2, summary.VoiceApprovals);
            Assert.Equal(1, summary.VoiceReRecords);
            Assert.Equal(1, summary.VoiceRejections);
            Assert.Equal(2, summary.TicketsViewed);
            Assert.Equal(1, summary.TicketStatusChanges);
            Assert.Equal(3, summary.ReportsViewed);
            Assert.Equal(3, summary.ReportStatusChanges);
            Assert.Equal(5, summary.ReportsResolved);
            Assert.Equal(2, summary.ReportsRejected);
            Assert.Equal(1, summary.WarnsIssued);
            Assert.Equal(2, summary.MutesIssued);
            Assert.Equal(3, summary.BansIssued);
            Assert.Equal(2, summary.UserStatusChanges);
            Assert.Equal(1, summary.ChatsClaimed);
            Assert.Equal(2, summary.ChatRepliesSent);
            Assert.Equal(1, summary.ChatsClosed);
            Assert.Equal(1, summary.RoomsCreated);
            Assert.Equal(1, summary.RoomsStarted);
            Assert.Equal(1, summary.RoomsEnded);
            Assert.Equal(6, summary.RoomModerationActions);

            Assert.Equal(9, result.Data.TotalCount);
            Assert.Equal(5, result.Data.Items.Count);
            Assert.Equal(2, result.Data.TotalPages);
        }
    }

    [Fact]
    public async Task GetStaffActivityAsync_ResolvesCurrentTargetIdentityAndFallsBackToTargetDisplayForDeletedUsers()
    {
        var reviewer = await SeedUserAsync("Blind", "Reviewer", "reviewer@cocorra.test", AppRoles.RecordReviewer);
        var liveTarget = await SeedUserAsync("Current", "Identity", "current@cocorra.test", AppRoles.User);
        var deletedTargetId = Guid.NewGuid();
        var t = new DateTime(2026, 8, 1, 14, 0, 0, DateTimeKind.Utc);

        await SeedAuditLogsAsync(
            new AuditLog
            {
                ActorUserId = reviewer.Id,
                Action = AuditActions.VoiceReviewDecision,
                Category = AuditCategories.VoiceReview,
                EntityType = AuditEntityTypes.User,
                EntityId = liveTarget.Id.ToString(),
                TargetUserId = liveTarget.Id,
                TargetDisplay = "stale@old.test",
                FromState = "Pending",
                ToState = "Active",
                Reason = "Clear voice",
                DetailsJson = "{\"shortRef\":\"VR-AABBCCDD\"}",
                OccurredAtUtc = t.AddMinutes(2)
            },
            new AuditLog
            {
                ActorUserId = reviewer.Id,
                Action = AuditActions.VoiceReviewDecision,
                Category = AuditCategories.VoiceReview,
                EntityType = AuditEntityTypes.User,
                EntityId = deletedTargetId.ToString(),
                TargetUserId = deletedTargetId,
                TargetDisplay = "deleted_user@legacy.test",
                FromState = "Pending",
                ToState = "Rejected",
                Reason = "Invalid recording",
                DetailsJson = "{not-valid-json",
                OccurredAtUtc = t.AddMinutes(1)
            });

        var (service, scope) = CreateServiceScope();
        using (scope)
        {
            var result = await service.GetStaffActivityAsync(reviewer.Id, new StaffActivityQueryDto());

            Assert.True(result.Succeeded);
            var items = result.Data!.Items;
            Assert.Equal(2, items.Count);

            var liveItem = items[0];
            Assert.Equal(liveTarget.Id, liveItem.TargetUserId);
            Assert.Equal("Current Identity", liveItem.TargetName);
            Assert.Equal("current@cocorra.test", liveItem.TargetEmail);
            Assert.Equal("Pending", liveItem.FromState);
            Assert.Equal("Active", liveItem.ToState);
            Assert.Equal("Clear voice", liveItem.Reason);
            Assert.True(liveItem.Details.HasValue);
            Assert.Equal("VR-AABBCCDD", liveItem.Details!.Value.GetProperty("shortRef").GetString());

            var deletedItem = items[1];
            Assert.Equal(deletedTargetId, deletedItem.TargetUserId);
            Assert.Equal("deleted_user@legacy.test", deletedItem.TargetName);
            Assert.Null(deletedItem.TargetEmail);
            Assert.Null(deletedItem.Details);
        }
    }
}
