using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Cocorra.BLL.Services.AuditLogs;
using Cocorra.BLL.Services.RolesService;
using Cocorra.DAL.Data;
using Cocorra.DAL.DTOS.Role;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;
using Cocorra.Tests.Helpers;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Cocorra.Tests;

public class RolesServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _services;

    public RolesServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var serviceCollection = new ServiceCollection();
        serviceCollection.AddLogging();
        serviceCollection.AddDbContext<AppDbContext>(options => options.UseSqlite(_connection));
        serviceCollection.AddIdentity<ApplicationUser, IdentityRole<Guid>>(options =>
        {
            options.User.RequireUniqueEmail = false;
        })
        .AddEntityFrameworkStores<AppDbContext>()
        .AddDefaultTokenProviders();

        _services = serviceCollection.BuildServiceProvider();

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _services.Dispose();
        _connection.Dispose();
    }

    private (RolesService service, RoleManager<IdentityRole<Guid>> roleMgr, UserManager<ApplicationUser> userMgr, AppDbContext db) CreateService()
    {
        var scope = _services.CreateScope();
        var roleMgr = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        var userMgr = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = new RolesService(roleMgr, userMgr, new Mock<IAuditLogService>().Object, db, new FakePrivateFileUrlIssuer());
        return (service, roleMgr, userMgr, db);
    }

    [Fact]
    public async Task GetRolesAsync_ReturnsAllRoles()
    {
        var (service, roleMgr, _, _) = CreateService();
        await roleMgr.CreateAsync(new IdentityRole<Guid>("Member"));
        await roleMgr.CreateAsync(new IdentityRole<Guid>("VIP"));

        var result = await service.GetRolesAsync();

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Data);
        Assert.Contains(result.Data, r => r.Name == "Member");
        Assert.Contains(result.Data, r => r.Name == "VIP");
    }

    [Fact]
    public async Task GetRoleByIdAsync_RoleNotFound_ReturnsBadRequest()
    {
        var (service, _, _, _) = CreateService();
        var result = await service.GetRoleByIdAsync(Guid.NewGuid().ToString());

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        Assert.Equal("Role not found", result.Message);
    }

    [Fact]
    public async Task GetRoleByIdAsync_RoleFound_ReturnsRoleDto()
    {
        var (service, roleMgr, _, _) = CreateService();
        var role = new IdentityRole<Guid>(AppRoles.Moderator);
        await roleMgr.CreateAsync(role);

        var result = await service.GetRoleByIdAsync(role.Id.ToString());

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Data);
        Assert.Equal(role.Id.ToString(), result.Data.Id);
        Assert.Equal(AppRoles.Moderator, result.Data.Name);
    }

    [Fact]
    public async Task ManageUserRolesAsync_UserNotFound_ReturnsBadRequest()
    {
        var (service, _, _, _) = CreateService();
        var dto = new ManageUserRolesDto
        {
            UserId = Guid.NewGuid(),
            Roles = new List<string> { "Member" }
        };

        var result = await service.ManageUserRolesAsync(dto, Guid.NewGuid());

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        Assert.Equal("User not found", result.Message);
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("admin")]
    [InlineData(" Admin ")]
    public async Task ManageUserRolesAsync_AssignAdminRoleVariants_RefusedAndLeavesRolesAndStampUnchanged(string adminVariant)
    {
        var (service, roleMgr, userMgr, _) = CreateService();
        await roleMgr.CreateAsync(new IdentityRole<Guid>(AppRoles.Admin));
        await roleMgr.CreateAsync(new IdentityRole<Guid>(AppRoles.User));

        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = "regular", FirstName = "R", LastName = "U" };
        await userMgr.CreateAsync(user);
        await userMgr.AddToRoleAsync(user, AppRoles.User);
        var originalStamp = (await userMgr.FindByIdAsync(user.Id.ToString()))!.SecurityStamp;

        var dto = new ManageUserRolesDto
        {
            UserId = user.Id,
            Roles = new List<string> { adminVariant }
        };

        var result = await service.ManageUserRolesAsync(dto, Guid.NewGuid());

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        Assert.Equal("Cannot assign the Admin role through this endpoint.", result.Message);

        var reloaded = await userMgr.FindByIdAsync(user.Id.ToString());
        Assert.Equal(originalStamp, reloaded!.SecurityStamp);
        Assert.Equal(new[] { AppRoles.User }, await userMgr.GetRolesAsync(reloaded));
    }

    [Fact]
    public async Task ManageUserRolesAsync_TargetIsAdmin_RefusedAndLeavesRolesAndStampUnchanged()
    {
        var (service, roleMgr, userMgr, _) = CreateService();
        await roleMgr.CreateAsync(new IdentityRole<Guid>(AppRoles.Admin));
        await roleMgr.CreateAsync(new IdentityRole<Guid>(AppRoles.Moderator));

        var adminUser = new ApplicationUser { Id = Guid.NewGuid(), UserName = "admin_user", FirstName = "A", LastName = "D" };
        await userMgr.CreateAsync(adminUser);
        await userMgr.AddToRoleAsync(adminUser, AppRoles.Admin);
        var originalStamp = (await userMgr.FindByIdAsync(adminUser.Id.ToString()))!.SecurityStamp;

        var result = await service.ManageUserRolesAsync(
            new ManageUserRolesDto { UserId = adminUser.Id, Roles = new List<string> { AppRoles.Moderator } },
            Guid.NewGuid());

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);

        var reloaded = await userMgr.FindByIdAsync(adminUser.Id.ToString());
        Assert.Equal(originalStamp, reloaded!.SecurityStamp);
        Assert.Equal(new[] { AppRoles.Admin }, await userMgr.GetRolesAsync(reloaded));
    }

    [Fact]
    public async Task ManageUserRolesAsync_NonExistentRole_ReturnsBadRequest()
    {
        var (service, _, userMgr, _) = CreateService();
        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = "user2", FirstName = "U", LastName = "2" };
        await userMgr.CreateAsync(user);

        var dto = new ManageUserRolesDto
        {
            UserId = user.Id,
            Roles = new List<string> { "GhostRole" }
        };

        var result = await service.ManageUserRolesAsync(dto, Guid.NewGuid());

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        Assert.Contains("does not exist in the system", result.Message);
    }

    [Fact]
    public async Task ManageUserRolesAsync_AddsRecordReviewerAndRemovesOldRole_RotatesSecurityStamp()
    {
        var (service, roleMgr, userMgr, _) = CreateService();
        await roleMgr.CreateAsync(new IdentityRole<Guid>(AppRoles.User));
        await roleMgr.CreateAsync(new IdentityRole<Guid>(AppRoles.RecordReviewer));

        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = "reviewer_candidate", FirstName = "U", LastName = "3" };
        await userMgr.CreateAsync(user);
        await userMgr.AddToRoleAsync(user, AppRoles.User);
        var originalStamp = (await userMgr.FindByIdAsync(user.Id.ToString()))!.SecurityStamp;

        var dto = new ManageUserRolesDto
        {
            UserId = user.Id,
            Roles = new List<string> { AppRoles.RecordReviewer }
        };

        var result = await service.ManageUserRolesAsync(dto, Guid.NewGuid());

        Assert.True(result.Succeeded);
        var reloaded = await userMgr.FindByIdAsync(user.Id.ToString());
        var currentRoles = await userMgr.GetRolesAsync(reloaded!);
        Assert.Contains(AppRoles.RecordReviewer, currentRoles);
        Assert.DoesNotContain(AppRoles.User, currentRoles);
        Assert.NotEqual(originalStamp, reloaded!.SecurityStamp);
    }

    [Fact]
    public async Task ManageUserRolesAsync_NoChangesDetected_DoesNotRotateSecurityStamp()
    {
        var (service, roleMgr, userMgr, _) = CreateService();
        await roleMgr.CreateAsync(new IdentityRole<Guid>(AppRoles.Moderator));

        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = "unchanged_mod", FirstName = "U", LastName = "M" };
        await userMgr.CreateAsync(user);
        await userMgr.AddToRoleAsync(user, AppRoles.Moderator);
        var originalStamp = (await userMgr.FindByIdAsync(user.Id.ToString()))!.SecurityStamp;

        var result = await service.ManageUserRolesAsync(
            new ManageUserRolesDto { UserId = user.Id, Roles = new List<string> { AppRoles.Moderator } },
            Guid.NewGuid());

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        var reloaded = await userMgr.FindByIdAsync(user.Id.ToString());
        Assert.Equal(originalStamp, reloaded!.SecurityStamp);
    }

    [Fact]
    public async Task GetUsersInRoleAsync_RoleDoesNotExist_ReturnsBadRequest()
    {
        var (service, _, _, _) = CreateService();
        var result = await service.GetUsersInRoleAsync("FakeRole");

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        Assert.Equal("Role not found", result.Message);
    }

    [Fact]
    public async Task GetUsersInRoleAsync_ValidRole_ReturnsUsersWithCreatedAtAndAllRoles()
    {
        var (service, roleMgr, userMgr, _) = CreateService();
        await roleMgr.CreateAsync(new IdentityRole<Guid>(AppRoles.Moderator));
        await roleMgr.CreateAsync(new IdentityRole<Guid>(AppRoles.RecordReviewer));

        var createdAt = new DateTime(2026, 4, 15, 10, 30, 0, DateTimeKind.Utc);
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = "support_agent",
            Email = "agent@cocorra.com",
            FirstName = "Agent",
            LastName = "Smith",
            CreatedAt = createdAt,
            VoiceVerificationPath = "Uploads/Voices/agent.m4a"
        };
        await userMgr.CreateAsync(user);
        await userMgr.AddToRolesAsync(user, new[] { AppRoles.Moderator, AppRoles.RecordReviewer });

        var result = await service.GetUsersInRoleAsync(AppRoles.Moderator);

        Assert.True(result.Succeeded);
        var returned = Assert.Single(result.Data!);
        Assert.Equal("Agent Smith", returned.FullName);
        Assert.Equal(createdAt, returned.CreatedAt);
        Assert.Contains(AppRoles.Moderator, returned.Roles);
        Assert.Contains(AppRoles.RecordReviewer, returned.Roles);
        Assert.True(returned.HasVoice);
        Assert.Null(returned.VoicePath);
    }

    [Theory]
    [InlineData(AppRoles.Coach)]
    [InlineData(AppRoles.RecordReviewer)]
    public async Task CreateUserWithRoleAsync_ValidAssignableRole_CreatesUserAndPersistsRole(string roleName)
    {
        var (service, roleMgr, userMgr, _) = CreateService();
        await roleMgr.CreateAsync(new IdentityRole<Guid>(roleName));

        var dto = new CreateUserDto
        {
            FirstName = "Sarah",
            LastName = "Connor",
            Email = $"sarah_{roleName.ToLowerInvariant()}@example.com",
            Password = "Password123!",
            Age = 28,
            Role = roleName
        };

        var result = await service.CreateUserWithRoleAsync(dto, Guid.NewGuid());

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Data);
        Assert.Equal("Sarah Connor", result.Data.FullName);
        Assert.Equal(dto.Email, result.Data.Email);
        Assert.Contains(roleName, result.Data.Roles);

        var persisted = await userMgr.FindByEmailAsync(dto.Email);
        Assert.NotNull(persisted);
        Assert.Contains(roleName, await userMgr.GetRolesAsync(persisted!));
    }

    [Theory]
    [InlineData("Admin", null)]
    [InlineData("admin", null)]
    [InlineData(" Admin ", null)]
    [InlineData(null, new[] { "Coach", "Admin" })]
    [InlineData(null, new[] { "Coach", " admin " })]
    public async Task CreateUserWithRoleAsync_AdminRoleVariants_RefusedAndPersistsNoUser(string? singleRole, string[]? rolesList)
    {
        var (service, roleMgr, userMgr, db) = CreateService();
        await roleMgr.CreateAsync(new IdentityRole<Guid>(AppRoles.Admin));
        await roleMgr.CreateAsync(new IdentityRole<Guid>(AppRoles.Coach));

        var email = $"blocked_{Guid.NewGuid():N}@example.com";
        var dto = new CreateUserDto
        {
            FirstName = "Attempt",
            LastName = "Admin",
            Email = email,
            Password = "Password123!",
            Age = 30,
            Role = singleRole,
            Roles = rolesList?.ToList()
        };

        var result = await service.CreateUserWithRoleAsync(dto, Guid.NewGuid());

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        Assert.Equal("Cannot assign the Admin role through this endpoint.", result.Message);
        Assert.Null(await userMgr.FindByEmailAsync(email));
        Assert.Equal(0, await db.Users.CountAsync());
    }

    [Fact]
    public async Task CreateUserWithRoleAsync_DuplicateEmail_ReturnsBadRequest()
    {
        var (service, roleMgr, userMgr, _) = CreateService();
        await roleMgr.CreateAsync(new IdentityRole<Guid>(AppRoles.Coach));

        var existingUser = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = "dup@example.com",
            Email = "dup@example.com",
            FirstName = "Existing",
            LastName = "User"
        };
        await userMgr.CreateAsync(existingUser, "Password123!");

        var dto = new CreateUserDto
        {
            FirstName = "New",
            LastName = "User",
            Email = "dup@example.com",
            Password = "Password123!",
            Role = AppRoles.Coach
        };

        var result = await service.CreateUserWithRoleAsync(dto, Guid.NewGuid());

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        Assert.Contains("already exists", result.Message);
    }

    [Fact]
    public async Task CreateUserWithRoleAsync_InvalidRole_ReturnsBadRequest()
    {
        var (service, _, _, _) = CreateService();

        var dto = new CreateUserDto
        {
            FirstName = "Test",
            LastName = "User",
            Email = "nonexistent_role@example.com",
            Password = "Password123!",
            Role = "NonExistentRole"
        };

        var result = await service.CreateUserWithRoleAsync(dto, Guid.NewGuid());

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        Assert.Contains("does not exist", result.Message);
    }

    [Fact]
    public async Task ManageUserRolesAsync_RemovingModerator_ReleasesOnlyThatUsersActiveClaimedChats()
    {
        var (service, roleMgr, userMgr, db) = CreateService();
        await roleMgr.CreateAsync(new IdentityRole<Guid>(AppRoles.Moderator));
        await roleMgr.CreateAsync(new IdentityRole<Guid>(AppRoles.User));

        var demotedMod = new ApplicationUser { Id = Guid.NewGuid(), UserName = "demoted_mod", FirstName = "Demoted", LastName = "Mod" };
        var keptMod = new ApplicationUser { Id = Guid.NewGuid(), UserName = "kept_mod", FirstName = "Kept", LastName = "Mod" };
        await userMgr.CreateAsync(demotedMod);
        await userMgr.CreateAsync(keptMod);
        await userMgr.AddToRoleAsync(demotedMod, AppRoles.Moderator);
        await userMgr.AddToRoleAsync(keptMod, AppRoles.Moderator);

        var activeChatId = await SeedSupportChatAsync(db, demotedMod.Id.ToString(), SupportChatStatus.Active);
        var closedChatId = await SeedSupportChatAsync(db, demotedMod.Id.ToString(), SupportChatStatus.Closed, closedAt: DateTime.UtcNow);
        var otherModActiveChatId = await SeedSupportChatAsync(db, keptMod.Id.ToString(), SupportChatStatus.Active);

        var result = await service.ManageUserRolesAsync(
            new ManageUserRolesDto { UserId = demotedMod.Id, Roles = new List<string> { AppRoles.User } },
            Guid.NewGuid());

        Assert.True(result.Succeeded);

        using var verifyScope = _services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();

        var reloadedActive = await verifyDb.SupportChats.AsNoTracking().SingleAsync(c => c.Id == activeChatId);
        Assert.Equal(SupportChatStatus.Pending, reloadedActive.Status);
        Assert.Null(reloadedActive.AdminId);

        var reloadedClosed = await verifyDb.SupportChats.AsNoTracking().SingleAsync(c => c.Id == closedChatId);
        Assert.Equal(SupportChatStatus.Closed, reloadedClosed.Status);
        Assert.Equal(demotedMod.Id.ToString(), reloadedClosed.AdminId);

        var reloadedOther = await verifyDb.SupportChats.AsNoTracking().SingleAsync(c => c.Id == otherModActiveChatId);
        Assert.Equal(SupportChatStatus.Active, reloadedOther.Status);
        Assert.Equal(keptMod.Id.ToString(), reloadedOther.AdminId);
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
