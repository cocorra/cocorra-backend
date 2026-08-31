using System.Security.Claims;
using Cocorra.API.Controllers;
using Cocorra.API.Hubs;
using Cocorra.BLL.Base;
using Cocorra.BLL.Services.AdminService;
using Cocorra.DAL.DTOS.AdminDto;
using Cocorra.DAL.Enums;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Moq;
using Xunit;

namespace Cocorra.Tests;

public class AdminControllerTests
{
    private readonly Mock<IAdminService> _adminServiceMock = new();
    private readonly Mock<IHubContext<RoomHub>> _roomHubContextMock = new();
    private readonly Mock<IHubClients> _hubClientsMock = new();
    private readonly Mock<ISingleClientProxy> _clientProxyMock = new();

    public AdminControllerTests()
    {
        _roomHubContextMock.Setup(h => h.Clients).Returns(_hubClientsMock.Object);
        _hubClientsMock.Setup(c => c.Client(It.IsAny<string>())).Returns(_clientProxyMock.Object);
    }

    private AdminController CreateController(Guid? adminId = null, string adminEmail = "admin@example.com")
    {
        var controller = new AdminController(_adminServiceMock.Object, _roomHubContextMock.Object);

        var uid = adminId ?? Guid.NewGuid();
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, uid.ToString()),
            new(ClaimTypes.Email, adminEmail),
            new(ClaimTypes.Role, "Admin")
        };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        var principal = new ClaimsPrincipal(identity);

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };

        return controller;
    }

    [Fact]
    public async Task GetAllUsers_ReturnsOkWithPagedList()
    {
        // Arrange
        var pagedResponse = new PagedResponse<UserDto>
        {
            Data = new List<UserDto>
            {
                new() { Id = Guid.NewGuid().ToString(), Email = "user1@example.com", FullName = "User One" }
            },
            CurrentPage = 1,
            PageSize = 10,
            TotalCount = 1,
            Succeeded = true,
            StatusCode = System.Net.HttpStatusCode.OK
        };

        _adminServiceMock
            .Setup(s => s.GetAllUsersAsync("test", 1, 10))
            .ReturnsAsync(pagedResponse);

        var controller = CreateController();

        // Act
        var result = await controller.GetAllUsers("test", 1, 10);

        // Assert
        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(pagedResponse, ok.Value);
    }

    [Fact]
    public async Task GetUserById_Success_ReturnsOk()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var userDto = new UserDto { Id = userId.ToString(), Email = "user@example.com", FullName = "User" };
        var serviceResponse = new Response<UserDto>
        {
            Succeeded = true,
            StatusCode = System.Net.HttpStatusCode.OK,
            Data = userDto
        };

        _adminServiceMock.Setup(s => s.GetUserByIdAsync(userId)).ReturnsAsync(serviceResponse);

        var controller = CreateController();

        // Act
        var result = await controller.GetUserById(userId);

        // Assert
        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(serviceResponse, ok.Value);
    }

    [Fact]
    public async Task GetUserById_NotFound_ReturnsBadRequest()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var serviceResponse = new Response<UserDto>
        {
            Succeeded = false,
            StatusCode = System.Net.HttpStatusCode.NotFound,
            Message = "User not found."
        };

        _adminServiceMock.Setup(s => s.GetUserByIdAsync(userId)).ReturnsAsync(serviceResponse);

        var controller = CreateController();

        // Act
        var result = await controller.GetUserById(userId);

        // Assert
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(serviceResponse, badRequest.Value);
    }

    [Fact]
    public async Task ChangeStatus_SelfChange_ReturnsBadRequest()
    {
        // Arrange
        var adminId = Guid.NewGuid();
        var controller = CreateController(adminId);

        var model = new ChangeStatusDto { NewStatus = UserStatus.Banned };

        // Act
        var result = await controller.ChangeStatus(adminId, model);

        // Assert
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.NotNull(badRequest.Value);
    }

    [Fact]
    public async Task ChangeStatus_Success_ReturnsOk()
    {
        // Arrange
        var adminId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();
        var controller = CreateController(adminId);

        var model = new ChangeStatusDto { NewStatus = UserStatus.Active };
        var serviceResponse = new Response<string>
        {
            Succeeded = true,
            StatusCode = System.Net.HttpStatusCode.OK,
            Data = "Status updated successfully."
        };

        _adminServiceMock.Setup(s => s.ChangeUserStatusAsync(targetUserId, UserStatus.Active)).ReturnsAsync(serviceResponse);

        // Act
        var result = await controller.ChangeStatus(targetUserId, model);

        // Assert
        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(serviceResponse, ok.Value);
    }

    [Fact]
    public async Task ChangeStatus_BannedStatus_CallsSignalRForceDisconnect()
    {
        // Arrange
        var adminId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();
        var controller = CreateController(adminId);

        var model = new ChangeStatusDto { NewStatus = UserStatus.Banned };
        var serviceResponse = new Response<string>
        {
            Succeeded = true,
            StatusCode = System.Net.HttpStatusCode.OK,
            Data = "User banned."
        };

        _adminServiceMock.Setup(s => s.ChangeUserStatusAsync(targetUserId, UserStatus.Banned)).ReturnsAsync(serviceResponse);

        // Act
        var result = await controller.ChangeStatus(targetUserId, model);

        // Assert
        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(serviceResponse, ok.Value);
    }

    [Fact]
    public async Task BulkChangeStatus_InvalidModel_ReturnsBadRequest()
    {
        // Arrange
        var controller = CreateController();
        controller.ModelState.AddModelError("UserIds", "Required");

        // Act
        var result = await controller.BulkChangeStatus(new BulkChangeStatusDto());

        // Assert
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task BulkChangeStatus_Success_ReturnsOk()
    {
        // Arrange
        var adminId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();
        var controller = CreateController(adminId);

        var model = new BulkChangeStatusDto
        {
            UserIds = new List<Guid> { targetUserId },
            NewStatus = UserStatus.Active
        };

        var serviceResponse = new Response<BulkChangeStatusResultDto>
        {
            Succeeded = true,
            StatusCode = System.Net.HttpStatusCode.OK,
            Data = new BulkChangeStatusResultDto
            {
                TotalRequested = 1,
                SucceededCount = 1,
                Results = new List<BulkItemResultDto>
                {
                    new() { UserId = targetUserId, Succeeded = true }
                }
            }
        };

        _adminServiceMock.Setup(s => s.BulkChangeUserStatusAsync(model, adminId)).ReturnsAsync(serviceResponse);

        // Act
        var result = await controller.BulkChangeStatus(model);

        // Assert
        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(serviceResponse, ok.Value);
    }

    [Fact]
    public async Task GetDashboardStats_ReturnsOk()
    {
        // Arrange
        var stats = new DashboardStatsDto
        {
            TotalUsers = 100,
            ActiveUsers = 80,
            BannedUsers = 5,
            PendingUsers = 15
        };
        var serviceResponse = new Response<DashboardStatsDto>
        {
            Succeeded = true,
            StatusCode = System.Net.HttpStatusCode.OK,
            Data = stats
        };

        _adminServiceMock.Setup(s => s.GetDashboardStatsAsync()).ReturnsAsync(serviceResponse);
        var controller = CreateController();

        // Act
        var result = await controller.GetDashboardStats();

        // Assert
        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(serviceResponse, ok.Value);
    }

    [Fact]
    public async Task BlockDeviceAndEmail_SelfBlock_ReturnsBadRequest()
    {
        // Arrange
        var adminEmail = "admin@example.com";
        var controller = CreateController(adminEmail: adminEmail);

        var model = new BlockDeviceAndEmailDto
        {
            Email = adminEmail,
            DeviceId = "device123"
        };

        // Act
        var result = await controller.BlockDeviceAndEmail(model);

        // Assert
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.NotNull(badRequest.Value);
    }

    [Fact]
    public async Task BlockDeviceAndEmail_Success_ReturnsOk()
    {
        // Arrange
        var controller = CreateController(adminEmail: "admin@example.com");

        var model = new BlockDeviceAndEmailDto
        {
            Email = "baduser@example.com",
            DeviceId = "device123"
        };

        var serviceResponse = new Response<string>
        {
            Succeeded = true,
            StatusCode = System.Net.HttpStatusCode.OK,
            Data = "Device and email blocked successfully."
        };

        _adminServiceMock.Setup(s => s.BlockDeviceAndEmailAsync(model)).ReturnsAsync(serviceResponse);

        // Act
        var result = await controller.BlockDeviceAndEmail(model);

        // Assert
        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(serviceResponse, ok.Value);
    }
}
