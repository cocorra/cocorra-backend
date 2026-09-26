using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Cocorra.BLL.Services.Email;
using Cocorra.BLL.Services.EventTracking;
using Cocorra.BLL.Services.OTPService;
using Cocorra.DAL.Models;
using Cocorra.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Moq;
using Xunit;

namespace Cocorra.Tests;

public class OTPServiceSecurityTests
{
    private readonly Mock<UserManager<ApplicationUser>> _userManagerMock = TestIdentityHelper.CreateMockUserManager();
    private readonly Mock<IEmailService> _emailServiceMock = new();
    private readonly Mock<IConfiguration> _configMock = new();
    private readonly Mock<IHttpContextAccessor> _httpContextAccessorMock = new();
    private readonly Mock<IEventTracker> _eventTrackerMock = new();
    private readonly IOtpAttemptLimiter _attemptLimiter = new OtpAttemptLimiter(new MemoryCache(new MemoryCacheOptions()));
    private readonly OTPService _service;

    public OTPServiceSecurityTests()
    {
        _configMock.Setup(c => c["AppSettings:BaseUrl"]).Returns("https://api.cocorra.com");
        _service = new OTPService(
            _configMock.Object,
            _userManagerMock.Object,
            _emailServiceMock.Object,
            _httpContextAccessorMock.Object,
            _eventTrackerMock.Object,
            _attemptLimiter
        );
    }

    [Fact]
    public async Task VerifyOtpAsync_UnknownEmail_ReturnsBadRequestWithSameMessageAsWrongCode()
    {
        var unknownEmail = "unknown@cocorra.com";
        _userManagerMock.Setup(m => m.FindByEmailAsync(unknownEmail)).ReturnsAsync((ApplicationUser?)null);

        var knownEmail = "known@cocorra.com";
        var user = new ApplicationUser { Id = Guid.NewGuid(), Email = knownEmail, EmailConfirmed = false };
        _userManagerMock.Setup(m => m.FindByEmailAsync(knownEmail)).ReturnsAsync(user);
        _userManagerMock.Setup(m => m.VerifyUserTokenAsync(user, TokenOptions.DefaultEmailProvider, OtpPurposes.EmailConfirmation, "wrong-code"))
            .ReturnsAsync(false);

        var unknownResult = await _service.VerifyOtpAsync(unknownEmail, "123456");
        var wrongCodeResult = await _service.VerifyOtpAsync(knownEmail, "wrong-code");

        Assert.False(unknownResult.Succeeded);
        Assert.False(wrongCodeResult.Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, unknownResult.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, wrongCodeResult.StatusCode);
        Assert.Equal(OTPService.InvalidOtpMessage, unknownResult.Message);
        Assert.Equal(OTPService.InvalidOtpMessage, wrongCodeResult.Message);
        Assert.Equal(wrongCodeResult.Message, unknownResult.Message);
    }

    [Fact]
    public async Task VerifyOtpAsync_AfterMaxFailedAttempts_NextCallReturns429AndDoesNotCallVerifyUserTokenAsync()
    {
        var email = "user@cocorra.com";
        var user = new ApplicationUser { Id = Guid.NewGuid(), Email = email, EmailConfirmed = false };
        _userManagerMock.Setup(m => m.FindByEmailAsync(email)).ReturnsAsync(user);
        _userManagerMock.Setup(m => m.VerifyUserTokenAsync(user, TokenOptions.DefaultEmailProvider, OtpPurposes.EmailConfirmation, "wrong"))
            .ReturnsAsync(false);
        _userManagerMock.Setup(m => m.VerifyUserTokenAsync(user, TokenOptions.DefaultEmailProvider, OtpPurposes.EmailConfirmation, "correct"))
            .ReturnsAsync(true);

        // Exhaust MaxFailedAttempts (5)
        for (int i = 0; i < OtpAttemptLimiter.MaxFailedAttempts; i++)
        {
            var failResult = await _service.VerifyOtpAsync(email, "wrong");
            Assert.Equal(HttpStatusCode.BadRequest, failResult.StatusCode);
        }

        _userManagerMock.Verify(m => m.VerifyUserTokenAsync(user, TokenOptions.DefaultEmailProvider, OtpPurposes.EmailConfirmation, "wrong"),
            Times.Exactly(OtpAttemptLimiter.MaxFailedAttempts));

        // The (MaxFailedAttempts + 1)th call with correct code must be rejected with 429
        var lockedResult = await _service.VerifyOtpAsync(email, "correct");

        Assert.False(lockedResult.Succeeded);
        Assert.Equal(HttpStatusCode.TooManyRequests, lockedResult.StatusCode);
        Assert.Equal(OTPService.TooManyAttemptsMessage, lockedResult.Message);

        // VerifyUserTokenAsync was NOT called for the locked call
        _userManagerMock.Verify(m => m.VerifyUserTokenAsync(user, TokenOptions.DefaultEmailProvider, OtpPurposes.EmailConfirmation, "correct"),
            Times.Never);
        // Total verify calls remains exactly MaxFailedAttempts
        _userManagerMock.Verify(m => m.VerifyUserTokenAsync(user, TokenOptions.DefaultEmailProvider, OtpPurposes.EmailConfirmation, It.IsAny<string>()),
            Times.Exactly(OtpAttemptLimiter.MaxFailedAttempts));
    }

