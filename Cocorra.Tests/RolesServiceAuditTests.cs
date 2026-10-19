using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Cocorra.BLL.Services.AuditLogs;
using Cocorra.BLL.Services.RolesService;
using Cocorra.DAL.Data;
using Cocorra.DAL.DTOS.AdminDto;
using Cocorra.DAL.DTOS.Role;
using Cocorra.DAL.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Cocorra.Tests;

public class RolesServiceAuditTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _services;
    private readonly Mock<IAuditLogService> _auditLogMock = new();

    public RolesServiceAuditTests()
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

    private (RolesService service, RoleManager<IdentityRole<Guid>> roleMgr, UserManager<ApplicationUser> userMgr) CreateService()
    {
        var scope = _services.CreateScope();
        var roleMgr = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        var userMgr = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = new RolesService(roleMgr, userMgr, _auditLogMock.Object, db);
        return (service, roleMgr, userMgr);
    }

    [Fact]
    public async Task ManageUserRolesAsync_Success_RecordsUserRolesChangedWithExactAddedAndRemovedLists()
    {
        var (service, roleMgr, userMgr) = CreateService();

        await roleMgr.CreateAsync(new IdentityRole<Guid>("OldRole"));
        await roleMgr.CreateAsync(new IdentityRole<Guid>("NewRole"));

        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = "audited_user",
            Email = "audited_user@cocorra.com",
            FirstName = "Audited",
            LastName = "User"
        };
        await userMgr.CreateAsync(user);
        await userMgr.AddToRoleAsync(user, "OldRole");

        var actorUserId = Guid.NewGuid();
        var dto = new ManageUserRolesDto
        {
            UserId = user.Id,
            Roles = new List<string> { "NewRole" }
        };

        AuditEntry? capturedEntry = null;
        _auditLogMock.Setup(a => a.RecordAsync(It.IsAny<AuditEntry>()))
            .Callback<AuditEntry>(e => capturedEntry = e)
            .Returns(Task.CompletedTask);

        var result = await service.ManageUserRolesAsync(dto, actorUserId);

        Assert.True(result.Succeeded);
        _auditLogMock.Verify(a => a.RecordAsync(It.IsAny<AuditEntry>()), Times.Once);

        Assert.NotNull(capturedEntry);
        Assert.Equal(AuditActions.UserRolesChanged, capturedEntry!.Action);
        Assert.Equal(actorUserId, capturedEntry.ActorUserId);
        Assert.Equal(user.Id, capturedEntry.TargetUserId);
        Assert.Equal("audited_user@cocorra.com", capturedEntry.TargetDisplay);

        var json = JsonSerializer.Serialize(capturedEntry.Details, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var doc = JsonDocument.Parse(json);

        var added = doc.RootElement.GetProperty("added").EnumerateArray().Select(x => x.GetString()).ToList();
        var removed = doc.RootElement.GetProperty("removed").EnumerateArray().Select(x => x.GetString()).ToList();

        Assert.Equal(new[] { "NewRole" }, added);
        Assert.Equal(new[] { "OldRole" }, removed);
    }

    [Fact]
    public async Task ManageUserRolesAsync_NoChangesDetected_RecordsNothing()
    {
        var (service, roleMgr, userMgr) = CreateService();

        await roleMgr.CreateAsync(new IdentityRole<Guid>("Member"));

        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = "static_user",
            Email = "static@cocorra.com",
            FirstName = "Static",
            LastName = "User"
        };
        await userMgr.CreateAsync(user);
        await userMgr.AddToRoleAsync(user, "Member");

        var dto = new ManageUserRolesDto
        {
            UserId = user.Id,
            Roles = new List<string> { "Member" }
        };

        var result = await service.ManageUserRolesAsync(dto, Guid.NewGuid());

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        Assert.Equal("No changes detected.", result.Message);
        _auditLogMock.Verify(a => a.RecordAsync(It.IsAny<AuditEntry>()), Times.Never);
    }

    [Fact]
    public async Task ManageUserRolesAsync_AdminAccount_RecordsNothing()
    {
        var (service, roleMgr, userMgr) = CreateService();

        await roleMgr.CreateAsync(new IdentityRole<Guid>("Admin"));
        await roleMgr.CreateAsync(new IdentityRole<Guid>("Moderator"));

        var adminUser = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = "admin_target",
            Email = "admintarget@cocorra.com",
            FirstName = "Admin",
            LastName = "Target"
        };
        await userMgr.CreateAsync(adminUser);
        await userMgr.AddToRoleAsync(adminUser, "Admin");

        var dto = new ManageUserRolesDto
        {
            UserId = adminUser.Id,
            Roles = new List<string> { "Moderator" }
        };

        var result = await service.ManageUserRolesAsync(dto, Guid.NewGuid());

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        Assert.Equal("Cannot modify roles of an Admin account.", result.Message);
        _auditLogMock.Verify(a => a.RecordAsync(It.IsAny<AuditEntry>()), Times.Never);
    }

    [Fact]
    public async Task CreateUserWithRoleAsync_Success_RecordsUserCreatedWithRoleWithRoles()
    {
        var (service, roleMgr, _) = CreateService();

        await roleMgr.CreateAsync(new IdentityRole<Guid>("Coach"));

        var actorUserId = Guid.NewGuid();
        var dto = new CreateUserDto
        {
            FirstName = "CoachFirstName",
            LastName = "CoachLastName",
            Email = "coach_created@cocorra.com",
            Password = "Password123!",
            Role = "Coach"
        };

        AuditEntry? capturedEntry = null;
        _auditLogMock.Setup(a => a.RecordAsync(It.IsAny<AuditEntry>()))
            .Callback<AuditEntry>(e => capturedEntry = e)
            .Returns(Task.CompletedTask);

        var result = await service.CreateUserWithRoleAsync(dto, actorUserId);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Data);
        _auditLogMock.Verify(a => a.RecordAsync(It.IsAny<AuditEntry>()), Times.Once);

        Assert.NotNull(capturedEntry);
        Assert.Equal(AuditActions.UserCreatedWithRole, capturedEntry!.Action);
        Assert.Equal(actorUserId, capturedEntry.ActorUserId);
        Assert.Equal(Guid.Parse(result.Data!.Id), capturedEntry.TargetUserId);
        Assert.Equal("coach_created@cocorra.com", capturedEntry.TargetDisplay);

        var json = JsonSerializer.Serialize(capturedEntry.Details, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var doc = JsonDocument.Parse(json);
        var roles = doc.RootElement.GetProperty("roles").EnumerateArray().Select(x => x.GetString()).ToList();

        Assert.Equal(new[] { "Coach" }, roles);
    }

    [Fact]
    public async Task CreateUserWithRoleAsync_FailedCreateAsync_RecordsNothing()
    {
        var (service, roleMgr, _) = CreateService();

        await roleMgr.CreateAsync(new IdentityRole<Guid>("Coach"));

        var dto = new CreateUserDto
        {
            FirstName = "Weak",
            LastName = "PasswordUser",
            Email = "weak_pw@cocorra.com",
            Password = "123", // Fails ASP.NET Identity default password complexity
            Role = "Coach"
        };

        var result = await service.CreateUserWithRoleAsync(dto, Guid.NewGuid());

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        Assert.Contains("Failed to create user", result.Message);
        _auditLogMock.Verify(a => a.RecordAsync(It.IsAny<AuditEntry>()), Times.Never);
    }

    [Fact]
    public async Task UpdateRoleDisplayNameAsync_SetThenReset_KeepsRoleNameAndRecordsEachChange()
    {
        var (service, roleMgr, _) = CreateService();
        await roleMgr.CreateAsync(new IdentityRole<Guid>("Coach"));
        var coach = await roleMgr.FindByNameAsync("Coach");
        var actorUserId = Guid.NewGuid();

        var captured = new List<AuditEntry>();
        _auditLogMock.Setup(a => a.RecordAsync(It.IsAny<AuditEntry>()))
            .Callback<AuditEntry>(e => captured.Add(e))
            .Returns(Task.CompletedTask);

        var set = await service.UpdateRoleDisplayNameAsync(coach!.Id.ToString(), new UpdateRoleDisplayNameDto { DisplayName = "  Mentor  " }, actorUserId);

        Assert.True(set.Succeeded);
        Assert.Equal("Coach", set.Data!.Name);
        Assert.Equal("Mentor", set.Data.DisplayName);
        Assert.True(await roleMgr.RoleExistsAsync("Coach"));

        var listed = await service.GetRolesAsync();
        Assert.Contains(listed.Data!, r => r.Name == "Coach" && r.DisplayName == "Mentor");

        var reset = await service.UpdateRoleDisplayNameAsync(coach.Id.ToString(), new UpdateRoleDisplayNameDto { DisplayName = "" }, actorUserId);

        Assert.True(reset.Succeeded);
        Assert.Equal("Coach", reset.Data!.DisplayName);

        Assert.Equal(2, captured.Count);
        Assert.All(captured, e =>
        {
            Assert.Equal(AuditActions.RoleDisplayNameChanged, e.Action);
            Assert.Equal(actorUserId, e.ActorUserId);
            Assert.Null(e.TargetUserId);
            Assert.Equal("Coach", e.TargetDisplay);
        });

        var json = JsonSerializer.Serialize(captured[1].Details, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("Mentor", doc.RootElement.GetProperty("from").GetString());
        Assert.Equal("Coach", doc.RootElement.GetProperty("to").GetString());
    }

    [Fact]
    public async Task UpdateRoleDisplayNameAsync_NameShownByAnotherRole_RejectsAndRecordsNothing()
    {
        var (service, roleMgr, _) = CreateService();
        await roleMgr.CreateAsync(new IdentityRole<Guid>("Coach"));
        await roleMgr.CreateAsync(new IdentityRole<Guid>("Moderator"));
        var coach = await roleMgr.FindByNameAsync("Coach");
        var moderator = await roleMgr.FindByNameAsync("Moderator");

        var sameAsName = await service.UpdateRoleDisplayNameAsync(coach!.Id.ToString(), new UpdateRoleDisplayNameDto { DisplayName = "moderator" }, Guid.NewGuid());
        Assert.False(sameAsName.Succeeded);

        await service.UpdateRoleDisplayNameAsync(moderator!.Id.ToString(), new UpdateRoleDisplayNameDto { DisplayName = "Guide" }, Guid.NewGuid());
        _auditLogMock.Invocations.Clear();

        var sameAsLabel = await service.UpdateRoleDisplayNameAsync(coach.Id.ToString(), new UpdateRoleDisplayNameDto { DisplayName = "GUIDE" }, Guid.NewGuid());
        Assert.False(sameAsLabel.Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, sameAsLabel.StatusCode);

        var tooLong = await service.UpdateRoleDisplayNameAsync(coach.Id.ToString(), new UpdateRoleDisplayNameDto { DisplayName = new string('x', 51) }, Guid.NewGuid());
        Assert.False(tooLong.Succeeded);

        var unchanged = await service.UpdateRoleDisplayNameAsync(coach.Id.ToString(), new UpdateRoleDisplayNameDto { DisplayName = "Coach" }, Guid.NewGuid());
        Assert.False(unchanged.Succeeded);

        _auditLogMock.Verify(a => a.RecordAsync(It.IsAny<AuditEntry>()), Times.Never);
    }
}
