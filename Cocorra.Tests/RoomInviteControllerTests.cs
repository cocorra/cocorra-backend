// Owns: BE-INVITE-070, BE-INVITE-071, BE-INVITE-072, BE-INVITE-073, BE-INVITE-074, BE-INVITE-075, BE-INVITE-076, BE-INVITE-077
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Claims;
using System.Threading.Tasks;
using Cocorra.API.Controllers;
using Cocorra.BLL.Base;
using Cocorra.BLL.Services.RoomInviteService;
using Cocorra.DAL.DTOS.RoomInviteDto;
using Cocorra.DAL.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Moq;
using Xunit;

namespace Cocorra.Tests;

public class RoomInviteControllerTests
{
    private readonly Mock<IRoomInviteService> _inviteServiceMock = new();

    private RoomInviteController CreateController(Guid? userId = null, string? role = null)
    {
        var controller = new RoomInviteController(_inviteServiceMock.Object);

        if (userId.HasValue)
        {
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, userId.Value.ToString())
            };
            if (!string.IsNullOrEmpty(role))
            {
                claims.Add(new(ClaimTypes.Role, role));
            }
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
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) }
            };
        }

        return controller;
    }

    private RoomInviteController CreateControllerWithInvalidClaim(string invalidClaimValue)
    {
        var controller = new RoomInviteController(_inviteServiceMock.Object);
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, invalidClaimValue)
        };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
        return controller;
    }

    // =========================================================================
    // ATTRIBUTE REFLECTION TESTS
    // =========================================================================

    [Fact]
    public void Controller_HasApiControllerAndAuthorizeAttributes()
    {
        var controllerType = typeof(RoomInviteController);

        var apiControllerAttributes = controllerType.GetCustomAttributes(typeof(ApiControllerAttribute), inherit: true);
        Assert.NotEmpty(apiControllerAttributes);

        var authorizeAttributes = controllerType.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true);
        Assert.NotEmpty(authorizeAttributes);
    }

    [Fact]
    public void Resolve_HasAllowAnonymousAndRateLimitingInvitesAttributes()
    {
        var method = typeof(RoomInviteController).GetMethod(nameof(RoomInviteController.Resolve));
        Assert.NotNull(method);

        var allowAnonymous = method!.GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: false)
            .OfType<AllowAnonymousAttribute>()
            .FirstOrDefault();
        Assert.NotNull(allowAnonymous);

        var rateLimiting = method.GetCustomAttributes(typeof(EnableRateLimitingAttribute), inherit: false)
            .OfType<EnableRateLimitingAttribute>()
            .FirstOrDefault();
        Assert.NotNull(rateLimiting);
        Assert.Equal("invites", rateLimiting!.PolicyName);
    }

    [Fact]
    public void Accept_HasRateLimitingInvitesAndNoAllowAnonymousAttribute()
    {
        var method = typeof(RoomInviteController).GetMethod(nameof(RoomInviteController.Accept));
        Assert.NotNull(method);

        var allowAnonymous = method!.GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: false)
            .OfType<AllowAnonymousAttribute>()
            .FirstOrDefault();
        Assert.Null(allowAnonymous);

        var rateLimiting = method.GetCustomAttributes(typeof(EnableRateLimitingAttribute), inherit: false)
            .OfType<EnableRateLimitingAttribute>()
            .FirstOrDefault();
        Assert.NotNull(rateLimiting);
        Assert.Equal("invites", rateLimiting!.PolicyName);
    }

    [Fact]
    public void GetStats_HasAuthorizeAttributeWithAdminRole()
    {
        var method = typeof(RoomInviteController).GetMethod(nameof(RoomInviteController.GetStats));
        Assert.NotNull(method);

        var authorizeAttribute = method!.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .OfType<AuthorizeAttribute>()
            .FirstOrDefault();

        Assert.NotNull(authorizeAttribute);
        Assert.Equal("Admin", authorizeAttribute!.Roles);
    }

    // =========================================================================
    // AUTHENTICATION / CLAIM TESTS
    // =========================================================================

    [Fact]
    public async Task Create_MissingNameIdentifierClaim_ReturnsUnauthorized()
    {
        var controller = CreateController();
        var result = await controller.Create(Guid.NewGuid(), new CreateRoomInviteDto());

        Assert.IsType<UnauthorizedResult>(result);
        _inviteServiceMock.Verify(s => s.CreateInviteAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CreateRoomInviteDto?>()), Times.Never);
    }

    [Fact]
    public async Task Create_InvalidNameIdentifierClaim_ReturnsUnauthorized()
    {
        var controller = CreateControllerWithInvalidClaim("not-a-valid-guid");
        var result = await controller.Create(Guid.NewGuid(), new CreateRoomInviteDto());

        Assert.IsType<UnauthorizedResult>(result);
        _inviteServiceMock.Verify(s => s.CreateInviteAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CreateRoomInviteDto?>()), Times.Never);
    }

    [Fact]
    public async Task Accept_MissingNameIdentifierClaim_ReturnsUnauthorized()
    {
        var controller = CreateController();
        var result = await controller.Accept("sample_invite_code_123");

        Assert.IsType<UnauthorizedResult>(result);
        _inviteServiceMock.Verify(s => s.AcceptInviteAsync(
            It.IsAny<string>(), It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task Accept_InvalidNameIdentifierClaim_ReturnsUnauthorized()
    {
        var controller = CreateControllerWithInvalidClaim("not-a-valid-guid");
        var result = await controller.Accept("sample_invite_code_123");

        Assert.IsType<UnauthorizedResult>(result);
        _inviteServiceMock.Verify(s => s.AcceptInviteAsync(
            It.IsAny<string>(), It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task Revoke_MissingNameIdentifierClaim_ReturnsUnauthorized()
    {
        var controller = CreateController();
        var result = await controller.Revoke("sample_invite_code_123");

        Assert.IsType<UnauthorizedResult>(result);
        _inviteServiceMock.Verify(s => s.RevokeInviteAsync(
            It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task Revoke_InvalidNameIdentifierClaim_ReturnsUnauthorized()
    {
        var controller = CreateControllerWithInvalidClaim("not-a-valid-guid");
        var result = await controller.Revoke("sample_invite_code_123");

        Assert.IsType<UnauthorizedResult>(result);
        _inviteServiceMock.Verify(s => s.RevokeInviteAsync(
            It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<bool>()), Times.Never);
    }

    // =========================================================================
    // MODEL STATE VALIDATION
    // =========================================================================

    [Fact]
    public async Task Create_InvalidModelState_ReturnsBadRequestObjectResultAndNeverCallsService()
    {
        var userId = Guid.NewGuid();
        var roomId = Guid.NewGuid();
        var controller = CreateController(userId);
        controller.ModelState.AddModelError("ExpiresInHours", "ExpiresInHours must be between 1 and 168.");

        var dto = new CreateRoomInviteDto { ExpiresInHours = 500 };
        var result = await controller.Create(roomId, dto);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var errors = Assert.IsType<SerializableError>(badRequest.Value);
        var fieldErrors = Assert.IsType<string[]>(errors["ExpiresInHours"]);
        Assert.Contains("ExpiresInHours must be between 1 and 168.", fieldErrors);

        _inviteServiceMock.Verify(s => s.CreateInviteAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CreateRoomInviteDto?>()), Times.Never);
    }

    // =========================================================================
    // ROLE HANDLING (ADMIN FLAG PROPAGATION)
    // =========================================================================

    [Fact]
    public async Task Revoke_PrincipalWithAdminRole_PassesIsAdminTrueToService()
    {
        var userId = Guid.NewGuid();
        var code = "invite_code_sample_123";
        var controller = CreateController(userId, role: "Admin");

        var serviceResponse = new Response<ResolveRoomInviteDto>
        {
            StatusCode = HttpStatusCode.OK,
            Succeeded = true,
            Data = new ResolveRoomInviteDto { InviteCode = code, Status = RoomInviteStatus.Revoked }
        };

        _inviteServiceMock.Setup(s => s.RevokeInviteAsync(code, userId, true))
            .ReturnsAsync(serviceResponse);

        var result = await controller.Revoke(code);

        _inviteServiceMock.Verify(s => s.RevokeInviteAsync(code, userId, true), Times.Once);
        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(200, obj.StatusCode);
        Assert.Equal(serviceResponse, obj.Value);
    }

    [Fact]
    public async Task Revoke_PrincipalWithoutAdminRole_PassesIsAdminFalseToService()
    {
        var userId = Guid.NewGuid();
        var code = "invite_code_sample_123";
        var controller = CreateController(userId, role: "User");

        var serviceResponse = new Response<ResolveRoomInviteDto>
        {
            StatusCode = HttpStatusCode.OK,
            Succeeded = true,
            Data = new ResolveRoomInviteDto { InviteCode = code, Status = RoomInviteStatus.Revoked }
        };

        _inviteServiceMock.Setup(s => s.RevokeInviteAsync(code, userId, false))
            .ReturnsAsync(serviceResponse);

        var result = await controller.Revoke(code);

        _inviteServiceMock.Verify(s => s.RevokeInviteAsync(code, userId, false), Times.Once);
        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(200, obj.StatusCode);
        Assert.Equal(serviceResponse, obj.Value);
    }

    // =========================================================================
    // STATUS CODE PROPAGATION TESTS
    // =========================================================================

    [Theory]
    [InlineData(HttpStatusCode.OK, 200)]
    [InlineData(HttpStatusCode.BadRequest, 400)]
    [InlineData(HttpStatusCode.Forbidden, 403)]
    [InlineData(HttpStatusCode.NotFound, 404)]
    [InlineData(HttpStatusCode.Conflict, 409)]
    [InlineData(HttpStatusCode.Gone, 410)]
    public async Task Create_ServiceStatusCode_PropagatedToObjectResultStatusCode(
        HttpStatusCode serviceStatus, int expectedHttpCode)
    {
        var userId = Guid.NewGuid();
        var roomId = Guid.NewGuid();
        var controller = CreateController(userId);
        var dto = new CreateRoomInviteDto();

        var serviceResponse = new Response<CreateRoomInviteResultDto>
        {
            StatusCode = serviceStatus,
            Succeeded = expectedHttpCode == 200,
            Message = "Create status propagation test"
        };

        _inviteServiceMock.Setup(s => s.CreateInviteAsync(roomId, userId, dto))
            .ReturnsAsync(serviceResponse);

        var result = await controller.Create(roomId, dto);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(expectedHttpCode, obj.StatusCode);
        Assert.Equal(serviceResponse, obj.Value);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, 200)]
    [InlineData(HttpStatusCode.NotFound, 404)]
    [InlineData(HttpStatusCode.Gone, 410)]
    public async Task Resolve_ServiceStatusCode_PropagatedToObjectResultStatusCode(
        HttpStatusCode serviceStatus, int expectedHttpCode)
    {
        var controller = CreateController();
        var code = "resolve_code_sample_12";

        var serviceResponse = new Response<ResolveRoomInviteDto>
        {
            StatusCode = serviceStatus,
            Succeeded = expectedHttpCode == 200,
            Message = "Resolve status propagation test"
        };

        _inviteServiceMock.Setup(s => s.ResolveInviteAsync(code))
            .ReturnsAsync(serviceResponse);

        var result = await controller.Resolve(code);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(expectedHttpCode, obj.StatusCode);
        Assert.Equal(serviceResponse, obj.Value);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, 200)]
    [InlineData(HttpStatusCode.BadRequest, 400)]
    [InlineData(HttpStatusCode.NotFound, 404)]
    [InlineData(HttpStatusCode.Conflict, 409)]
    [InlineData(HttpStatusCode.Gone, 410)]
    public async Task Accept_ServiceStatusCode_PropagatedToObjectResultStatusCode(
        HttpStatusCode serviceStatus, int expectedHttpCode)
    {
        var userId = Guid.NewGuid();
        var controller = CreateController(userId);
        var code = "accept_code_sample_123";

        var serviceResponse = new Response<object>
        {
            StatusCode = serviceStatus,
            Succeeded = expectedHttpCode == 200,
            Message = "Accept status propagation test"
        };

        _inviteServiceMock.Setup(s => s.AcceptInviteAsync(code, userId))
            .ReturnsAsync(serviceResponse);

        var result = await controller.Accept(code);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(expectedHttpCode, obj.StatusCode);
        Assert.Equal(serviceResponse, obj.Value);
    }

    [Fact]
    public async Task GetStats_ServiceStatusCode_PropagatedToObjectResultStatusCode()
    {
        var controller = CreateController(Guid.NewGuid(), role: "Admin");
        var from = DateTime.UtcNow.AddDays(-1);
        var to = DateTime.UtcNow;
        var roomId = Guid.NewGuid();
        var inviterId = Guid.NewGuid();

        var serviceResponse = new Response<RoomInviteStatsDto>
        {
            StatusCode = HttpStatusCode.OK,
            Succeeded = true,
            Data = new RoomInviteStatsDto { TotalCreated = 10, Active = 5 }
        };

        _inviteServiceMock.Setup(s => s.GetStatsAsync(from, to, roomId, inviterId))
            .ReturnsAsync(serviceResponse);

        var result = await controller.GetStats(from, to, roomId, inviterId);

        _inviteServiceMock.Verify(s => s.GetStatsAsync(from, to, roomId, inviterId), Times.Once);
        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(200, obj.StatusCode);
        Assert.Equal(serviceResponse, obj.Value);
    }
}
