using Microsoft.Extensions.Options;
using Cocorra.Tests.Helpers;
using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Cocorra.BLL.Services.AuthServices;
using Cocorra.BLL.Services.BlockedDevicesService;
using Cocorra.BLL.Services.Email;
using Cocorra.BLL.Services.EventTracking;
using Cocorra.BLL.Services.OTPService;
using Cocorra.BLL.Services.RoomService;
using Cocorra.BLL.Services.Upload;
using Cocorra.DAL.Data;
using Cocorra.DAL.DTOS.Auth;
using Cocorra.DAL.Models;
using Cocorra.DAL.Repository.RoomRepository;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Cocorra.Tests;

public class AuthServicesOtpSecurityTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _services;
    private readonly Mock<IConfiguration> _configMock = new();
    private readonly Mock<IUploadVoice> _uploadVoiceMock = new();
    private readonly Mock<IEmailService> _emailServiceMock = new();
    private readonly Mock<IUploadImage> _uploadImageMock = new();
    private readonly Mock<IRoomRepository> _roomRepoMock = new();
    private readonly Mock<IEventTracker> _eventTrackerMock = new();
    private readonly Mock<IRoomService> _roomServiceMock = new();
    private readonly Mock<IBlockedDevicesService> _blockedDevicesServiceMock = new();
    private readonly IOtpAttemptLimiter _otpAttemptLimiter = new OtpAttemptLimiter(new MemoryCache(new MemoryCacheOptions()));
    private const string TestOtpHmacKey = "test-only-otp-hmac-key-at-least-32-chars";
    private OtpCodeService _otpCodes = null!;

    public AuthServicesOtpSecurityTests()
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
        _otpCodes = new OtpCodeService(db, Options.Create(new OtpOptions { HmacKey = TestOtpHmacKey }));

        var service = new AuthServices(
            userMgr,
            roleMgr,
            _configMock.Object,
            _uploadVoiceMock.Object,
            new InlineEmailQueue(_emailServiceMock.Object, _otpCodes),
            _uploadImageMock.Object,
            db,
            _roomRepoMock.Object,
            _eventTrackerMock.Object,
            _roomServiceMock.Object,
            _blockedDevicesServiceMock.Object,
            _otpAttemptLimiter,
            _otpCodes
        );

        return (service, userMgr, roleMgr, db);
    }

    [Fact]
    public async Task ForgotPasswordAsync_UnknownAndKnownEmail_ReturnIdenticalSuccessAndMessageText()
    {
        var (service, userMgr, _, _) = CreateService();
        var knownEmail = "known@cocorra.com";
        var user = new ApplicationUser
        {
            UserName = knownEmail,
            Email = knownEmail,
            FirstName = "Known"
        };
        await userMgr.CreateAsync(user, "Password123");

        var unknownResult = await service.ForgotPasswordAsync(new ForgotPasswordDto { Email = "unknown@cocorra.com" });
        var knownResult = await service.ForgotPasswordAsync(new ForgotPasswordDto { Email = knownEmail });

        Assert.True(unknownResult.Succeeded);
        Assert.True(knownResult.Succeeded);
        Assert.Equal(HttpStatusCode.OK, unknownResult.StatusCode);
        Assert.Equal(HttpStatusCode.OK, knownResult.StatusCode);

        Assert.Equal(AuthServices.ForgotPasswordGenericMessage, unknownResult.Data);
        Assert.Equal(AuthServices.ForgotPasswordGenericMessage, knownResult.Data);
        Assert.Equal(unknownResult.Message, knownResult.Message);
    }

    [Fact]
    public async Task ForgotPasswordAsync_WhenSendPasswordResetEmailThrows_ResultIsStillSuccessWithSameText()
    {
        var (service, userMgr, _, _) = CreateService();
        var email = "reset-throw@cocorra.com";
        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            FirstName = "User"
        };
        await userMgr.CreateAsync(user, "Password123");

        _emailServiceMock.Setup(e => e.SendPasswordResetEmailAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()
        )).ThrowsAsync(new HttpRequestException("Resend API down"));

        var result = await service.ForgotPasswordAsync(new ForgotPasswordDto { Email = email });

        Assert.True(result.Succeeded);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal(AuthServices.ForgotPasswordGenericMessage, result.Data);
    }

    [Fact]
    public async Task ForgotPasswordAsync_UsesPasswordResetPurpose_AndSecondCallWithinCooldownSendsNoSecondEmail()
    {
        var (service, userMgr, _, _) = CreateService();
        var email = "purpose-cooldown@cocorra.com";
        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            FirstName = "PurposeUser"
        };
        await userMgr.CreateAsync(user, "Password123");

        string? capturedOtp = null;
        _emailServiceMock.Setup(e => e.SendPasswordResetEmailAsync(
            email,
            "PurposeUser",
            email,
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()
        )).Callback<string, string, string, string, string, CancellationToken>((to, name, em, code, logo, ct) =>
        {
            capturedOtp = code;
        }).Returns(Task.CompletedTask);

        // First call generates OTP and sends email
        var firstResult = await service.ForgotPasswordAsync(new ForgotPasswordDto { Email = email });
        Assert.True(firstResult.Succeeded);
        Assert.NotNull(capturedOtp);

        // Verify the code is valid specifically for PasswordReset purpose, not EmailConfirmation
        // (checked first: a successful check uses the code up).
        var validForConfirm = await _otpCodes.TryConsumeAsync(user.Id, OtpPurposes.EmailConfirmation, capturedOtp!);
        var validForReset = await _otpCodes.TryConsumeAsync(user.Id, OtpPurposes.PasswordReset, capturedOtp!);
        Assert.False(validForConfirm);
        Assert.True(validForReset);

        // Second call within cooldown window returns success without sending email
        var secondResult = await service.ForgotPasswordAsync(new ForgotPasswordDto { Email = email });
        Assert.True(secondResult.Succeeded);
        Assert.Equal(AuthServices.ForgotPasswordGenericMessage, secondResult.Data);

        // Exactly one email was dispatched
        _emailServiceMock.Verify(e => e.SendPasswordResetEmailAsync(
            email,
            "PurposeUser",
            email,
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()
        ), Times.Once);
    }

    [Fact]
    public async Task ResetPasswordAsync_UnknownEmail_Returns400WithSameMessageAsWrongCodeForKnownUser()
    {
        var (service, userMgr, _, _) = CreateService();
        var knownEmail = "known-user@cocorra.com";
        var user = new ApplicationUser
        {
            UserName = knownEmail,
            Email = knownEmail
        };
        await userMgr.CreateAsync(user, "OldPassword123");

        var unknownResult = await service.ResetPasswordAsync(new ResetPasswordDto
        {
            Email = "unknown@cocorra.com",
            OtpCode = "123456",
            NewPassword = "NewPassword123"
        });

        var wrongCodeResult = await service.ResetPasswordAsync(new ResetPasswordDto
        {
            Email = knownEmail,
            OtpCode = "wrong-code",
            NewPassword = "NewPassword123"
        });

        Assert.False(unknownResult.Succeeded);
        Assert.False(wrongCodeResult.Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, unknownResult.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, wrongCodeResult.StatusCode);
        Assert.Equal("Invalid or expired OTP code.", unknownResult.Message);
        Assert.Equal("Invalid or expired OTP code.", wrongCodeResult.Message);
        Assert.Equal(wrongCodeResult.Message, unknownResult.Message);
    }

    [Fact]
    public async Task ResetPasswordAsync_AfterMaxFailedAttempts_Returns429AndDoesNotResetPassword()
    {
        var (service, userMgr, _, _) = CreateService();
        var email = "too-many-attempts@cocorra.com";
        var user = new ApplicationUser
        {
            UserName = email,
            Email = email
        };
        await userMgr.CreateAsync(user, "InitialPassword123");

        // Real valid reset token
        var validCode = await _otpCodes.IssueAsync(user.Id, OtpPurposes.PasswordReset);

        // Exhaust MaxFailedAttempts (5) with wrong code
        for (int i = 0; i < OtpAttemptLimiter.MaxFailedAttempts; i++)
        {
            var res = await service.ResetPasswordAsync(new ResetPasswordDto
            {
                Email = email,
                OtpCode = "000000",
                NewPassword = "NewPassword123"
            });
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        }

        // 6th attempt with the CORRECT code is blocked by rate limiting
        var lockedResult = await service.ResetPasswordAsync(new ResetPasswordDto
        {
            Email = email,
            OtpCode = validCode,
            NewPassword = "NewPassword123"
        });

        Assert.False(lockedResult.Succeeded);
        Assert.Equal(HttpStatusCode.TooManyRequests, lockedResult.StatusCode);
        Assert.Equal("Too many failed attempts. Please try again later.", lockedResult.Message);

        // Verify password was NOT changed and remains original
        var freshUser = await userMgr.FindByEmailAsync(email);
        Assert.True(await userMgr.CheckPasswordAsync(freshUser!, "InitialPassword123"));
        Assert.False(await userMgr.CheckPasswordAsync(freshUser!, "NewPassword123"));
    }

    [Fact]
    public async Task ResetPasswordAsync_RejectedNewPassword_DoesNotUseUpTheCode()
    {
        var (service, userMgr, _, _) = CreateService();
        var email = "weak-password@cocorra.com";
        var user = new ApplicationUser { UserName = email, Email = email };
        await userMgr.CreateAsync(user, "InitialPassword123");
        var code = await _otpCodes.IssueAsync(user.Id, OtpPurposes.PasswordReset);

        // Shorter than the test host's RequiredLength (6).
        var weak = await service.ResetPasswordAsync(new ResetPasswordDto { Email = email, OtpCode = code, NewPassword = "abc" });
        var retry = await service.ResetPasswordAsync(new ResetPasswordDto { Email = email, OtpCode = code, NewPassword = "NewPassword123" });

        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
        Assert.StartsWith("Password reset failed:", weak.Message);
        Assert.True(retry.Succeeded, retry.Message);
        Assert.True(await userMgr.CheckPasswordAsync((await userMgr.FindByEmailAsync(email))!, "NewPassword123"));
    }

    [Fact]
    public async Task ResetPasswordAsync_SameCodeTwice_SecondUseIsRejected()
    {
        var (service, userMgr, _, _) = CreateService();
        var email = "single-use@cocorra.com";
        var user = new ApplicationUser { UserName = email, Email = email };
        await userMgr.CreateAsync(user, "InitialPassword123");
        var code = await _otpCodes.IssueAsync(user.Id, OtpPurposes.PasswordReset);

        var first = await service.ResetPasswordAsync(new ResetPasswordDto { Email = email, OtpCode = code, NewPassword = "NewPassword123" });
        var second = await service.ResetPasswordAsync(new ResetPasswordDto { Email = email, OtpCode = code, NewPassword = "AnotherPassword123" });

        Assert.True(first.Succeeded, first.Message);
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
        Assert.Equal("Invalid or expired OTP code.", second.Message);
        Assert.True(await userMgr.CheckPasswordAsync((await userMgr.FindByEmailAsync(email))!, "NewPassword123"));
    }

    [Fact]
    public async Task ResetPasswordAsync_VerifiesWithPasswordResetPurpose_EmailConfirmationCodeIsRejected()
    {
        var (service, userMgr, _, _) = CreateService();
        var email = "purpose-check@cocorra.com";
        var user = new ApplicationUser
        {
            UserName = email,
            Email = email
        };
        await userMgr.CreateAsync(user, "InitialPassword123");

        // Code generated for EmailConfirmation purpose, NOT PasswordReset
        var emailConfirmationCode = await _otpCodes.IssueAsync(user.Id, OtpPurposes.EmailConfirmation);

        var result = await service.ResetPasswordAsync(new ResetPasswordDto
        {
            Email = email,
            OtpCode = emailConfirmationCode,
            NewPassword = "NewPassword123"
        });

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        Assert.Equal("Invalid or expired OTP code.", result.Message);

        // Password not changed
        var freshUser = await userMgr.FindByEmailAsync(email);
        Assert.True(await userMgr.CheckPasswordAsync(freshUser!, "InitialPassword123"));

        // Now test that a code generated for PasswordReset succeeds
        var passwordResetCode = await _otpCodes.IssueAsync(user.Id, OtpPurposes.PasswordReset);

        var successResult = await service.ResetPasswordAsync(new ResetPasswordDto
        {
            Email = email,
            OtpCode = passwordResetCode,
            NewPassword = "NewPassword123"
        });

        Assert.True(successResult.Succeeded);
        Assert.Equal(HttpStatusCode.OK, successResult.StatusCode);
        Assert.Equal("Password has been reset successfully.", successResult.Data);

        var updatedUser = await userMgr.FindByEmailAsync(email);
        Assert.True(await userMgr.CheckPasswordAsync(updatedUser!, "NewPassword123"));
    }
}