    [Fact]
    public async Task VerifyOtpAsync_SuccessfulVerificationResetsCounter_AllowsFiveMoreAttempts()
    {
        var email = "reset-test@cocorra.com";
        var user = new ApplicationUser { Id = Guid.NewGuid(), Email = email, EmailConfirmed = false };
        _userManagerMock.Setup(m => m.FindByEmailAsync(email)).ReturnsAsync(user);
        _userManagerMock.Setup(m => m.VerifyUserTokenAsync(user, TokenOptions.DefaultEmailProvider, OtpPurposes.EmailConfirmation, "wrong"))
            .ReturnsAsync(false);
        _userManagerMock.Setup(m => m.VerifyUserTokenAsync(user, TokenOptions.DefaultEmailProvider, OtpPurposes.EmailConfirmation, "correct"))
            .ReturnsAsync(true);
        _userManagerMock.Setup(m => m.UpdateAsync(user)).ReturnsAsync(IdentityResult.Success);

        // 4 failed attempts
        for (int i = 0; i < OtpAttemptLimiter.MaxFailedAttempts - 1; i++)
        {
            var res = await _service.VerifyOtpAsync(email, "wrong");
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        }

        // 5th attempt: successful verification resets counter
        var successRes = await _service.VerifyOtpAsync(email, "correct");
        Assert.True(successRes.Succeeded);
        Assert.Equal(HttpStatusCode.OK, successRes.StatusCode);

        // After success reset, 5 more failed attempts are permitted
        for (int i = 0; i < OtpAttemptLimiter.MaxFailedAttempts; i++)
        {
            var res = await _service.VerifyOtpAsync(email, "wrong");
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        }

        // 6th attempt after reset returns 429
        var lockedRes = await _service.VerifyOtpAsync(email, "wrong");
        Assert.False(lockedRes.Succeeded);
        Assert.Equal(HttpStatusCode.TooManyRequests, lockedRes.StatusCode);
    }

