using System;
using System.Net;
using System.Threading.Tasks;
using Cocorra.BLL.Services.Email;
using Cocorra.BLL.Services.EventTracking;
using Cocorra.BLL.Services.OTPService;
using Cocorra.DAL.Models;
using Cocorra.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Moq;
using Xunit;

namespace Cocorra.Tests;

public class OTPServiceTests
{
    private readonly Mock<UserManager<ApplicationUser>> _userManagerMock = TestIdentityHelper.CreateMockUserManager();
    private readonly Mock<IEmailService> _emailServiceMock = new();
    private readonly Mock<IConfiguration> _configMock = new();
    private readonly Mock<IHttpContextAccessor> _httpContextAccessorMock = new();
    private readonly Mock<IEventTracker> _eventTrackerMock = new();
    private readonly OTPService _service;

    public OTPServiceTests()
    {
        _configMock.Setup(c => c["AppSettings:BaseUrl"]).Returns("https://api.cocorra.com");
        _service = new OTPService(
            _configMock.Object,
            _userManagerMock.Object,
            _emailServiceMock.Object,
            _httpContextAccessorMock.Object,
            _eventTrackerMock.Object
        );
    }

    [Fact]
    public async Task ResendOtpAsync_UserNotFound_ReturnsBadRequest()
    {
        var email = "unknown@cocorra.com";
        _userManagerMock.Setup(m => m.FindByEmailAsync(email)).ReturnsAsync((ApplicationUser?)null);

        var result = await _service.ResendOtpAsync(email);

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        Assert.Equal("User not found", result.Message);
        _emailServiceMock.Verify(e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ResendOtpAsync_EmailAlreadyConfirmed_ReturnsBadRequest()
    {
        var email = "verified@cocorra.com";
        var user = new ApplicationUser { Email = email, EmailConfirmed = true };
        _userManagerMock.Setup(m => m.FindByEmailAsync(email)).ReturnsAsync(user);

        var result = await _service.ResendOtpAsync(email);

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        Assert.Equal("Email is already confirmed", result.Message);
        _emailServiceMock.Verify(e => e.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ResendOtpAsync_ValidUser_GeneratesTokenAndSendsEmail()
    {
        var email = "pending@cocorra.com";
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            FirstName = "Ali",
            Email = email,
            EmailConfirmed = false
        };
        _userManagerMock.Setup(m => m.FindByEmailAsync(email)).ReturnsAsync(user);
        _userManagerMock.Setup(m => m.GenerateTwoFactorTokenAsync(user, TokenOptions.DefaultEmailProvider))
            .ReturnsAsync("123456");

        var result = await _service.ResendOtpAsync(email);

        Assert.True(result.Succeeded);
        Assert.Equal("OTP code resent successfully", result.Data);
        _emailServiceMock.Verify(e => e.SendEmailAsync(
            email,
            "Resend OTP",
            It.Is<string>(html => html.Contains("123456") && html.Contains("Ali"))
        ), Times.Once);
    }

    [Fact]
    public async Task VerifyOtpAsync_UserNotFound_ReturnsBadRequest()
    {
        var email = "unknown@cocorra.com";
        _userManagerMock.Setup(m => m.FindByEmailAsync(email)).ReturnsAsync((ApplicationUser?)null);

        var result = await _service.VerifyOtpAsync(email, "123456");

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        Assert.Equal("User not found", result.Message);
    }

    [Fact]
    public async Task VerifyOtpAsync_InvalidOtp_ReturnsBadRequest()
    {
        var email = "user@cocorra.com";
        var user = new ApplicationUser { Id = Guid.NewGuid(), Email = email, EmailConfirmed = false };
        _userManagerMock.Setup(m => m.FindByEmailAsync(email)).ReturnsAsync(user);
        _userManagerMock.Setup(m => m.VerifyTwoFactorTokenAsync(user, TokenOptions.DefaultEmailProvider, "wrong"))
            .ReturnsAsync(false);

        var result = await _service.VerifyOtpAsync(email, "wrong");

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        Assert.Equal("Invalid OTP code", result.Message);
        Assert.False(user.EmailConfirmed);
        _userManagerMock.Verify(m => m.UpdateAsync(user), Times.Never);
    }

    [Fact]
    public async Task VerifyOtpAsync_ValidOtp_ConfirmsEmailUpdatesUserAndTracksEvent()
    {
        var userId = Guid.NewGuid();
        var email = "user@cocorra.com";
        var user = new ApplicationUser { Id = userId, Email = email, EmailConfirmed = false };
        _userManagerMock.Setup(m => m.FindByEmailAsync(email)).ReturnsAsync(user);
        _userManagerMock.Setup(m => m.VerifyTwoFactorTokenAsync(user, TokenOptions.DefaultEmailProvider, "999888"))
            .ReturnsAsync(true);
        _userManagerMock.Setup(m => m.UpdateAsync(user)).ReturnsAsync(IdentityResult.Success);

        var result = await _service.VerifyOtpAsync(email, "999888");

        Assert.True(result.Succeeded);
        Assert.Equal("Email confirmed successfully", result.Data);
        Assert.True(user.EmailConfirmed);
        _userManagerMock.Verify(m => m.UpdateAsync(user), Times.Once);
        _eventTrackerMock.Verify(t => t.Track(EventTypes.EmailConfirmed, userId, null), Times.Once);
    }
}
