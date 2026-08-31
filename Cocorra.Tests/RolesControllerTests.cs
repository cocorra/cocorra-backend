using Cocorra.API.Controllers;
using Cocorra.BLL.Base;
using Cocorra.BLL.Services.RolesService;
using Cocorra.DAL.DTOS.AdminDto;
using Cocorra.DAL.DTOS.Role;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Cocorra.Tests;

public class RolesControllerTests
{
    private readonly Mock<IRolesService> _rolesServiceMock = new();

    private RolesController CreateController()
    {
        return new RolesController(_rolesServiceMock.Object);
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
    public async Task ManageUserRoles_Success_ReturnsOk()
    {
        var dto = new ManageUserRolesDto { UserId = Guid.NewGuid() };
        var serviceResponse = new Response<string>
        {
            Succeeded = true,
            StatusCode = System.Net.HttpStatusCode.OK,
            Data = "Roles updated"
        };

        _rolesServiceMock.Setup(s => s.ManageUserRolesAsync(dto)).ReturnsAsync(serviceResponse);

        var controller = CreateController();
        var result = await controller.ManageUserRoles(dto);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(serviceResponse, ok.Value);
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
}