    [Fact]
    public async Task VerifyOtpAsync_UsesEmailConfirmationPurpose_PasswordResetCodeIsRejected()
    {
        var email = "purpose-check@cocorra.com";
        var user = new ApplicationUser { Id = Guid.NewGuid(), Email = email, EmailConfirmed = false };
        _userManagerMock.Setup(m => m.FindByEmailAsync(email)).ReturnsAsync(user);

        // Token configured to be valid for PasswordReset but invalid for EmailConfirmation
        _userManagerMock.Setup(m => m.VerifyUserTokenAsync(user, TokenOptions.DefaultEmailProvider, OtpPurposes.PasswordReset, "reset-only-code"))
            .ReturnsAsync(true);
        _userManagerMock.Setup(m => m.VerifyUserTokenAsync(user, TokenOptions.DefaultEmailProvider, OtpPurposes.EmailConfirmation, "reset-only-code"))
            .ReturnsAsync(false);

        var result = await _service.VerifyOtpAsync(email, "reset-only-code");

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        Assert.Equal(OTPService.InvalidOtpMessage, result.Message);

        // Verify it was checked specifically with OtpPurposes.EmailConfirmation
        _userManagerMock.Verify(m => m.VerifyUserTokenAsync(user, TokenOptions.DefaultEmailProvider, OtpPurposes.EmailConfirmation, "reset-only-code"),
            Times.Once);
        // And never checked with OtpPurposes.PasswordReset
        _userManagerMock.Verify(m => m.VerifyUserTokenAsync(user, TokenOptions.DefaultEmailProvider, OtpPurposes.PasswordReset, It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task ResendOtpAsync_UnknownConfirmedAndValidUser_AllReturn200WithIdenticalMessage()
    {
        var unknownEmail = "unknown@cocorra.com";
        _userManagerMock.Setup(m => m.FindByEmailAsync(unknownEmail)).ReturnsAsync((ApplicationUser?)null);

        var confirmedEmail = "confirmed@cocorra.com";
        var confirmedUser = new ApplicationUser { Email = confirmedEmail, EmailConfirmed = true };
        _userManagerMock.Setup(m => m.FindByEmailAsync(confirmedEmail)).ReturnsAsync(confirmedUser);

        var validEmail = "valid@cocorra.com";
        var validUser = new ApplicationUser { Id = Guid.NewGuid(), FirstName = "Valid", Email = validEmail, EmailConfirmed = false };
        _userManagerMock.Setup(m => m.FindByEmailAsync(validEmail)).ReturnsAsync(validUser);
        _userManagerMock.Setup(m => m.GenerateUserTokenAsync(validUser, TokenOptions.DefaultEmailProvider, OtpPurposes.EmailConfirmation))
            .ReturnsAsync("123456");

        var unknownRes = await _service.ResendOtpAsync(unknownEmail);
        var confirmedRes = await _service.ResendOtpAsync(confirmedEmail);
        var validRes = await _service.ResendOtpAsync(validEmail);

        Assert.Equal(HttpStatusCode.OK, unknownRes.StatusCode);
        Assert.Equal(HttpStatusCode.OK, confirmedRes.StatusCode);
        Assert.Equal(HttpStatusCode.OK, validRes.StatusCode);

        Assert.True(unknownRes.Succeeded);
        Assert.True(confirmedRes.Succeeded);
        Assert.True(validRes.Succeeded);

        Assert.Equal(OTPService.ResendGenericMessage, unknownRes.Data);
        Assert.Equal(OTPService.ResendGenericMessage, confirmedRes.Data);
        Assert.Equal(OTPService.ResendGenericMessage, validRes.Data);

        Assert.Equal(unknownRes.Message, confirmedRes.Message);
        Assert.Equal(confirmedRes.Message, validRes.Message);
    }

    [Fact]
    public async Task ResendOtpAsync_TwoCallsInARowForValidUser_SendsExactlyOneEmailDueToCooldown()
    {
        var email = "cooldown@cocorra.com";
        var user = new ApplicationUser { Id = Guid.NewGuid(), FirstName = "Bob", Email = email, EmailConfirmed = false };
        _userManagerMock.Setup(m => m.FindByEmailAsync(email)).ReturnsAsync(user);
        _userManagerMock.Setup(m => m.GenerateUserTokenAsync(user, TokenOptions.DefaultEmailProvider, OtpPurposes.EmailConfirmation))
            .ReturnsAsync("112233");

        var firstResult = await _service.ResendOtpAsync(email);
        var secondResult = await _service.ResendOtpAsync(email);

        Assert.True(firstResult.Succeeded);
        Assert.True(secondResult.Succeeded);
        Assert.Equal(HttpStatusCode.OK, firstResult.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secondResult.StatusCode);
        Assert.Equal(OTPService.ResendGenericMessage, firstResult.Data);
        Assert.Equal(OTPService.ResendGenericMessage, secondResult.Data);

        // Only one email sent because the second request was blocked by cooldown
        _emailServiceMock.Verify(e => e.SendOtpEmailAsync(
            email,
            "Bob",
            email,
            "112233",
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()
        ), Times.Once);
    }

    [Fact]
    public async Task ResendOtpAsync_WhenSendOtpEmailThrowsHttpRequestException_Returns200WithGenericMessage()
    {
        var email = "network-error@cocorra.com";
        var user = new ApplicationUser { Id = Guid.NewGuid(), FirstName = "Charlie", Email = email, EmailConfirmed = false };
        _userManagerMock.Setup(m => m.FindByEmailAsync(email)).ReturnsAsync(user);
        _userManagerMock.Setup(m => m.GenerateUserTokenAsync(user, TokenOptions.DefaultEmailProvider, OtpPurposes.EmailConfirmation))
            .ReturnsAsync("445566");
        _emailServiceMock.Setup(e => e.SendOtpEmailAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()
        )).ThrowsAsync(new HttpRequestException("Connection refused"));

        var result = await _service.ResendOtpAsync(email);

        Assert.True(result.Succeeded);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal(OTPService.ResendGenericMessage, result.Data);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ResendOtpAsync_NullOrWhitespaceInput_ReturnsBadRequestAndNeverCallsUserManager(string? input)
    {
        var result = await _service.ResendOtpAsync(input!);

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        Assert.Equal("Email is required.", result.Message);
        _userManagerMock.Verify(m => m.FindByEmailAsync(It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData(null, "123456")]
    [InlineData("", "123456")]
    [InlineData("   ", "123456")]
    [InlineData("user@cocorra.com", null)]
    [InlineData("user@cocorra.com", "")]
    [InlineData("user@cocorra.com", "   ")]
    public async Task VerifyOtpAsync_NullOrWhitespaceInput_ReturnsBadRequestAndNeverCallsUserManager(string? email, string? otpCode)
    {
        var result = await _service.VerifyOtpAsync(email!, otpCode!);

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        Assert.Equal(OTPService.InvalidOtpMessage, result.Message);
        _userManagerMock.Verify(m => m.FindByEmailAsync(It.IsAny<string>()), Times.Never);
    }
}
