using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Cocorra.API.Controllers;
using Cocorra.BLL.Base;
using Cocorra.BLL.Services.Auth;
using Cocorra.BLL.Services.OTPService;
using Cocorra.DAL.DTOS.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Moq;
using Xunit;

namespace Cocorra.Tests;

public class AuthenticationControllerOtpTests
{
    private readonly Mock<IAuthServices> _authServicesMock = new();
    private readonly Mock<IOTPService> _otpServiceMock = new();

    private AuthenticationController CreateController()
    {
        return new AuthenticationController(_authServicesMock.Object, _otpServiceMock.Object);
    }

    // ── Attribute Reflection Tests ──────────────────────────────────────────

    [Theory]
    [InlineData(nameof(AuthenticationController.ForgotPassword), typeof(HttpPostAttribute))]
    [InlineData(nameof(AuthenticationController.ResendOtp), typeof(HttpPostAttribute))]
    [InlineData(nameof(AuthenticationController.ResetPassword), typeof(HttpPostAttribute))]
    public void OtpActions_HaveEnableRateLimitingOtpAttribute(string actionName, Type httpAttributeType)
    {
        var method = typeof(AuthenticationController).GetMethods()
            .FirstOrDefault(m => m.Name == actionName && m.GetCustomAttributes(httpAttributeType, inherit: false).Any());

        Assert.NotNull(method);

        var rateLimiting = method!.GetCustomAttributes(typeof(EnableRateLimitingAttribute), inherit: false)
            .OfType<EnableRateLimitingAttribute>()
            .FirstOrDefault();

        Assert.NotNull(rateLimiting);
        Assert.Equal("otp", rateLimiting!.PolicyName);
    }

    [Fact]
    public void ConfirmEmail_Post_HasEnableRateLimitingOtpAttribute()
    {
        var method = typeof(AuthenticationController).GetMethods()
            .FirstOrDefault(m => m.Name == nameof(AuthenticationController.ConfirmEmail) &&
                                 m.GetCustomAttributes(typeof(HttpPostAttribute), inherit: false).Any());

        Assert.NotNull(method);

        var rateLimiting = method!.GetCustomAttributes(typeof(EnableRateLimitingAttribute), inherit: false)
            .OfType<EnableRateLimitingAttribute>()
            .FirstOrDefault();

        Assert.NotNull(rateLimiting);
        Assert.Equal("otp", rateLimiting!.PolicyName);
    }

    [Fact]
    public void ConfirmEmail_Get_HasEnableRateLimitingOtpAttribute()
    {
        var method = typeof(AuthenticationController).GetMethods()
            .FirstOrDefault(m => m.Name == nameof(AuthenticationController.ConfirmEmail) &&
                                 m.GetCustomAttributes(typeof(HttpGetAttribute), inherit: false).Any());

        Assert.NotNull(method);

        var rateLimiting = method!.GetCustomAttributes(typeof(EnableRateLimitingAttribute), inherit: false)
            .OfType<EnableRateLimitingAttribute>()
            .FirstOrDefault();

        Assert.NotNull(rateLimiting);
        Assert.Equal("otp", rateLimiting!.PolicyName);
    }

    // ── Action Behavior Tests ───────────────────────────────────────────────

    [Theory]
    [InlineData(HttpStatusCode.OK, 200)]
    [InlineData(HttpStatusCode.BadRequest, 400)]
    [InlineData(HttpStatusCode.TooManyRequests, 429)]
    public async Task ConfirmEmail_Post_ValidDto_CallsVerifyOtpAsyncAndReturnsServiceStatusCode(
        HttpStatusCode serviceStatusCode, int expectedHttpCode)
    {
        var controller = CreateController();
        var dto = new ConfirmEmailDto
        {
            Email = "user@example.com",
            OtpCode = "123456"
        };

        var serviceResponse = new Response<string>
        {
            StatusCode = serviceStatusCode,
            Succeeded = expectedHttpCode == 200,
            Message = "Verification outcome",
            Data = "Email confirmed successfully"
        };

        _otpServiceMock.Setup(s => s.VerifyOtpAsync("user@example.com", "123456"))
            .ReturnsAsync(serviceResponse);

        var result = await controller.ConfirmEmail(dto);

        _otpServiceMock.Verify(s => s.VerifyOtpAsync("user@example.com", "123456"), Times.Once);
        var objResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(expectedHttpCode, objResult.StatusCode);
        Assert.Equal(serviceResponse, objResult.Value);
    }

    [Theory]
    [InlineData(null, "123456")]
    [InlineData("", "123456")]
    [InlineData("   ", "123456")]
    [InlineData("user@example.com", null)]
    [InlineData("user@example.com", "")]
    [InlineData("user@example.com", "   ")]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("   ", "   ")]
    public async Task ConfirmEmail_Get_MissingEmailOrCode_ReturnsBadRequestAndDoesNotCallService(string? email, string? otpCode)
    {
        var controller = CreateController();

        var result = await controller.ConfirmEmail(email, otpCode);

        var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(400, badRequestResult.StatusCode);
        Assert.Equal("Email and OTP code are required.", badRequestResult.Value);

        _otpServiceMock.Verify(s => s.VerifyOtpAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ResendOtp_NullOrWhitespaceEmail_ReturnsBadRequestAndDoesNotCallService(string? email)
    {
        var controller = CreateController();

        var result = await controller.ResendOtp(email);

        var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(400, badRequestResult.StatusCode);
        Assert.Equal("Email is required.", badRequestResult.Value);

        _otpServiceMock.Verify(s => s.ResendOtpAsync(It.IsAny<string>()), Times.Never);
    }
}
