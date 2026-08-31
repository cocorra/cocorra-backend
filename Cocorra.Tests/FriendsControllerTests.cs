using System.Security.Claims;
using Cocorra.API.Controllers;
using Cocorra.BLL.Base;
using Cocorra.BLL.Services.FriendService;
using Cocorra.DAL.DTOS.FriendDto;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Cocorra.Tests;

public class FriendsControllerTests
{
    private readonly Mock<IFriendService> _friendServiceMock = new();

    private FriendsController CreateController(Guid? userId = null)
    {
        var controller = new FriendsController(_friendServiceMock.Object);

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
    public async Task SearchUser_Unauthorized_WhenNoUser()
    {
        var controller = CreateController();
        var result = await controller.SearchUser(Guid.NewGuid());

        Assert.IsType<UnauthorizedResult>(result);
    }

    [Fact]
    public async Task SearchUser_Success_ReturnsOk()
    {
        var currentUserId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();
        var searchDto = new UserSearchDto { Id = targetUserId, FullName = "Target User" };
        var serviceResponse = new Response<UserSearchDto>
        {
            Succeeded = true,
            StatusCode = System.Net.HttpStatusCode.OK,
            Data = searchDto
        };

        _friendServiceMock.Setup(s => s.SearchUserByIdAsync(currentUserId, targetUserId)).ReturnsAsync(serviceResponse);

        var controller = CreateController(currentUserId);
        var result = await controller.SearchUser(targetUserId);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(serviceResponse, ok.Value);
    }

    [Fact]
    public async Task SendRequest_Success_ReturnsOk()
    {
        var currentUserId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();
        var serviceResponse = new Response<string>
        {
            Succeeded = true,
            StatusCode = System.Net.HttpStatusCode.OK,
            Data = "Request sent"
        };

        _friendServiceMock.Setup(s => s.SendFriendRequestAsync(currentUserId, targetUserId)).ReturnsAsync(serviceResponse);

        var controller = CreateController(currentUserId);
        var result = await controller.SendRequest(new SendRequestDto { TargetUserId = targetUserId });

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(serviceResponse, ok.Value);
    }

    [Fact]
    public async Task RespondRequest_Success_ReturnsStatusCodeResult()
    {
        var currentUserId = Guid.NewGuid();
        var senderId = Guid.NewGuid();
        var serviceResponse = new Response<string>
        {
            Succeeded = true,
            StatusCode = System.Net.HttpStatusCode.OK,
            Data = "Request accepted"
        };

        _friendServiceMock.Setup(s => s.RespondToFriendRequestAsync(currentUserId, senderId, true)).ReturnsAsync(serviceResponse);

        var controller = CreateController(currentUserId);
        var result = await controller.RespondRequest(senderId, true);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(200, obj.StatusCode);
    }

    [Fact]
    public async Task RemoveFriendOrCancelRequest_Success_ReturnsStatusCodeResult()
    {
        var currentUserId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var serviceResponse = new Response<string>
        {
            Succeeded = true,
            StatusCode = System.Net.HttpStatusCode.OK,
            Data = "Friend removed"
        };

        _friendServiceMock.Setup(s => s.RemoveFriendOrCancelRequestAsync(currentUserId, targetId)).ReturnsAsync(serviceResponse);

        var controller = CreateController(currentUserId);
        var result = await controller.RemoveFriendOrCancelRequest(targetId);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(200, obj.StatusCode);
    }
}
