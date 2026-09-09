using System;
using System.Collections.Generic;
using System.Net;
using System.Security.Claims;
using System.Threading.Tasks;
using Cocorra.BLL.DTOS.Auth;
using Cocorra.BLL.Services.AuthServices;
using Cocorra.BLL.Services.Email;
using Cocorra.BLL.Services.EventTracking;
using Cocorra.BLL.Services.Upload;
using Cocorra.DAL.Data;
using Cocorra.DAL.DTOS.Auth;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;
using Cocorra.DAL.Repository.RoomRepository;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Cocorra.Tests;

public class AuthServicesTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _services;
    private readonly Mock<IConfiguration> _configMock = new();
    private readonly Mock<IUploadVoice> _uploadVoiceMock = new();
    private readonly Mock<IEmailService> _emailServiceMock = new();
    private readonly Mock<IUploadImage> _uploadImageMock = new();
    private readonly Mock<IRoomRepository> _roomRepoMock = new();
    private readonly Mock<IEventTracker> _eventTrackerMock = new();

    public AuthServicesTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var serviceCollection = new ServiceCollection();
        serviceCollection.AddLogging();
        serviceCollection.AddDbContext<AppDbContext>(options => options.UseSqlite(_connection));
        serviceCollection.AddIdentity<ApplicationUser, IdentityRole<Guid>>(options =>
        {
            options.User.RequireUniqueEmail = true;
            options.Password.RequireDigit = false;
            options.Password.RequiredLength = 6;
            options.Password.RequireNonAlphanumeric = false;
            options.Password.RequireUppercase = false;
            options.Password.RequireLowercase = false;
        })
        .AddEntityFrameworkStores<AppDbContext>()
        .AddDefaultTokenProviders();

        _services = serviceCollection.BuildServiceProvider();

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Database.EnsureCreated();

        _configMock.Setup(c => c["JWTSetting:securityKey"]).Returns("VeryLongSecretKeyForTestingJwtTokens12345!#@$");
        _configMock.Setup(c => c["JWTSetting:ValidIssuer"]).Returns("CocorraTestIssuer");
        _configMock.Setup(c => c["JWTSetting:ValidAudience"]).Returns("CocorraTestAudience");
        _configMock.Setup(c => c["AppSettings:BaseUrl"]).Returns("https://api.cocorra.com");
    }

    public void Dispose()
    {
        _services.Dispose();
        _connection.Dispose();
    }

    private (AuthServices service, UserManager<ApplicationUser> userMgr, RoleManager<IdentityRole<Guid>> roleMgr, AppDbContext db) CreateService()
    {
        var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var userMgr = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roleMgr = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();

        var service = new AuthServices(
            userMgr,
            roleMgr,
            _configMock.Object,
            _uploadVoiceMock.Object,
            _emailServiceMock.Object,
            _uploadImageMock.Object,
            db,
            _roomRepoMock.Object,
            _eventTrackerMock.Object
        );

        return (service, userMgr, roleMgr, db);
    }

    [Fact]
    public async Task LoginAsync_UserNotFound_ReturnsBadRequest()
    {
        var (service, _, _, _) = CreateService();
        var result = await service.LoginAsync(new LoginDto { Email = "missing@cocorra.com", Password = "Pass" });

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        Assert.Equal("Invalid Email or Password", result.Message);
    }

    [Fact]
    public async Task LoginAsync_InvalidPassword_ReturnsBadRequest()
    {
        var (service, userMgr, _, _) = CreateService();
        var user = new ApplicationUser
        {
            UserName = "user@cocorra.com",
            Email = "user@cocorra.com",
            EmailConfirmed = true,
            Status = UserStatus.Active
        };
        await userMgr.CreateAsync(user, "Password123");

        var result = await service.LoginAsync(new LoginDto { Email = "user@cocorra.com", Password = "WrongPassword" });

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        Assert.Equal("Invalid Email or Password", result.Message);
    }

    [Fact]
    public async Task LoginAsync_UnconfirmedEmail_ReturnsBadRequest()
    {
        var (service, userMgr, _, _) = CreateService();
        var user = new ApplicationUser
        {
            UserName = "unconfirmed@cocorra.com",
            Email = "unconfirmed@cocorra.com",
            EmailConfirmed = false,
            Status = UserStatus.Active
        };
        await userMgr.CreateAsync(user, "Password123");

        var result = await service.LoginAsync(new LoginDto { Email = "unconfirmed@cocorra.com", Password = "Password123" });

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        Assert.Contains("confirm your email", result.Message);
    }

    [Theory]
    [InlineData(UserStatus.Banned, "banned")]
    [InlineData(UserStatus.Rejected, "rejected")]
    public async Task LoginAsync_BannedOrRejectedUser_ReturnsBadRequest(UserStatus status, string expectedMessageFragment)
    {
        var (service, userMgr, _, _) = CreateService();
        var email = $"status_{status}@cocorra.com";
        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            Status = status
        };
        await userMgr.CreateAsync(user, "Password123");

        var result = await service.LoginAsync(new LoginDto { Email = email, Password = "Password123" });

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        Assert.Contains(expectedMessageFragment, result.Message);
    }

    [Fact]
    public async Task LoginAsync_ActiveUser_GeneratesTokensAndReturnsAuthModel()
    {
        var (service, userMgr, roleMgr, _) = CreateService();
        await roleMgr.CreateAsync(new IdentityRole<Guid>("User"));

        var user = new ApplicationUser
        {
            UserName = "active@cocorra.com",
            Email = "active@cocorra.com",
            EmailConfirmed = true,
            Status = UserStatus.Active,
            FirstName = "Active",
            LastName = "User"
        };
        await userMgr.CreateAsync(user, "Password123");
        await userMgr.AddToRoleAsync(user, "User");

        var result = await service.LoginAsync(new LoginDto { Email = "active@cocorra.com", Password = "Password123" });

        Assert.True(result.Succeeded);
        var auth = Assert.IsType<AuthModel>(result.Data);
        Assert.NotNull(auth.Token);
        Assert.NotNull(auth.RefreshToken);
        Assert.Equal("active@cocorra.com", auth.Email);
    }

    [Fact]
    public async Task ForgotPasswordAsync_UserNotFound_ReturnsGenericSuccess()
    {
        var (service, _, _, _) = CreateService();
        var result = await service.ForgotPasswordAsync(new ForgotPasswordDto { Email = "missing@cocorra.com" });

        Assert.True(result.Succeeded);
        Assert.Contains("receive a reset link", result.Data);
        _emailServiceMock.Verify(e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ForgotPasswordAsync_ExistingUser_GeneratesOtpAndSendsEmail()
    {
        var (service, userMgr, _, _) = CreateService();
        var user = new ApplicationUser
        {
            UserName = "reset@cocorra.com",
            Email = "reset@cocorra.com",
            FirstName = "Karim"
        };
        await userMgr.CreateAsync(user, "Password123");

        var result = await service.ForgotPasswordAsync(new ForgotPasswordDto { Email = "reset@cocorra.com" });

        Assert.True(result.Succeeded);
        _emailServiceMock.Verify(e => e.SendEmailAsync(
            "reset@cocorra.com",
            "Password Reset Code",
            It.Is<string>(s => s.Contains("Karim"))
        ), Times.Once);
    }

    [Fact]
    public async Task UpdateFcmTokenAsync_UserNotFound_ReturnsBadRequest()
    {
        var (service, _, _, _) = CreateService();
        var result = await service.UpdateFcmTokenAsync(Guid.NewGuid(), "token_123");

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        Assert.Equal("User not found.", result.Message);
    }

    [Fact]
    public async Task UpdateFcmTokenAsync_ValidUser_UpdatesTokenAndClearsStaleTokens()
    {
        var (service, userMgr, _, _) = CreateService();
        var user1 = new ApplicationUser { UserName = "u1@test.com", Email = "u1@test.com", FcmToken = "shared_token" };
        var user2 = new ApplicationUser { UserName = "u2@test.com", Email = "u2@test.com" };
        await userMgr.CreateAsync(user1, "Password123");
        await userMgr.CreateAsync(user2, "Password123");

        var result = await service.UpdateFcmTokenAsync(user2.Id, "shared_token");

        Assert.True(result.Succeeded);
        Assert.Equal("FCM Token updated successfully.", result.Data);

        var updatedUser2 = await userMgr.FindByIdAsync(user2.Id.ToString());
        Assert.Equal("shared_token", updatedUser2!.FcmToken);

        var updatedUser1 = await userMgr.FindByIdAsync(user1.Id.ToString());
        Assert.Null(updatedUser1!.FcmToken); // Stale token cleared from old device
    }

    [Fact]
    public async Task UpdatePasswordAsync_UserNotFound_ReturnsBadRequest()
    {
        var (service, _, _, _) = CreateService();
        var result = await service.UpdatePasswordAsync(Guid.NewGuid(), "old", "new");

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        Assert.Equal("User not found.", result.Message);
    }

    [Fact]
    public async Task UpdatePasswordAsync_InvalidOldPassword_ReturnsBadRequest()
    {
        var (service, userMgr, _, _) = CreateService();
        var user = new ApplicationUser { UserName = "change_pass@test.com", Email = "change_pass@test.com" };
        await userMgr.CreateAsync(user, "Original123");

        var result = await service.UpdatePasswordAsync(user.Id, "WrongOldPass", "NewPass123");

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
    }

    [Fact]
    public async Task UpdatePasswordAsync_ValidPassword_ReturnsSuccess()
    {
        var (service, userMgr, _, _) = CreateService();
        var user = new ApplicationUser { UserName = "change_pass2@test.com", Email = "change_pass2@test.com" };
        await userMgr.CreateAsync(user, "Original123");

        var result = await service.UpdatePasswordAsync(user.Id, "Original123", "NewPass123");

        Assert.True(result.Succeeded);
        Assert.Equal("Password updated successfully.", result.Data);
    }

    [Fact]
    public async Task DeleteAccountAsync_UserNotFound_ReturnsBadRequest()
    {
        var (service, _, _, _) = CreateService();
        var result = await service.DeleteAccountAsync(Guid.NewGuid());

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        Assert.Equal("User not found.", result.Message);
    }

    [Fact]
    public async Task DeleteAccountAsync_ValidUser_DeletesUser()
    {
        var (service, userMgr, _, _) = CreateService();
        var user = new ApplicationUser { UserName = "del@test.com", Email = "del@test.com" };
        await userMgr.CreateAsync(user, "Password123");

        var result = await service.DeleteAccountAsync(user.Id);

        Assert.True(result.Succeeded);
        Assert.Equal("Account deleted successfully.", result.Data);

        var deleted = await userMgr.FindByIdAsync(user.Id.ToString());
        Assert.Null(deleted);
    }

    [Fact]
    public async Task RevokeTokenAsync_UserNotFound_ReturnsBadRequest()
    {
        var (service, _, _, _) = CreateService();
        var result = await service.RevokeTokenAsync(Guid.NewGuid());

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        Assert.Equal("Invalid user", result.Message);
    }

    [Fact]
    public async Task RevokeTokenAsync_ValidUser_ClearsBothRefreshTokenAndFcmToken()
    {
        var (service, userMgr, _, _) = CreateService();
        var user = new ApplicationUser
        {
            UserName = "revoke@test.com",
            Email = "revoke@test.com",
            RefreshToken = "existing_refresh_token",
            FcmToken = "fcm_token_device"
        };
        await userMgr.CreateAsync(user, "Password123");

        var result = await service.RevokeTokenAsync(user.Id);

        Assert.True(result.Succeeded);
        Assert.Equal("Token revoked successfully.", result.Data);

        var updated = await userMgr.FindByIdAsync(user.Id.ToString());
        Assert.Null(updated!.RefreshToken);
        Assert.Null(updated.FcmToken);
    }
}
