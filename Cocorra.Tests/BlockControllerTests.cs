using System.Security.Claims;
using Cocorra.API.Controllers;
using Cocorra.BLL.Base;
using Cocorra.BLL.Services.BlockService;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Cocorra.Tests;

public class BlockControllerTests
{
    private readonly Mock<IBlockService> _blockServiceMock = new();

    private BlockController CreateController(Guid? userId = null)
    {
        var controller = new BlockController(_blockServiceMock.Object);

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
    public async Task BlockUser_Unauthorized_WhenNotLoggedIn()
    {
        var controller = CreateController();
        var result = await controller.BlockUser("target-user");

        Assert.IsType<UnauthorizedResult>(result);
    }

    [Fact]
    public async Task BlockUser_Success_ReturnsOk()
    {
        var userId = Guid.NewGuid();
        var serviceResponse = new Response<string>
        {
            Succeeded = true,
            StatusCode = System.Net.HttpStatusCode.OK,
            Data = "User blocked"
        };

        _blockServiceMock.Setup(s => s.BlockUserAsync(userId, "target-user")).ReturnsAsync(serviceResponse);

        var controller = CreateController(userId);
        var result = await controller.BlockUser("target-user");

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(serviceResponse, ok.Value);
    }

    [Fact]
    public async Task BlockUser_Failure_ReturnsBadRequest()
    {
        var userId = Guid.NewGuid();
        var serviceResponse = new Response<string>
        {
            Succeeded = false,
            StatusCode = System.Net.HttpStatusCode.BadRequest,
            Message = "User already blocked"
        };

        _blockServiceMock.Setup(s => s.BlockUserAsync(userId, "target-user")).ReturnsAsync(serviceResponse);

        var controller = CreateController(userId);
        var result = await controller.BlockUser("target-user");

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(serviceResponse, badRequest.Value);
    }

    [Fact]
    public async Task UnblockUser_Unauthorized_WhenNotLoggedIn()
    {
        var controller = CreateController();
        var result = await controller.UnblockUser("target-user");

        Assert.IsType<UnauthorizedResult>(result);
    }

    [Fact]
    public async Task UnblockUser_Success_ReturnsOk()
    {
        var userId = Guid.NewGuid();
        var serviceResponse = new Response<string>
        {
            Succeeded = true,
            StatusCode = System.Net.HttpStatusCode.OK,
            Data = "User unblocked"
        };

        _blockServiceMock.Setup(s => s.UnblockUserAsync(userId, "target-user")).ReturnsAsync(serviceResponse);

        var controller = CreateController(userId);
        var result = await controller.UnblockUser("target-user");

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(serviceResponse, ok.Value);
    }
}
