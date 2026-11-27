using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Cocorra.BLL.Services.AdminService;
using Cocorra.BLL.Services.AuditLogs;
using Cocorra.BLL.Services.Email;
using Cocorra.BLL.Services.EventTracking;
using Cocorra.BLL.Services.NotificationService;
using Cocorra.BLL.Services.Storage;
using Cocorra.BLL.Services.Upload;
using Cocorra.BLL.Services.VoiceReviewService;
using Cocorra.DAL.Data;
using Cocorra.DAL.DTOS.VoiceReviewDto;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;
using Cocorra.DAL.Repository.AuditLogRepository;
using Cocorra.DAL.Repository.BlockedDevicesRepository;
using Cocorra.DAL.Repository.NotificationRepository;
using Cocorra.DAL.Repository.UserRepository;
using Cocorra.Tests.Helpers;
using Microsoft.AspNetCore.DataProtection;
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

public class VoiceReviewServiceTests : IDisposable
{
    private const string ProtectorPurpose = "Cocorra.VoiceReview.Ref.v1";

    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _services;
    private readonly EphemeralDataProtectionProvider _dataProtectionProvider = new();
    private readonly IConfiguration _configuration;
    private readonly FakePrivateFileUrlIssuer _fileUrls = new();
    private readonly Mock<IUploadVoice> _uploadVoiceMock = new();
    private readonly Mock<IEmailService> _emailServiceMock = new();
    private readonly Mock<IPushNotificationService> _pushServiceMock = new();
    private readonly Mock<IBlockedDevicesRepository> _blockedDevicesRepoMock = new();
    private readonly Mock<IEventTracker> _eventTrackerMock = new();
    private readonly Mock<INotificationRepository> _notificationRepoMock = new();

