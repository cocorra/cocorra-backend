using System.Security.Claims;
using Cocorra.API.Controllers;
using Cocorra.BLL.Base;
using Cocorra.BLL.Services.ProfileService;
using Cocorra.DAL.DTOS.ProfileDto;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Cocorra.Tests;

public class ProfileControllerTests
{
    private readonly Mock<IProfileService> _profileServiceMock = new();

    private ProfileController CreateController(Guid? userId = null)
    {
        var controller = new ProfileController(_profileServiceMock.Object);

        if (userId.HasValue)
        {
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, userId.Value.ToString())
            };
            var identity = new ClaimsIdentity(claims, "TestAuth");
            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
            };
        }
        else
        {
            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal() }
            };
        }

        return controller;
    }

    [Fact]
    public async Task GetMyProfile_Unauthorized_WhenNoUser()
    {
        var controller = CreateController();
        var result = await controller.GetMyProfile();

        Assert.IsType<UnauthorizedResult>(result);
    }

    [Fact]
    public async Task GetMyProfile_Success_ReturnsOk()
    {
        var userId = Guid.NewGuid();
        var profileDto = new MyProfileDto { Id = userId, FirstName = "John", LastName = "Doe" };
        var serviceResponse = new Response<MyProfileDto>
        {
            Succeeded = true,
            StatusCode = System.Net.HttpStatusCode.OK,
            Data = profileDto
        };

        _profileServiceMock.Setup(s => s.GetMyProfileAsync(userId)).ReturnsAsync(serviceResponse);

        var controller = CreateController(userId);
        var result = await controller.GetMyProfile();

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(200, obj.StatusCode);
        Assert.Equal(serviceResponse, obj.Value);
    }

    [Fact]
    public async Task GetUserProfile_Success_ReturnsOk()
    {
        var currentUserId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();
        var publicProfileDto = new PublicProfileDto { UserId = targetUserId, FullName = "Jane Doe" };
        var serviceResponse = new Response<PublicProfileDto>
        {
            Succeeded = true,
            StatusCode = System.Net.HttpStatusCode.OK,
            Data = publicProfileDto
        };

        _profileServiceMock.Setup(s => s.GetUserProfileAsync(currentUserId, targetUserId)).ReturnsAsync(serviceResponse);

        var controller = CreateController(currentUserId);
        var result = await controller.GetUserProfile(targetUserId);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(200, obj.StatusCode);
        Assert.Equal(serviceResponse, obj.Value);
    }

    [Fact]
    public async Task UpdateProfile_Success_ReturnsOk()
    {
        var userId = Guid.NewGuid();
        var dto = new UpdateProfileDto { FirstName = "Updated" };
        var profileDto = new MyProfileDto { Id = userId, FirstName = "Updated" };
        var serviceResponse = new Response<MyProfileDto>
        {
            Succeeded = true,
            StatusCode = System.Net.HttpStatusCode.OK,
            Data = profileDto
        };

        _profileServiceMock.Setup(s => s.UpdateProfileAsync(userId, dto)).ReturnsAsync(serviceResponse);

        var controller = CreateController(userId);
        var result = await controller.UpdateProfile(dto);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(200, obj.StatusCode);
    }

    [Fact]
    public async Task UploadProfilePicture_NoFile_ReturnsBadRequest()
    {
        var userId = Guid.NewGuid();
        var controller = CreateController(userId);

        var result = await controller.UploadProfilePicture(null!);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("No file uploaded.", badRequest.Value);
    }

    [Fact]
    public async Task UploadProfilePicture_Success_ReturnsOk()
    {
        var userId = Guid.NewGuid();
        var fileMock = new Mock<IFormFile>();
        fileMock.Setup(f => f.Length).Returns(100);

        var serviceResponse = new Response<string>
        {
            Succeeded = true,
            StatusCode = System.Net.HttpStatusCode.OK,
            Data = "https://cdn.example.com/pic.jpg"
        };

        _profileServiceMock.Setup(s => s.UploadProfilePictureAsync(userId, fileMock.Object)).ReturnsAsync(serviceResponse);

        var controller = CreateController(userId);
        var result = await controller.UploadProfilePicture(fileMock.Object);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(200, obj.StatusCode);
    }

    [Fact]
    public async Task UpdateAvatarPreset_Success_ReturnsOk()
    {
        var userId = Guid.NewGuid();
        var dto = new UpdateAvatarPresetDto { AvatarPresetKey = "avatar_1.png" };
        var serviceResponse = new Response<string>
        {
            Succeeded = true,
            StatusCode = System.Net.HttpStatusCode.OK,
            Data = "Avatar updated"
        };

        _profileServiceMock.Setup(s => s.UpdateAvatarPresetAsync(userId, dto)).ReturnsAsync(serviceResponse);

        var controller = CreateController(userId);
        var result = await controller.UpdateAvatarPreset(dto);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(200, obj.StatusCode);
    }
}
