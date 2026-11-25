using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Cocorra.BLL.Services.AdminService;
using Cocorra.BLL.Services.AuditLogs;
using Cocorra.BLL.Services.Email;
using Cocorra.BLL.Services.EventTracking;
using Cocorra.BLL.Services.NotificationService;
using Cocorra.BLL.Services.Upload;
using Cocorra.DAL.Data;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;
using Cocorra.DAL.Repository.AuditLogRepository;
using Cocorra.DAL.Repository.BlockedDevicesRepository;
using Cocorra.DAL.Repository.NotificationRepository;
using Cocorra.DAL.Repository.UserRepository;
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

public class AdminServiceModeratorScopeTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _services;
    private readonly IConfiguration _configuration;
    private readonly Mock<IUploadVoice> _uploadVoiceMock = new();
    private readonly Mock<IEmailService> _emailServiceMock = new();
    private readonly Mock<IPushNotificationService> _pushServiceMock = new();
    private readonly Mock<IBlockedDevicesRepository> _blockedDevicesRepoMock = new();
    private readonly Mock<IEventTracker> _eventTrackerMock = new();
    private readonly Mock<INotificationRepository> _notificationRepoMock = new();

    public AdminServiceModeratorScopeTests()
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
        UserStatus status,
        string role,
        int age = 26,
        string mbti = "ENFP")
    {
        using var scope = _services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = email,
            Email = email,
            FirstName = firstName,
            LastName = lastName,
            Age = age,
            MBTI = mbti,
            Status = status,
            CreatedAt = DateTime.UtcNow
        };
        var created = await userManager.CreateAsync(user);
        Assert.True(created.Succeeded);
        var addedRole = await userManager.AddToRoleAsync(user, role);
        Assert.True(addedRole.Succeeded);
        return (await userManager.FindByIdAsync(user.Id.ToString()))!;
    }

    private (AdminService service, AppDbContext db, IServiceScope scope) CreateServiceScope()
    {
        var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var userRepository = new UserRepository(db);
        var auditLogRepo = new AuditLogRepository(db);
        var httpContextAccessor = new Mock<IHttpContextAccessor>();
        var auditLogService = new AuditLogService(auditLogRepo, httpContextAccessor.Object, NullLogger<AuditLogService>.Instance);

        var service = new AdminService(
            userManager,
            _uploadVoiceMock.Object,
            _configuration,
            _emailServiceMock.Object,
            userRepository,
            _pushServiceMock.Object,
            _blockedDevicesRepoMock.Object,
            _eventTrackerMock.Object,
            db,
            _notificationRepoMock.Object,
            new FakePrivateFileUrlIssuer(),
            auditLogService);

        return (service, db, scope);
    }

    private async Task<ApplicationUser> ReloadUserAsync(Guid userId)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId);
    }

    private async Task<List<AuditLog>> ReadAllAuditLogsAsync()
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.AuditLogs.AsNoTracking().OrderBy(a => a.Id).ToListAsync();
    }

    [Fact]
    public async Task GetAllUsersAsync_AsModerator_ExcludesAdminsFromRowsAndTotalCountAndMasksPii()
    {
        var admin = await SeedUserAsync("Alice", "Admin", "admin@cocorra.test", UserStatus.Active, AppRoles.Admin, age: 35, mbti: "ENTJ");
        var plain = await SeedUserAsync("Bob", "Member", "bob@cocorra.test", UserStatus.Active, AppRoles.User, age: 27, mbti: "INFP");
        var coach = await SeedUserAsync("Cara", "Coach", "cara@cocorra.test", UserStatus.Active, AppRoles.Coach, age: 31, mbti: "ENFJ");

        var (service, _, scope) = CreateServiceScope();
        using (scope)
        {
            var modResult = await service.GetAllUsersAsync(search: null, page: 1, pageSize: 10, callerIsAdmin: false);
            var modUsers = modResult.Data!.ToList();

            Assert.True(modResult.Succeeded);
            Assert.Equal(2, modResult.TotalCount);
            Assert.Equal(2, modUsers.Count);
            Assert.DoesNotContain(modUsers, u => u.Id == admin.Id.ToString());
            Assert.Contains(modUsers, u => u.Id == plain.Id.ToString());
            Assert.Contains(modUsers, u => u.Id == coach.Id.ToString());
            Assert.All(modUsers, u =>
            {
                Assert.Null(u.Email);
                Assert.Null(u.Age);
                Assert.Null(u.MBTI);
            });

            var adminResult = await service.GetAllUsersAsync(search: null, page: 1, pageSize: 10, callerIsAdmin: true);
            var adminUsers = adminResult.Data!.ToList();

            Assert.Equal(3, adminResult.TotalCount);
            Assert.Equal(3, adminUsers.Count);
            var bobForAdmin = adminUsers.Single(u => u.Id == plain.Id.ToString());
            Assert.Equal("bob@cocorra.test", bobForAdmin.Email);
            Assert.Equal(27, bobForAdmin.Age);
            Assert.Equal("INFP", bobForAdmin.MBTI);
        }
    }

    [Fact]
    public async Task GetAllUsersAsync_AsModerator_CannotSearchByEmailButCanSearchByName()
    {
        await SeedUserAsync("Nadia", "Kamel", "unique_oracle_email@cocorra.test", UserStatus.Active, AppRoles.User);

        var (service, _, scope) = CreateServiceScope();
        using (scope)
        {
            var modEmailSearch = await service.GetAllUsersAsync("unique_oracle_email", 1, 10, callerIsAdmin: false);
            Assert.Equal(0, modEmailSearch.TotalCount);
            Assert.Empty(modEmailSearch.Data!);

            var modNameSearch = await service.GetAllUsersAsync("Nadia", 1, 10, callerIsAdmin: false);
            Assert.Equal(1, modNameSearch.TotalCount);
            Assert.Single(modNameSearch.Data!);

            var adminEmailSearch = await service.GetAllUsersAsync("unique_oracle_email", 1, 10, callerIsAdmin: true);
            Assert.Equal(1, adminEmailSearch.TotalCount);
            Assert.Single(adminEmailSearch.Data!);
        }
    }

    [Fact]
    public async Task GetUserByIdAsync_AdminTargetAndPlainTarget_EnforcesVisibilityAndMasking()
    {
        var admin = await SeedUserAsync("Root", "Admin", "root@cocorra.test", UserStatus.Active, AppRoles.Admin, age: 40, mbti: "INTJ");
        var plain = await SeedUserAsync("Omar", "User", "omar@cocorra.test", UserStatus.Active, AppRoles.User, age: 24, mbti: "ISTP");

        var (service, _, scope) = CreateServiceScope();
        using (scope)
        {
            var modAdminDetail = await service.GetUserByIdAsync(admin.Id, callerIsAdmin: false);
            Assert.False(modAdminDetail.Succeeded);
            Assert.Equal(HttpStatusCode.NotFound, modAdminDetail.StatusCode);

            var modPlainDetail = await service.GetUserByIdAsync(plain.Id, callerIsAdmin: false);
            Assert.True(modPlainDetail.Succeeded);
            Assert.Null(modPlainDetail.Data!.Email);
            Assert.Null(modPlainDetail.Data.Age);
            Assert.Null(modPlainDetail.Data.MBTI);

            var adminSelfDetail = await service.GetUserByIdAsync(admin.Id, callerIsAdmin: true);
            Assert.True(adminSelfDetail.Succeeded);
            Assert.Equal("root@cocorra.test", adminSelfDetail.Data!.Email);
            Assert.Equal(40, adminSelfDetail.Data.Age);
            Assert.Equal("INTJ", adminSelfDetail.Data.MBTI);
        }
    }

    [Theory]
    [InlineData(AppRoles.Admin)]
    [InlineData(AppRoles.Moderator)]
    [InlineData(AppRoles.RecordReviewer)]
    [InlineData(AppRoles.Coach)]
    public async Task ChangeUserStatusAsync_AsModeratorTargetingProtectedRole_Returns403AndLeavesStateAndAuditUntouched(string targetRole)
    {
        var actorId = Guid.NewGuid();
        var target = await SeedUserAsync("Staff", targetRole, $"{targetRole.ToLowerInvariant()}@cocorra.test", UserStatus.Active, targetRole);
        var originalStamp = target.SecurityStamp;

        var (service, _, scope) = CreateServiceScope();
        using (scope)
        {
            var result = await service.ChangeUserStatusAsync(target.Id, UserStatus.Banned, "Attempted sanction", actorId, callerIsAdmin: false);

            Assert.False(result.Succeeded);
            Assert.Equal(HttpStatusCode.Forbidden, result.StatusCode);
            Assert.False(result.SessionRevoked);
        }

        var reloaded = await ReloadUserAsync(target.Id);
        Assert.Equal(UserStatus.Active, reloaded.Status);
        Assert.Equal(originalStamp, reloaded.SecurityStamp);
        Assert.Empty(await ReadAllAuditLogsAsync());
    }

    [Theory]
    [InlineData(UserStatus.Pending)]
    [InlineData(UserStatus.ReRecord)]
    public async Task ChangeUserStatusAsync_AsModeratorTargetingVoiceVerificationUser_Returns403AndLeavesStateUntouched(UserStatus initialStatus)
    {
        var actorId = Guid.NewGuid();
        var target = await SeedUserAsync("Applicant", "One", "applicant@cocorra.test", initialStatus, AppRoles.User);
        var originalStamp = target.SecurityStamp;

        var (service, _, scope) = CreateServiceScope();
        using (scope)
        {
            var result = await service.ChangeUserStatusAsync(target.Id, UserStatus.Active, "Approving early", actorId, callerIsAdmin: false);

            Assert.False(result.Succeeded);
            Assert.Equal(HttpStatusCode.Forbidden, result.StatusCode);
        }

        var reloaded = await ReloadUserAsync(target.Id);
        Assert.Equal(initialStatus, reloaded.Status);
        Assert.Equal(originalStamp, reloaded.SecurityStamp);
        Assert.Empty(await ReadAllAuditLogsAsync());
    }

    [Theory]
    [InlineData(UserStatus.Rejected)]
    [InlineData(UserStatus.ReRecord)]
    [InlineData(UserStatus.Pending)]
    public async Task ChangeUserStatusAsync_AsModeratorSettingDisallowedStatus_Returns403AndLeavesStateUntouched(UserStatus disallowedStatus)
    {
        var actorId = Guid.NewGuid();
        var target = await SeedUserAsync("Plain", "Member", "plain@cocorra.test", UserStatus.Active, AppRoles.User);
        var originalStamp = target.SecurityStamp;

        var (service, _, scope) = CreateServiceScope();
        using (scope)
        {
            var result = await service.ChangeUserStatusAsync(target.Id, disallowedStatus, "Disallowed transition", actorId, callerIsAdmin: false);

            Assert.False(result.Succeeded);
            Assert.Equal(HttpStatusCode.Forbidden, result.StatusCode);
        }

        var reloaded = await ReloadUserAsync(target.Id);
        Assert.Equal(UserStatus.Active, reloaded.Status);
        Assert.Equal(originalStamp, reloaded.SecurityStamp);
        Assert.Empty(await ReadAllAuditLogsAsync());
    }

    [Fact]
    public async Task ChangeUserStatusAsync_AsModeratorBanningPlainActiveUser_PersistsBanRotatesStampAndWritesAuditRow()
    {
        var moderatorId = Guid.NewGuid();
        var target = await SeedUserAsync("Plain", "Offender", "offender@cocorra.test", UserStatus.Active, AppRoles.User);
        var originalStamp = target.SecurityStamp;

        var (service, _, scope) = CreateServiceScope();
        using (scope)
        {
            var result = await service.ChangeUserStatusAsync(
                target.Id,
                UserStatus.Banned,
                "  Confirmed harassment in voice room  ",
                moderatorId,
                callerIsAdmin: false);

            Assert.True(result.Succeeded);
            Assert.Equal(HttpStatusCode.OK, result.StatusCode);
            Assert.True(result.SessionRevoked);
        }

        var reloaded = await ReloadUserAsync(target.Id);
        Assert.Equal(UserStatus.Banned, reloaded.Status);
        Assert.True(reloaded.LockoutEnabled);
        Assert.NotEqual(originalStamp, reloaded.SecurityStamp);

        var auditRow = Assert.Single(await ReadAllAuditLogsAsync());
        Assert.Equal(AuditActions.UserStatusChanged, auditRow.Action);
        Assert.Equal(moderatorId, auditRow.ActorUserId);
        Assert.Equal(target.Id, auditRow.TargetUserId);
        Assert.Equal(AuditCategories.UserModeration, auditRow.Category);
        Assert.Equal(AuditEntityTypes.User, auditRow.EntityType);
        Assert.Equal(target.Id.ToString(), auditRow.EntityId);
        Assert.Equal("Active", auditRow.FromState);
        Assert.Equal("Banned", auditRow.ToState);
        Assert.Equal("Confirmed harassment in voice room", auditRow.Reason);

        var (unbanService, _, unbanScope) = CreateServiceScope();
        using (unbanScope)
        {
            var unbanResult = await unbanService.ChangeUserStatusAsync(
                target.Id,
                UserStatus.Active,
                "Appeal accepted after review",
                moderatorId,
                callerIsAdmin: false);

            Assert.True(unbanResult.Succeeded);
            Assert.Equal(HttpStatusCode.OK, unbanResult.StatusCode);
            Assert.False(unbanResult.SessionRevoked);
        }

        Assert.Equal(UserStatus.Active, (await ReloadUserAsync(target.Id)).Status);
        var allAudits = await ReadAllAuditLogsAsync();
        Assert.Equal(2, allAudits.Count);
        Assert.Equal("Banned", allAudits[1].FromState);
        Assert.Equal("Active", allAudits[1].ToState);
        Assert.Equal("Appeal accepted after review", allAudits[1].Reason);
    }

    [Fact]
    public async Task ChangeUserStatusAsync_AsAdmin_RotatesStampForStaffAndBansAndRejectionsButNotPlainApprovals()
    {
        var adminId = Guid.NewGuid();
        var plainPending = await SeedUserAsync("Plain", "Approve", "approve@cocorra.test", UserStatus.Pending, AppRoles.User);
        var plainReject = await SeedUserAsync("Plain", "Reject", "reject@cocorra.test", UserStatus.Pending, AppRoles.User);
        var plainBan = await SeedUserAsync("Plain", "Ban", "ban@cocorra.test", UserStatus.Active, AppRoles.User);
        var modTarget = await SeedUserAsync("Mod", "Staff", "modstaff@cocorra.test", UserStatus.Banned, AppRoles.Moderator);

        var (service, _, scope) = CreateServiceScope();
        using (scope)
        {
            var approveRes = await service.ChangeUserStatusAsync(plainPending.Id, UserStatus.Active, "Clear recording", adminId, callerIsAdmin: true);
            Assert.True(approveRes.Succeeded);
            Assert.False(approveRes.SessionRevoked);

            var rejectRes = await service.ChangeUserStatusAsync(plainReject.Id, UserStatus.Rejected, "Fake recording", adminId, callerIsAdmin: true);
            Assert.True(rejectRes.Succeeded);
            Assert.True(rejectRes.SessionRevoked);

            var banRes = await service.ChangeUserStatusAsync(plainBan.Id, UserStatus.Banned, "Repeated spam", adminId, callerIsAdmin: true);
            Assert.True(banRes.Succeeded);
            Assert.True(banRes.SessionRevoked);

            var staffRes = await service.ChangeUserStatusAsync(modTarget.Id, UserStatus.Active, "Reinstated moderator", adminId, callerIsAdmin: true);
            Assert.True(staffRes.Succeeded);
            Assert.True(staffRes.SessionRevoked);
        }

        Assert.Equal(plainPending.SecurityStamp, (await ReloadUserAsync(plainPending.Id)).SecurityStamp);
        Assert.NotEqual(plainReject.SecurityStamp, (await ReloadUserAsync(plainReject.Id)).SecurityStamp);
        Assert.NotEqual(plainBan.SecurityStamp, (await ReloadUserAsync(plainBan.Id)).SecurityStamp);
        Assert.NotEqual(modTarget.SecurityStamp, (await ReloadUserAsync(modTarget.Id)).SecurityStamp);
    }

    public static TheoryData<string?, bool> ReasonBoundaryCases => new()
    {
        { null, false },
        { "", false },
        { "   ", false },
        { "ab", false },
        { "  ab  ", false },
        { new string('x', 1001), false },
        { "abc", true },
        { "  abc  ", true },
        { new string('x', 1000), true }
    };

    [Theory]
    [MemberData(nameof(ReasonBoundaryCases))]
    public async Task ChangeUserStatusAsync_ReasonBoundaries_EnforcesTrimmed3To1000Characters(string? reason, bool shouldSucceed)
    {
        var adminId = Guid.NewGuid();
        var target = await SeedUserAsync("Boundary", "User", $"b_{Guid.NewGuid():N}@cocorra.test", UserStatus.Active, AppRoles.User);
        var originalStamp = target.SecurityStamp;

        var (service, _, scope) = CreateServiceScope();
        using (scope)
        {
            var result = await service.ChangeUserStatusAsync(target.Id, UserStatus.Banned, reason, adminId, callerIsAdmin: true);

            Assert.Equal(shouldSucceed, result.Succeeded);
            Assert.Equal(shouldSucceed ? HttpStatusCode.OK : HttpStatusCode.BadRequest, result.StatusCode);
        }

        var reloaded = await ReloadUserAsync(target.Id);
        var audits = await ReadAllAuditLogsAsync();

        if (shouldSucceed)
        {
            Assert.Equal(UserStatus.Banned, reloaded.Status);
            var row = Assert.Single(audits);
            Assert.Equal(reason!.Trim(), row.Reason);
        }
        else
        {
            Assert.Equal(UserStatus.Active, reloaded.Status);
            Assert.Equal(originalStamp, reloaded.SecurityStamp);
            Assert.Empty(audits);
        }
    }

    [Fact]
    public async Task ChangeUserStatusAsync_SelfChange_RefusedAndPersistsNothing()
    {
        var actor = await SeedUserAsync("Self", "Admin", "self@cocorra.test", UserStatus.Active, AppRoles.Admin);
        var originalStamp = actor.SecurityStamp;

        var (service, _, scope) = CreateServiceScope();
        using (scope)
        {
            var result = await service.ChangeUserStatusAsync(actor.Id, UserStatus.Banned, "Self ban test", actor.Id, callerIsAdmin: true);

            Assert.False(result.Succeeded);
            Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        }

        var reloaded = await ReloadUserAsync(actor.Id);
        Assert.Equal(UserStatus.Active, reloaded.Status);
        Assert.Equal(originalStamp, reloaded.SecurityStamp);
        Assert.Empty(await ReadAllAuditLogsAsync());
    }

    [Theory]
    [InlineData(UserStatus.ReRecord)]
    [InlineData(UserStatus.Pending)]
    public async Task ChangeUserStatusAsync_PlainUserDemotedFromActive_RotatesSecurityStampAndFlagsSessionRevoked(UserStatus demotedStatus)
    {
        var adminId = Guid.NewGuid();
        var plainActive = await SeedUserAsync("Plain", "Active", $"demote_{demotedStatus}@cocorra.test", UserStatus.Active, AppRoles.User);
        var originalStamp = plainActive.SecurityStamp;

        var (service, _, scope) = CreateServiceScope();
        using (scope)
        {
            var result = await service.ChangeUserStatusAsync(plainActive.Id, demotedStatus, "Re-verification requested", adminId, callerIsAdmin: true);

            Assert.True(result.Succeeded);
            Assert.True(result.SessionRevoked);
        }

        var reloaded = await ReloadUserAsync(plainActive.Id);
        Assert.Equal(demotedStatus, reloaded.Status);
        Assert.NotEqual(originalStamp, reloaded.SecurityStamp);

        var audit = Assert.Single(await ReadAllAuditLogsAsync());
        Assert.Equal(AuditActions.UserStatusChanged, audit.Action);
        Assert.Equal("Active", audit.FromState);
        Assert.Equal(demotedStatus.ToString(), audit.ToState);
    }

    [Theory]
    [InlineData(UserStatus.Banned)]
    [InlineData(UserStatus.Rejected)]
    [InlineData(UserStatus.Pending)]
    [InlineData(UserStatus.ReRecord)]
    public async Task ChangeUserStatusAsync_ModeratorDemotedOrBanned_ReleasesActiveClaimedSupportChats(UserStatus nonActiveStatus)
    {
        var adminId = Guid.NewGuid();
        var mod = await SeedUserAsync("Staff", "Mod", $"mod_{nonActiveStatus}@cocorra.test", UserStatus.Active, AppRoles.Moderator);
        var otherMod = await SeedUserAsync("Other", "Mod", $"other_{nonActiveStatus}@cocorra.test", UserStatus.Active, AppRoles.Moderator);

        Guid activeChatId;
        Guid closedChatId;
        Guid otherChatId;
        using (var seedScope = _services.CreateScope())
        {
            var seedDb = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            activeChatId = await SeedSupportChatAsync(seedDb, mod.Id.ToString(), SupportChatStatus.Active);
            closedChatId = await SeedSupportChatAsync(seedDb, mod.Id.ToString(), SupportChatStatus.Closed, closedAt: DateTime.UtcNow);
            otherChatId = await SeedSupportChatAsync(seedDb, otherMod.Id.ToString(), SupportChatStatus.Active);
        }

        var (service, _, scope) = CreateServiceScope();
        using (scope)
        {
            var result = await service.ChangeUserStatusAsync(mod.Id, nonActiveStatus, "Staff status suspended", adminId, callerIsAdmin: true);
            Assert.True(result.Succeeded);
        }

        using var verifyScope = _services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();

        var releasedChat = await verifyDb.SupportChats.AsNoTracking().SingleAsync(c => c.Id == activeChatId);
        Assert.Equal(SupportChatStatus.Pending, releasedChat.Status);
        Assert.Null(releasedChat.AdminId);

        var closedChat = await verifyDb.SupportChats.AsNoTracking().SingleAsync(c => c.Id == closedChatId);
        Assert.Equal(SupportChatStatus.Closed, closedChat.Status);
        Assert.Equal(mod.Id.ToString(), closedChat.AdminId);

        var otherChat = await verifyDb.SupportChats.AsNoTracking().SingleAsync(c => c.Id == otherChatId);
        Assert.Equal(SupportChatStatus.Active, otherChat.Status);
        Assert.Equal(otherMod.Id.ToString(), otherChat.AdminId);
    }

    [Theory]
    [InlineData(0, -1, 1, 1)]
    [InlineData(-3, 0, 1, 1)]
    [InlineData(1, 10000, 1, 100)]
    public async Task GetAllUsersAsync_OutOfRangePaging_ClampsPageAndPageSizeWithoutException(
        int requestedPage, int requestedPageSize, int expectedPage, int expectedPageSize)
    {
        await SeedUserAsync("User", "One", "u1@cocorra.test", UserStatus.Active, AppRoles.User);
        await SeedUserAsync("User", "Two", "u2@cocorra.test", UserStatus.Active, AppRoles.User);

        var (service, _, scope) = CreateServiceScope();
        using (scope)
        {
            var result = await service.GetAllUsersAsync(null, requestedPage, requestedPageSize, callerIsAdmin: true);

            Assert.True(result.Succeeded);
            Assert.Equal(expectedPage, result.CurrentPage);
            Assert.Equal(expectedPageSize, result.PageSize);
            Assert.Equal(2, result.TotalCount);
            Assert.Equal(Math.Min(2, expectedPageSize), result.Data!.Count());
        }
    }

    private static async Task<Guid> SeedSupportChatAsync(
        AppDbContext db,
        string? adminId,
        SupportChatStatus status,
        DateTime? closedAt = null)
    {
        var chatId = Guid.NewGuid();
        var chatIdUpper = chatId.ToString().ToUpperInvariant();
        var userId = Guid.NewGuid().ToString();
        var created = DateTime.UtcNow;
        var statusInt = (int)status;

        await db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO SupportChats (Id, UserId, AdminId, Status, CreatedAt, ClosedAt, RowVersion)
            VALUES ({chatIdUpper}, {userId}, {adminId}, {statusInt}, {created}, {closedAt}, X'0000000000000001')");

        return chatId;
    }
}
