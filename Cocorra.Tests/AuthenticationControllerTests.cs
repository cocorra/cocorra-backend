using System.Security.Claims;
using Cocorra.API.Controllers;
using Cocorra.BLL.Base;
using Cocorra.BLL.DTOS.Auth;
using Cocorra.BLL.Services.Auth;
using Cocorra.BLL.Services.OTPService;
using Cocorra.DAL.DTOS.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Cocorra.Tests;

public class AuthenticationControllerTests
{
    private readonly Mock<IAuthServices> _authServiceMock = new();
    private readonly Mock<IOTPService> _otpServiceMock = new();

    private AuthenticationController CreateController(Guid? userId = null, string? email = null)
    {
        var controller = new AuthenticationController(_authServiceMock.Object, _otpServiceMock.Object);

        var claims = new List<Claim>();
        if (userId.HasValue)
        {
            claims.Add(new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()));
        }
        if (!string.IsNullOrEmpty(email))
        {
            claims.Add(new Claim(ClaimTypes.Email, email));
        }

        var identity = new ClaimsIdentity(claims, "TestAuth");
        var principal = new ClaimsPrincipal(identity);

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };

        return controller;
    }

    [Fact]
    public async Task Register_InvalidModelState_ReturnsBadRequest()
    {
        var controller = CreateController();
        controller.ModelState.AddModelError("Email", "Required");

        var result = await controller.Register(new RegisterDto());

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Register_Success_ReturnsOk()
    {
        var dto = new RegisterDto { Email = "test@example.com", Password = "Pass" };
        var serviceResponse = new Response<object>
        {
            Succeeded = true,
            StatusCode = System.Net.HttpStatusCode.OK,
            Data = "Registration successful"
        };

        _authServiceMock.Setup(s => s.RegisterAsync(dto)).ReturnsAsync(serviceResponse);

        var controller = CreateController();
        var result = await controller.Register(dto);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(serviceResponse, ok.Value);
    }

    [Fact]
    public async Task Login_Success_ReturnsOk()
    {
        var dto = new LoginDto { Email = "test@example.com", Password = "Pass" };
        var serviceResponse = new Response<object>
        {
            Succeeded = true,
            StatusCode = System.Net.HttpStatusCode.OK,
            Data = new { Token = "jwt-token" }
        };

        _authServiceMock.Setup(s => s.LoginAsync(dto)).ReturnsAsync(serviceResponse);

        var controller = CreateController();
        var result = await controller.Login(dto);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(serviceResponse, ok.Value);
    }

    [Fact]
    public async Task SubmitMbti_Unauthorized_WhenNoUserId()
    {
        var controller = CreateController(); // No user claim
        var result = await controller.SubmitMbti(new SubmitMbtiDto());

        Assert.IsType<UnauthorizedResult>(result);
    }

    [Fact]
    public async Task SubmitMbti_Success_ReturnsOk()
    {
        var userId = Guid.NewGuid();
        var dto = new SubmitMbtiDto();
        var serviceResponse = new Response<string>
        {
            Succeeded = true,
            StatusCode = System.Net.HttpStatusCode.OK,
            Data = "MBTI updated"
        };

        _authServiceMock.Setup(s => s.SubmitMbtiAsync(userId, dto)).ReturnsAsync(serviceResponse);

        var controller = CreateController(userId);
        var result = await controller.SubmitMbti(dto);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(serviceResponse, ok.Value);
    }

    [Fact]
    public async Task UpdateFcmToken_MissingToken_ReturnsBadRequest()
    {
        var userId = Guid.NewGuid();
        var controller = CreateController(userId);

        var result = await controller.UpdateFcmToken(null, null);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("FCM token is required.", badRequest.Value);
    }

    [Fact]
    public async Task UpdateFcmToken_Success_ReturnsOkStatus()
    {
        var userId = Guid.NewGuid();
        var serviceResponse = new Response<string>
        {
            Succeeded = true,
            StatusCode = System.Net.HttpStatusCode.OK,
            Data = "Token updated"
        };

        _authServiceMock.Setup(s => s.UpdateFcmTokenAsync(userId, "fcm-123")).ReturnsAsync(serviceResponse);

        var controller = CreateController(userId);
        var result = await controller.UpdateFcmToken("fcm-123", null);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(200, obj.StatusCode);
    }

    [Fact]
    public async Task ResendOtp_ReturnsStatusCodeResult()
    {
        var serviceResponse = new Response<string>
        {
            Succeeded = true,
            StatusCode = System.Net.HttpStatusCode.OK,
            Data = "OTP sent"
        };

        _otpServiceMock.Setup(s => s.ResendOtpAsync("test@example.com")).ReturnsAsync(serviceResponse);

        var controller = CreateController();
        var result = await controller.ResendOtp("test@example.com");

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(200, obj.StatusCode);
    }

    [Fact]
    public async Task ConfirmEmail_ReturnsStatusCodeResult()
    {
        var serviceResponse = new Response<string>
        {
            Succeeded = true,
            StatusCode = System.Net.HttpStatusCode.OK,
            Data = "Email confirmed"
        };

        _otpServiceMock.Setup(s => s.VerifyOtpAsync("test@example.com", "123456")).ReturnsAsync(serviceResponse);

        var controller = CreateController();
        var result = await controller.ConfirmEmail("test@example.com", "123456");

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(200, obj.StatusCode);
    }

    [Fact]
    public async Task ReRecordVoice_Unauthorized_WhenNoEmail()
    {
        var controller = CreateController(); // No email claim
        var fileMock = new Mock<IFormFile>();

        var result = await controller.ReRecordVoice(fileMock.Object);

        Assert.IsType<UnauthorizedResult>(result);
    }

    [Fact]
    public async Task ReRecordVoice_NoFile_ReturnsBadRequest()
    {
        var controller = CreateController(email: "user@example.com");

        var result = await controller.ReRecordVoice(null!);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("No voice file uploaded.", badRequest.Value);
    }

    [Fact]
    public async Task UpdatePassword_Success_ReturnsOk()
    {
        var userId = Guid.NewGuid();
        var dto = new UpdatePasswordDto { CurrentPassword = "Old", NewPassword = "New" };
        var serviceResponse = new Response<string>
        {
            Succeeded = true,
            StatusCode = System.Net.HttpStatusCode.OK,
            Data = "Password changed"
        };

        _authServiceMock.Setup(s => s.UpdatePasswordAsync(userId, "Old", "New")).ReturnsAsync(serviceResponse);

        var controller = CreateController(userId);
        var result = await controller.UpdatePassword(dto);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(200, obj.StatusCode);
    }

    [Fact]
    public async Task DeleteAccount_Success_ReturnsOk()
    {
        var userId = Guid.NewGuid();
        var serviceResponse = new Response<string>
        {
            Succeeded = true,
            StatusCode = System.Net.HttpStatusCode.OK,
            Data = "Account deleted"
        };

        _authServiceMock.Setup(s => s.DeleteAccountAsync(userId)).ReturnsAsync(serviceResponse);

        var controller = CreateController(userId);
        var result = await controller.DeleteAccount();

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(200, obj.StatusCode);
    }

    [Fact]
    public async Task RefreshToken_Success_ReturnsOk()
    {
        var dto = new RefreshTokenDto { RefreshToken = "refresh-token-value" };
        var serviceResponse = new Response<AuthModel>
        {
            Succeeded = true,
            StatusCode = System.Net.HttpStatusCode.OK,
            Data = new AuthModel { Token = "new-token" }
        };

        _authServiceMock.Setup(s => s.RefreshTokenAsync(dto)).ReturnsAsync(serviceResponse);

        var controller = CreateController();
        var result = await controller.RefreshToken(dto);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(serviceResponse, ok.Value);
    }

    [Fact]
    public async Task RevokeToken_Success_ReturnsOk()
    {
        var userId = Guid.NewGuid();
        var serviceResponse = new Response<string>
        {
            Succeeded = true,
            StatusCode = System.Net.HttpStatusCode.OK,
            Data = "Token revoked"
        };

        _authServiceMock.Setup(s => s.RevokeTokenAsync(userId)).ReturnsAsync(serviceResponse);

        var controller = CreateController(userId);
        var result = await controller.RevokeToken();

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(200, obj.StatusCode);
    }
}
