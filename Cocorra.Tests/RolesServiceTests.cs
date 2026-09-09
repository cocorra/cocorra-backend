using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Cocorra.BLL.Services.RolesService;
using Cocorra.DAL.Data;
using Cocorra.DAL.DTOS.Role;
using Cocorra.DAL.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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

    private (RolesService service, RoleManager<IdentityRole<Guid>> roleMgr, UserManager<ApplicationUser> userMgr) CreateService()
    {
        var scope = _services.CreateScope();
        var roleMgr = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        var userMgr = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var service = new RolesService(roleMgr, userMgr);
        return (service, roleMgr, userMgr);
    }

    [Fact]
    public async Task GetRolesAsync_ReturnsAllRoles()
    {
        var (service, roleMgr, _) = CreateService();
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
        var (service, _, _) = CreateService();
        var result = await service.GetRoleByIdAsync(Guid.NewGuid().ToString());

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        Assert.Equal("Role not found", result.Message);
    }

    [Fact]
    public async Task GetRoleByIdAsync_RoleFound_ReturnsRoleDto()
    {
        var (service, roleMgr, _) = CreateService();
        var role = new IdentityRole<Guid>("Moderator");
        await roleMgr.CreateAsync(role);

        var result = await service.GetRoleByIdAsync(role.Id.ToString());

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Data);
        Assert.Equal(role.Id.ToString(), result.Data.Id);
        Assert.Equal("Moderator", result.Data.Name);
    }

    [Fact]
    public async Task ManageUserRolesAsync_UserNotFound_ReturnsBadRequest()
    {
        var (service, _, _) = CreateService();
        var dto = new ManageUserRolesDto
        {
            UserId = Guid.NewGuid(),
            Roles = new List<string> { "Member" }
        };

        var result = await service.ManageUserRolesAsync(dto);

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        Assert.Equal("User not found", result.Message);
    }

    [Fact]
    public async Task ManageUserRolesAsync_AssignAdminRoleAttempt_ReturnsBadRequest()
    {
        var (service, _, userMgr) = CreateService();
        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = "regular", FirstName = "R", LastName = "U" };
        await userMgr.CreateAsync(user);

        var dto = new ManageUserRolesDto
        {
            UserId = user.Id,
            Roles = new List<string> { "Admin" }
        };

        var result = await service.ManageUserRolesAsync(dto);

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        Assert.Equal("Cannot assign the Admin role through this endpoint.", result.Message);
    }

    [Fact]
    public async Task ManageUserRolesAsync_NonExistentRole_ReturnsBadRequest()
    {
        var (service, _, userMgr) = CreateService();
        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = "user2", FirstName = "U", LastName = "2" };
        await userMgr.CreateAsync(user);

        var dto = new ManageUserRolesDto
        {
            UserId = user.Id,
            Roles = new List<string> { "GhostRole" }
        };

        var result = await service.ManageUserRolesAsync(dto);

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        Assert.Contains("does not exist in the system", result.Message);
    }

    [Fact]
    public async Task ManageUserRolesAsync_AddsAndRemovesRolesSuccessfully()
    {
        var (service, roleMgr, userMgr) = CreateService();
        await roleMgr.CreateAsync(new IdentityRole<Guid>("OldRole"));
        await roleMgr.CreateAsync(new IdentityRole<Guid>("NewRole"));

        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = "user3", FirstName = "U", LastName = "3" };
        await userMgr.CreateAsync(user);
        await userMgr.AddToRoleAsync(user, "OldRole");

        var dto = new ManageUserRolesDto
        {
            UserId = user.Id,
            Roles = new List<string> { "NewRole" }
        };

        var result = await service.ManageUserRolesAsync(dto);

        Assert.True(result.Succeeded);
        var currentRoles = await userMgr.GetRolesAsync(user);
        Assert.Contains("NewRole", currentRoles);
        Assert.DoesNotContain("OldRole", currentRoles);
    }

    [Fact]
    public async Task GetUsersInRoleAsync_RoleDoesNotExist_ReturnsBadRequest()
    {
        var (service, _, _) = CreateService();
        var result = await service.GetUsersInRoleAsync("FakeRole");

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        Assert.Equal("Role not found", result.Message);
    }

    [Fact]
    public async Task GetUsersInRoleAsync_ValidRole_ReturnsUsersInRole()
    {
        var (service, roleMgr, userMgr) = CreateService();
        await roleMgr.CreateAsync(new IdentityRole<Guid>("Support"));

        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = "support_agent",
            Email = "agent@cocorra.com",
            FirstName = "Agent",
            LastName = "Smith"
        };
        await userMgr.CreateAsync(user);
        await userMgr.AddToRoleAsync(user, "Support");

        var result = await service.GetUsersInRoleAsync("Support");

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Data);
        Assert.Single(result.Data);
        Assert.Equal("Agent Smith", result.Data[0].FullName);
    }
}