    public VoiceReviewServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["JWTSetting:securityKey"] = "TestVoiceReviewSigningKey_AtLeast32Chars_123456!",
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
        string? voicePath,
        DateTime createdAtUtc,
        int age = 29,
        string? mbti = "INTJ",
        string? profilePicture = "avatars/secret.png",
        string? bio = "Private bio text")
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
            ProfilePicturePath = profilePicture,
            Bio = bio,
            Status = status,
            VoiceVerificationPath = voicePath,
            CreatedAt = createdAtUtc
        };
        var created = await userManager.CreateAsync(user);
        Assert.True(created.Succeeded);
        var addedRole = await userManager.AddToRoleAsync(user, role);
        Assert.True(addedRole.Succeeded);
        return (await userManager.FindByIdAsync(user.Id.ToString()))!;
    }

    private (VoiceReviewService service, IServiceScope scope) CreateServiceScope(
        IDataProtectionProvider? providerOverride = null,
        FakePrivateFileUrlIssuer? fileUrlsOverride = null)
    {
        var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var userRepo = new UserRepository(db);
        var auditRepo = new AuditLogRepository(db);
        var httpContextAccessor = new Mock<IHttpContextAccessor>();
        var auditLog = new AuditLogService(auditRepo, httpContextAccessor.Object, NullLogger<AuditLogService>.Instance);
        var fileUrls = fileUrlsOverride ?? _fileUrls;

        var adminService = new AdminService(
            userManager,
            _uploadVoiceMock.Object,
            _configuration,
            _emailServiceMock.Object,
            userRepo,
            _pushServiceMock.Object,
            _blockedDevicesRepoMock.Object,
            _eventTrackerMock.Object,
            db,
            _notificationRepoMock.Object,
            fileUrls,
            auditLog);

        var service = new VoiceReviewService(
            userRepo,
            userManager,
            adminService,
            fileUrls,
            auditLog,
            providerOverride ?? _dataProtectionProvider,
            _configuration);

        return (service, scope);
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
    public async Task GetPendingAsync_FiltersToPlainPendingWithVoiceOrdersOldestFirstClampsPagingAndLeaksNoPii()
    {
        var baseTime = new DateTime(2026, 5, 1, 10, 0, 0, DateTimeKind.Utc);
        var oldest = await SeedUserAsync(
            "SecretFirst",
            "SecretLast",
            "secret.applicant@cocorra.test",
            UserStatus.Pending,
            AppRoles.User,
            "voices/secret-oldest.m4a",
            baseTime,
            age: 37,
            mbti: "INFJ",
            profilePicture: "avatars/secret-avatar-88.jpg",
            bio: "Unique confidential applicant bio");

        var middle = await SeedUserAsync("Second", "User", "second@cocorra.test", UserStatus.Pending, AppRoles.User, "voices/second.m4a", baseTime.AddMinutes(5));
        var newest = await SeedUserAsync("Third", "User", "third@cocorra.test", UserStatus.Pending, AppRoles.User, "voices/third.m4a", baseTime.AddMinutes(10));

        await SeedUserAsync("Active", "User", "active@cocorra.test", UserStatus.Active, AppRoles.User, "voices/active.m4a", baseTime.AddMinutes(-5));
        await SeedUserAsync("ReRecord", "User", "rerecord@cocorra.test", UserStatus.ReRecord, AppRoles.User, "voices/rerecord.m4a", baseTime.AddMinutes(-4));
        await SeedUserAsync("Rejected", "User", "rejected@cocorra.test", UserStatus.Rejected, AppRoles.User, "voices/rejected.m4a", baseTime.AddMinutes(-3));
        await SeedUserAsync("NoVoice", "User", "novoice@cocorra.test", UserStatus.Pending, AppRoles.User, null, baseTime.AddMinutes(-2));
        await SeedUserAsync("ErrVoice", "User", "errvoice@cocorra.test", UserStatus.Pending, AppRoles.User, "Error:UploadFailed", baseTime.AddMinutes(-1));
        await SeedUserAsync("StaffMod", "User", "mod@cocorra.test", UserStatus.Pending, AppRoles.Moderator, "voices/mod.m4a", baseTime.AddMinutes(-6));
        await SeedUserAsync("StaffCoach", "User", "coach@cocorra.test", UserStatus.Pending, AppRoles.Coach, "voices/coach.m4a", baseTime.AddMinutes(-7));
        await SeedUserAsync("StaffRev", "User", "rev@cocorra.test", UserStatus.Pending, AppRoles.RecordReviewer, "voices/rev.m4a", baseTime.AddMinutes(-8));
        await SeedUserAsync("StaffAdmin", "User", "admin@cocorra.test", UserStatus.Pending, AppRoles.Admin, "voices/admin.m4a", baseTime.AddMinutes(-9));

        var (service, scope) = CreateServiceScope();
        using (scope)
        {
            var clamped = await service.GetPendingAsync(pageNumber: 0, pageSize: 200);
            Assert.True(clamped.Succeeded);
            Assert.Equal(1, clamped.CurrentPage);
            Assert.Equal(50, clamped.PageSize);
            Assert.Equal(3, clamped.TotalCount);

            var items = clamped.Data!.ToList();
            Assert.Equal(3, items.Count);
            Assert.Equal(new[] { baseTime, baseTime.AddMinutes(5), baseTime.AddMinutes(10) }, items.Select(i => i.SubmittedAtUtc));
            Assert.All(items, i =>
            {
                Assert.Equal("Pending", i.Status);
                Assert.True(i.HasVoice);
                Assert.Matches(new Regex("^VR-[0-9A-F]{8}$"), i.ShortRef);
                Assert.False(Guid.TryParse(i.ReviewId, out _));
            });

            var firstJson = JsonSerializer.Serialize(items[0], new JsonSerializerOptions(JsonSerializerDefaults.Web));
            using var doc = JsonDocument.Parse(firstJson);
            var properties = doc.RootElement.EnumerateObject().ToList();
            var propertyNames = properties.Select(p => p.Name).OrderBy(n => n).ToArray();
            Assert.Equal(new[] { "hasVoice", "reviewId", "shortRef", "status", "submittedAtUtc" }, propertyNames);
            Assert.DoesNotContain(properties, p => p.Value.ValueKind == JsonValueKind.Number);
            Assert.DoesNotContain(properties, p =>
                p.Value.ValueKind == JsonValueKind.String
                && (string.Equals(p.Value.GetString(), "37", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(p.Value.GetString(), "INFJ", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(p.Value.GetString(), AppRoles.User, StringComparison.OrdinalIgnoreCase)));

            var forbiddenSubstrings = new[]
            {
                oldest.Id.ToString(),
                oldest.Id.ToString("N"),
                "SecretFirst",
                "SecretLast",
                "secret.applicant@cocorra.test",
                "secret-avatar-88",
                "Unique confidential applicant bio",
                "secret-oldest.m4a"
            };
            foreach (var secret in forbiddenSubstrings)
            {
                Assert.DoesNotContain(secret, firstJson, StringComparison.OrdinalIgnoreCase);
            }

            var page2 = await service.GetPendingAsync(pageNumber: 2, pageSize: 2);
            Assert.Equal(3, page2.TotalCount);
            var page2Item = Assert.Single(page2.Data!);
            Assert.Equal(newest.CreatedAt, page2Item.SubmittedAtUtc);
            Assert.Equal(items[2].ShortRef, page2Item.ShortRef);
            Assert.NotEqual(middle.Id.ToString(), page2Item.ReviewId);
        }
    }

    [Fact]
    public async Task ShortRef_IsStableAcrossCallsAndDataProtectorInstances()
    {
        await SeedUserAsync("Stable", "Ref", "stable@cocorra.test", UserStatus.Pending, AppRoles.User, "voices/stable.m4a", DateTime.UtcNow);

        string shortRef1;
        string reviewId1;
        var (service1, scope1) = CreateServiceScope(new EphemeralDataProtectionProvider());
        using (scope1)
        {
            var item1 = Assert.Single((await service1.GetPendingAsync()).Data!);
            shortRef1 = item1.ShortRef;
            reviewId1 = item1.ReviewId;
        }

        string shortRef2;
        string reviewId2;
        var (service2, scope2) = CreateServiceScope(new EphemeralDataProtectionProvider());
        using (scope2)
        {
            var item2 = Assert.Single((await service2.GetPendingAsync()).Data!);
            shortRef2 = item2.ShortRef;
            reviewId2 = item2.ReviewId;
        }

        Assert.Matches(new Regex("^VR-[0-9A-F]{8}$"), shortRef1);
        Assert.Equal(shortRef1, shortRef2);
        Assert.NotEqual(reviewId1, reviewId2);
    }

    [Fact]
    public async Task InvalidTamperedWrongPurposeOrExpiredReviewId_Returns404OnVoiceUrlAndDecisionWithNoStateChange()
    {
        var user = await SeedUserAsync("Target", "Pending", "pending@cocorra.test", UserStatus.Pending, AppRoles.User, "voices/p.m4a", DateTime.UtcNow);
        var originalStamp = user.SecurityStamp;

        var (service, scope) = CreateServiceScope();
        using (scope)
        {
            var validReviewId = Assert.Single((await service.GetPendingAsync()).Data!).ReviewId;
            var tampered = validReviewId[..^1] + (validReviewId[^1] == 'A' ? 'B' : 'A');

            var expiredToken = _dataProtectionProvider
                .CreateProtector(ProtectorPurpose)
                .ToTimeLimitedDataProtector()
                .Protect(user.Id.ToString("D"), DateTimeOffset.UtcNow.AddMinutes(-5));

            var wrongPurposeToken = _dataProtectionProvider
                .CreateProtector("Cocorra.Different.Purpose")
                .ToTimeLimitedDataProtector()
                .Protect(user.Id.ToString("D"), TimeSpan.FromHours(1));

            var invalidTokens = new[]
            {
                "",
                "   ",
                "garbage-not-base64",
                user.Id.ToString("D"),
                tampered,
                expiredToken,
                wrongPurposeToken
            };

            foreach (var badToken in invalidTokens)
            {
                var urlResult = await service.GetVoiceUrlAsync(badToken);
                Assert.False(urlResult.Succeeded);
                Assert.Equal(HttpStatusCode.NotFound, urlResult.StatusCode);

                var decideResult = await service.DecideAsync(
                    badToken,
                    new VoiceReviewDecisionDto { Decision = UserStatus.Active },
                    Guid.NewGuid());
                Assert.False(decideResult.Succeeded);
                Assert.Equal(HttpStatusCode.NotFound, decideResult.StatusCode);
            }
        }

        Assert.Empty(_fileUrls.Calls);
        var reloaded = await ReloadUserAsync(user.Id);
        Assert.Equal(UserStatus.Pending, reloaded.Status);
        Assert.Equal(originalStamp, reloaded.SecurityStamp);
        Assert.Empty(await ReadAllAuditLogsAsync());
    }

    [Fact]
    public async Task GetVoiceUrlAsync_ValidPendingReviewId_IssuesPresignedUrl()
    {
        var user = await SeedUserAsync("Voice", "Owner", "voice@cocorra.test", UserStatus.Pending, AppRoles.User, "voices/owner.m4a", DateTime.UtcNow);

        var (service, scope) = CreateServiceScope();
        using (scope)
        {
            var item = Assert.Single((await service.GetPendingAsync()).Data!);
            var urlResult = await service.GetVoiceUrlAsync(item.ReviewId);

            Assert.True(urlResult.Succeeded);
            Assert.Equal(HttpStatusCode.OK, urlResult.StatusCode);
            Assert.Equal("signed:voices/owner.m4a", urlResult.Data!.Url);
            Assert.Equal(FakePrivateFileUrlIssuer.ExpiresAtUtc, urlResult.Data.ExpiresAtUtc);

            var call = Assert.Single(_fileUrls.Calls);
            Assert.Equal(PrivateFileKind.Voice, call.Kind);
            Assert.Equal("VoiceReview.VoiceUrl", call.Source);
            Assert.Equal(user.Id, Assert.Single(call.Files).TargetUserId);
        }
    }

    [Fact]
    public async Task DecideAsync_ActiveWithoutReason_ApprovesWithoutRotatingStampAndWritesSingleVoiceReviewAuditRow()
    {
        var reviewerId = Guid.NewGuid();
        var user = await SeedUserAsync("Approve", "Me", "approve@cocorra.test", UserStatus.Pending, AppRoles.User, "voices/ok.m4a", DateTime.UtcNow);
        var originalStamp = user.SecurityStamp;

        var (service, scope) = CreateServiceScope();
        using (scope)
        {
            var item = Assert.Single((await service.GetPendingAsync()).Data!);

            var overlongReason = await service.DecideAsync(
                item.ReviewId,
                new VoiceReviewDecisionDto { Decision = UserStatus.Active, Reason = new string('a', 1001) },
                reviewerId);
            Assert.False(overlongReason.Succeeded);
            Assert.Equal(HttpStatusCode.BadRequest, overlongReason.StatusCode);

            var result = await service.DecideAsync(
                item.ReviewId,
                new VoiceReviewDecisionDto { Decision = UserStatus.Active, Reason = null },
                reviewerId);

            Assert.True(result.Succeeded);
            Assert.Equal(HttpStatusCode.OK, result.StatusCode);
            Assert.Null(result.RevokedUserId);
        }

        var reloaded = await ReloadUserAsync(user.Id);
        Assert.Equal(UserStatus.Active, reloaded.Status);
        Assert.Null(reloaded.VoiceVerificationPath);
        Assert.Equal(originalStamp, reloaded.SecurityStamp);

        var audits = await ReadAllAuditLogsAsync();
        var audit = Assert.Single(audits);
        Assert.Equal(AuditActions.VoiceReviewDecision, audit.Action);
        Assert.Equal(AuditCategories.VoiceReview, audit.Category);
        Assert.Equal(AuditEntityTypes.User, audit.EntityType);
        Assert.Equal(user.Id.ToString(), audit.EntityId);
        Assert.Equal(reviewerId, audit.ActorUserId);
        Assert.Equal(user.Id, audit.TargetUserId);
        Assert.Equal("Pending", audit.FromState);
        Assert.Equal("Active", audit.ToState);
        Assert.Null(audit.Reason);
    }

    [Theory]
    [InlineData(UserStatus.ReRecord, false)]
    [InlineData(UserStatus.Rejected, true)]
    public async Task DecideAsync_ReRecordOrRejected_ValidatesReasonWritesVoiceReviewAuditOnlyAndSetsRevokedIdForRejected(
        UserStatus decision,
        bool expectRevoked)
    {
        var reviewerId = Guid.NewGuid();
        var user = await SeedUserAsync("Target", decision.ToString(), $"{decision}@cocorra.test", UserStatus.Pending, AppRoles.User, "voices/sample.m4a", DateTime.UtcNow);
        var originalStamp = user.SecurityStamp;

        var (service, scope) = CreateServiceScope();
        using (scope)
        {
            var item = Assert.Single((await service.GetPendingAsync()).Data!);

            foreach (var badReason in new string?[] { null, "", "   ", "ab", new string('r', 1001) })
            {
                var bad = await service.DecideAsync(item.ReviewId, new VoiceReviewDecisionDto { Decision = decision, Reason = badReason }, reviewerId);
                Assert.False(bad.Succeeded);
                Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
                Assert.Null(bad.RevokedUserId);
            }

            Assert.Empty(await ReadAllAuditLogsAsync());
            Assert.Equal(UserStatus.Pending, (await ReloadUserAsync(user.Id)).Status);

            var ok = await service.DecideAsync(
                item.ReviewId,
                new VoiceReviewDecisionDto { Decision = decision, Reason = "  Background noise too loud  " },
                reviewerId);

            Assert.True(ok.Succeeded);
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            Assert.Equal(expectRevoked ? user.Id : (Guid?)null, ok.RevokedUserId);
        }

        var reloaded = await ReloadUserAsync(user.Id);
        Assert.Equal(decision, reloaded.Status);
        if (expectRevoked)
            Assert.NotEqual(originalStamp, reloaded.SecurityStamp);
        else
            Assert.Equal(originalStamp, reloaded.SecurityStamp);

        var audits = await ReadAllAuditLogsAsync();
        var audit = Assert.Single(audits);
        Assert.Equal(AuditActions.VoiceReviewDecision, audit.Action);
        Assert.Equal(AuditCategories.VoiceReview, audit.Category);
        Assert.Equal("Pending", audit.FromState);
        Assert.Equal(decision.ToString(), audit.ToState);
        Assert.Equal("Background noise too loud", audit.Reason);
    }

    [Theory]
    [InlineData(UserStatus.Pending)]
    [InlineData(UserStatus.Banned)]
    public async Task DecideAsync_InvalidDecisionValue_Returns400AndWritesNothing(UserStatus invalidDecision)
    {
        var user = await SeedUserAsync("Bad", "Decision", "baddecision@cocorra.test", UserStatus.Pending, AppRoles.User, "voices/b.m4a", DateTime.UtcNow);

        var (service, scope) = CreateServiceScope();
        using (scope)
        {
            var item = Assert.Single((await service.GetPendingAsync()).Data!);
            var result = await service.DecideAsync(
                item.ReviewId,
                new VoiceReviewDecisionDto { Decision = invalidDecision, Reason = "Valid reason text" },
                Guid.NewGuid());

            Assert.False(result.Succeeded);
            Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        }

        Assert.Equal(UserStatus.Pending, (await ReloadUserAsync(user.Id)).Status);
        Assert.Empty(await ReadAllAuditLogsAsync());
    }

    [Fact]
    public async Task DecideAsync_NonPendingTarget_Returns409ConflictAndWritesNoExtraAuditRow()
    {
        var user = await SeedUserAsync("Already", "Decided", "decided@cocorra.test", UserStatus.Pending, AppRoles.User, "voices/d.m4a", DateTime.UtcNow);

        var (service, scope) = CreateServiceScope();
        using (scope)
        {
            var item = Assert.Single((await service.GetPendingAsync()).Data!);

            var first = await service.DecideAsync(item.ReviewId, new VoiceReviewDecisionDto { Decision = UserStatus.Active }, Guid.NewGuid());
            Assert.True(first.Succeeded);

            var second = await service.DecideAsync(
                item.ReviewId,
                new VoiceReviewDecisionDto { Decision = UserStatus.Rejected, Reason = "Too late" },
                Guid.NewGuid());

            Assert.False(second.Succeeded);
            Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
            Assert.Null(second.RevokedUserId);
        }

        Assert.Equal(UserStatus.Active, (await ReloadUserAsync(user.Id)).Status);
        var audit = Assert.Single(await ReadAllAuditLogsAsync());
        Assert.Equal("Active", audit.ToState);
    }

    [Fact]
    public async Task DecideAsync_TargetPromotedToStaffBeforeDecision_Returns403AndGetVoiceUrlReturns404()
    {
        var user = await SeedUserAsync("Promoted", "Staff", "promoted@cocorra.test", UserStatus.Pending, AppRoles.User, "voices/staff.m4a", DateTime.UtcNow);

        string reviewId;
        var (service1, scope1) = CreateServiceScope();
        using (scope1)
        {
            reviewId = Assert.Single((await service1.GetPendingAsync()).Data!).ReviewId;
        }

        using (var roleScope = _services.CreateScope())
        {
            var um = roleScope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var tracked = await um.FindByIdAsync(user.Id.ToString());
            await um.AddToRoleAsync(tracked!, AppRoles.Moderator);
        }

        var (service2, scope2) = CreateServiceScope();
        using (scope2)
        {
            var urlResult = await service2.GetVoiceUrlAsync(reviewId);
            Assert.False(urlResult.Succeeded);
            Assert.Equal(HttpStatusCode.NotFound, urlResult.StatusCode);

            var decideResult = await service2.DecideAsync(
                reviewId,
                new VoiceReviewDecisionDto { Decision = UserStatus.Rejected, Reason = "Should be forbidden" },
                Guid.NewGuid());

            Assert.False(decideResult.Succeeded);
            Assert.Equal(HttpStatusCode.Forbidden, decideResult.StatusCode);
            Assert.Null(decideResult.RevokedUserId);
        }

        Assert.Equal(UserStatus.Pending, (await ReloadUserAsync(user.Id)).Status);
        Assert.Empty(await ReadAllAuditLogsAsync());
    }

    [Fact]
    public async Task DecideAsync_ConcurrentDoubleDecisionOnSamePendingUser_ExactlyOneSucceedsAndWritesOneAuditRow()
    {
        var tempDbPath = Path.Combine(Path.GetTempPath(), $"cocorra_vr_race_{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={tempDbPath}";

        try
        {
            var sc = new ServiceCollection();
            sc.AddLogging();
            sc.AddDbContext<AppDbContext>(options => options.UseSqlite(connectionString));
            sc.AddIdentity<ApplicationUser, IdentityRole<Guid>>(options =>
            {
                options.User.RequireUniqueEmail = false;
            })
            .AddEntityFrameworkStores<AppDbContext>()
            .AddDefaultTokenProviders();

            await using var provider = sc.BuildServiceProvider();

            Guid targetUserId;
            using (var initScope = provider.CreateScope())
            {
                var db = initScope.ServiceProvider.GetRequiredService<AppDbContext>();
                await db.Database.EnsureCreatedAsync();
                await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");

                var rm = initScope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
                foreach (var role in AppRoles.All)
                    await rm.CreateAsync(new IdentityRole<Guid>(role));

                var um = initScope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
                var user = new ApplicationUser
                {
                    Id = Guid.NewGuid(),
                    UserName = "race@cocorra.test",
                    Email = "race@cocorra.test",
                    FirstName = "Race",
                    LastName = "User",
                    Status = UserStatus.Pending,
                    VoiceVerificationPath = "voices/race.m4a",
                    CreatedAt = DateTime.UtcNow
                };
                Assert.True((await um.CreateAsync(user)).Succeeded);
                Assert.True((await um.AddToRoleAsync(user, AppRoles.User)).Succeeded);
                targetUserId = user.Id;
            }

            var dpProvider = new EphemeralDataProtectionProvider();

            VoiceReviewService BuildService(IServiceScope scope)
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var um = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
                var userRepo = new UserRepository(db);
                var auditRepo = new AuditLogRepository(db);
                var auditLog = new AuditLogService(auditRepo, new Mock<IHttpContextAccessor>().Object, NullLogger<AuditLogService>.Instance);
                var fileUrls = new FakePrivateFileUrlIssuer();
                var adminService = new AdminService(
                    um,
                    new Mock<IUploadVoice>().Object,
                    _configuration,
                    new Mock<IEmailService>().Object,
                    userRepo,
                    new Mock<IPushNotificationService>().Object,
                    new Mock<IBlockedDevicesRepository>().Object,
                    new Mock<IEventTracker>().Object,
                    db,
                    new Mock<INotificationRepository>().Object,
                    fileUrls,
                    auditLog);
                return new VoiceReviewService(userRepo, um, adminService, fileUrls, auditLog, dpProvider, _configuration);
            }

            string reviewId;
            using (var qScope = provider.CreateScope())
            {
                var qSvc = BuildService(qScope);
                reviewId = Assert.Single((await qSvc.GetPendingAsync()).Data!).ReviewId;
            }

            var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            async Task<VoiceReviewDecisionResponse> RunDecisionAsync(UserStatus decision, string? reason)
            {
                using var runScope = provider.CreateScope();
                var svc = BuildService(runScope);
                await startGate.Task;
                return await svc.DecideAsync(reviewId, new VoiceReviewDecisionDto { Decision = decision, Reason = reason }, Guid.NewGuid());
            }

            var task1 = Task.Run(() => RunDecisionAsync(UserStatus.ReRecord, "Unclear audio clip"));
            var task2 = Task.Run(() => RunDecisionAsync(UserStatus.Rejected, "Voice mismatch detected"));
            startGate.SetResult();

            var results = await Task.WhenAll(task1, task2);

            Assert.Equal(1, results.Count(r => r.Succeeded));
            var failed = Assert.Single(results, r => !r.Succeeded);
            Assert.Equal(HttpStatusCode.Conflict, failed.StatusCode);

            using (var verifyScope = provider.CreateScope())
            {
                var db = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
                var finalUser = await db.Users.AsNoTracking().SingleAsync(u => u.Id == targetUserId);
                Assert.NotEqual(UserStatus.Pending, finalUser.Status);

                var auditRows = await db.AuditLogs.AsNoTracking().ToListAsync();
                var singleAudit = Assert.Single(auditRows);
                Assert.Equal(AuditActions.VoiceReviewDecision, singleAudit.Action);
                Assert.Equal(finalUser.Status.ToString(), singleAudit.ToState);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(tempDbPath)) File.Delete(tempDbPath);
            if (File.Exists(tempDbPath + "-wal")) File.Delete(tempDbPath + "-wal");
            if (File.Exists(tempDbPath + "-shm")) File.Delete(tempDbPath + "-shm");
        }
    }

    [Theory]
    [InlineData(UserStatus.Active, false)]
    [InlineData(UserStatus.Rejected, false)]
    [InlineData(UserStatus.ReRecord, true)]
    public async Task DecideAsync_OptimisticConcurrencyFailure_Returns409AndDoesNotDeleteVoiceOrWriteAudit(
        UserStatus decision, bool throwConcurrencyException)
    {
        var userId = Guid.NewGuid();
        var user = new ApplicationUser
        {
            Id = userId,
            Email = "concurrent@cocorra.test",
            Status = UserStatus.Pending,
            VoiceVerificationPath = "Uploads/Voices/keep-me.m4a"
        };

        var userManagerMock = TestIdentityHelper.CreateMockUserManager();
        userManagerMock.Setup(m => m.FindByIdAsync(userId.ToString())).ReturnsAsync(user);
        userManagerMock.Setup(m => m.GetRolesAsync(user)).ReturnsAsync(new List<string> { AppRoles.User });

        var concurrencyError = new IdentityError
        {
            Code = nameof(IdentityErrorDescriber.ConcurrencyFailure),
            Description = "Optimistic concurrency failure, object has been modified."
        };
        if (throwConcurrencyException)
        {
            userManagerMock.Setup(m => m.UpdateAsync(user)).ThrowsAsync(new DbUpdateConcurrencyException("Simulated race"));
        }
        else
        {
            userManagerMock.Setup(m => m.UpdateAsync(user)).ReturnsAsync(IdentityResult.Failed(concurrencyError));
        }

        var uploadVoiceMock = new Mock<IUploadVoice>();
        var auditLogMock = new Mock<IAuditLogService>();

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var userRepo = new UserRepository(db);
        var adminService = new AdminService(
            userManagerMock.Object,
            uploadVoiceMock.Object,
            _configuration,
            _emailServiceMock.Object,
            userRepo,
            _pushServiceMock.Object,
            _blockedDevicesRepoMock.Object,
            _eventTrackerMock.Object,
            db,
            _notificationRepoMock.Object,
            _fileUrls,
            auditLogMock.Object);

        var service = new VoiceReviewService(
            userRepo,
            userManagerMock.Object,
            adminService,
            _fileUrls,
            auditLogMock.Object,
            _dataProtectionProvider,
            _configuration);

        var reviewId = _dataProtectionProvider
            .CreateProtector(ProtectorPurpose)
            .ToTimeLimitedDataProtector()
            .Protect(userId.ToString("D"), TimeSpan.FromHours(1));

        var result = await service.DecideAsync(
            reviewId,
            new VoiceReviewDecisionDto { Decision = decision, Reason = "Valid concurrency test reason" },
            Guid.NewGuid());

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.Conflict, result.StatusCode);
        Assert.Null(result.RevokedUserId);
        uploadVoiceMock.Verify(v => v.DeleteVoice(It.IsAny<string?>()), Times.Never);
        auditLogMock.Verify(a => a.RecordAsync(It.IsAny<AuditEntry>()), Times.Never);
    }
}
