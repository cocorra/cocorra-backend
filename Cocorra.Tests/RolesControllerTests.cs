using System.Security.Claims;
using Cocorra.API.Controllers;
using Cocorra.API.Hubs;
using Cocorra.BLL.Base;
using Cocorra.BLL.Services.RolesService;
using Cocorra.DAL.DTOS.AdminDto;
using Cocorra.DAL.DTOS.Role;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Cocorra.Tests;

public class RolesControllerTests
{
    private readonly Mock<IRolesService> _rolesServiceMock = new();
    private readonly Mock<IStaffConnectionRevoker> _revokerMock = new();

    private RolesController CreateController(Guid? adminId = null)
    {
        var claims = adminId.HasValue
            ? new[] { new Claim(ClaimTypes.NameIdentifier, adminId.Value.ToString()) }
            : new[] { new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()) };
        var admin = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));

        return new RolesController(_rolesServiceMock.Object, _revokerMock.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = admin } }
        };
    }

    [Fact]
    public async Task GetRoles_Success_ReturnsOk()
    {
        var roles = new List<RoleDto>
        {
            new() { Id = "1", Name = "Admin" }
        };
        var serviceResponse = new Response<List<RoleDto>>
        {
            Succeeded = true,
            StatusCode = System.Net.HttpStatusCode.OK,
            Data = roles
        };

        _rolesServiceMock.Setup(s => s.GetRolesAsync()).ReturnsAsync(serviceResponse);

        var controller = CreateController();
        var result = await controller.GetRoles();

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(serviceResponse, ok.Value);
    }

    [Fact]
    public async Task GetRoleById_NotFound_ReturnsBadRequest()
    {
        var serviceResponse = new Response<RoleDto>
        {
            Succeeded = false,
            StatusCode = System.Net.HttpStatusCode.NotFound,
            Message = "Role not found"
        };

        _rolesServiceMock.Setup(s => s.GetRoleByIdAsync("999")).ReturnsAsync(serviceResponse);

        var controller = CreateController();
        var result = await controller.GetRoleById("999");

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(serviceResponse, badRequest.Value);
    }

    [Fact]
    public async Task ManageUserRoles_Success_ReturnsOkAndInvokesRevoker()
    {
        var dto = new ManageUserRolesDto { UserId = Guid.NewGuid(), Roles = new List<string> { "Moderator" } };
        var serviceResponse = new Response<string>
        {
            Succeeded = true,
            StatusCode = System.Net.HttpStatusCode.OK,
            Data = "Roles updated"
        };

        _rolesServiceMock.Setup(s => s.ManageUserRolesAsync(dto, It.IsAny<Guid>())).ReturnsAsync(serviceResponse);

        var controller = CreateController();
        var result = await controller.ManageUserRoles(dto);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(serviceResponse, ok.Value);
        _revokerMock.Verify(r => r.RevokeAsync(dto.UserId), Times.Once);
    }

    [Fact]
    public async Task ManageUserRoles_Failure_ReturnsBadRequestWithoutRevoking()
    {
        var dto = new ManageUserRolesDto { UserId = Guid.NewGuid(), Roles = new List<string> { "Admin" } };
        var serviceResponse = new Response<string>
        {
            Succeeded = false,
            StatusCode = System.Net.HttpStatusCode.BadRequest,
            Message = "Cannot assign the Admin role through this endpoint."
        };

        _rolesServiceMock.Setup(s => s.ManageUserRolesAsync(dto, It.IsAny<Guid>())).ReturnsAsync(serviceResponse);

        var controller = CreateController();
        var result = await controller.ManageUserRoles(dto);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(serviceResponse, badRequest.Value);
        _revokerMock.Verify(r => r.RevokeAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task ManageUserRoles_InvalidModelState_ReturnsBadRequestWithoutRevoking()
    {
        var controller = CreateController();
        controller.ModelState.AddModelError("Roles", "Required");

        var result = await controller.ManageUserRoles(new ManageUserRolesDto { UserId = Guid.NewGuid() });

        Assert.IsType<BadRequestObjectResult>(result);
        _rolesServiceMock.Verify(s => s.ManageUserRolesAsync(It.IsAny<ManageUserRolesDto>(), It.IsAny<Guid>()), Times.Never);
        _revokerMock.Verify(r => r.RevokeAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task GetUsersInRole_Success_ReturnsOk()
    {
        var users = new List<UserDto>
        {
            new() { Id = Guid.NewGuid().ToString(), Email = "admin@example.com" }
        };
        var serviceResponse = new Response<List<UserDto>>
        {
            Succeeded = true,
            StatusCode = System.Net.HttpStatusCode.OK,
            Data = users
        };

        _rolesServiceMock.Setup(s => s.GetUsersInRoleAsync("Admin")).ReturnsAsync(serviceResponse);

        var controller = CreateController();
        var result = await controller.GetUsersInRole("Admin");

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(serviceResponse, ok.Value);
    }

    [Fact]
    public async Task CreateUser_Success_ReturnsOkWithoutRevoking()
    {
        var dto = new CreateUserDto
        {
            FirstName = "New",
            LastName = "Coach",
            Email = "coach@example.com",
            Password = "Password123!",
            Age = 30,
            Role = "Coach"
        };
        var serviceResponse = new Response<UserDto>
        {
            Succeeded = true,
            StatusCode = System.Net.HttpStatusCode.OK,
            Data = new UserDto { Id = Guid.NewGuid().ToString(), Email = "coach@example.com", FullName = "New Coach", Roles = new List<string> { "Coach" } }
        };

        _rolesServiceMock.Setup(s => s.CreateUserWithRoleAsync(dto, It.IsAny<Guid>())).ReturnsAsync(serviceResponse);

        var controller = CreateController();
        var result = await controller.CreateUser(dto);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(serviceResponse, ok.Value);
        _revokerMock.Verify(r => r.RevokeAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task CreateUser_Failure_ReturnsBadRequestWithoutRevoking()
    {
        var dto = new CreateUserDto { Email = "fail@example.com" };
        var serviceResponse = new Response<UserDto>
        {
            Succeeded = false,
            StatusCode = System.Net.HttpStatusCode.BadRequest,
            Message = "Email already registered"
        };

        _rolesServiceMock.Setup(s => s.CreateUserWithRoleAsync(dto, It.IsAny<Guid>())).ReturnsAsync(serviceResponse);

        var controller = CreateController();
        var result = await controller.CreateUser(dto);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(serviceResponse, badRequest.Value);
        _revokerMock.Verify(r => r.RevokeAsync(It.IsAny<Guid>()), Times.Never);
    }
}
