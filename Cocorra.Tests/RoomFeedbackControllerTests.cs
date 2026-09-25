using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Claims;
using System.Threading.Tasks;
using Cocorra.API.Controllers;
using Cocorra.BLL.Base;
using Cocorra.BLL.Services.RoomFeedbackService;
using Cocorra.DAL.DTOS.RoomFeedbackDto;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Cocorra.Tests;

public class RoomFeedbackControllerTests
{
    private readonly Mock<IRoomFeedbackService> _feedbackServiceMock = new();

    private RoomFeedbackController CreateController(Guid? userId = null, string? role = null)
    {
        var controller = new RoomFeedbackController(_feedbackServiceMock.Object);

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

    private RoomFeedbackController CreateControllerWithInvalidClaim(string invalidClaimValue)
    {
        var controller = new RoomFeedbackController(_feedbackServiceMock.Object);
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

    [Fact]
    public async Task Submit_MissingNameIdentifierClaim_ReturnsUnauthorized()
    {
        var controller = CreateController();
        var result = await controller.Submit(Guid.NewGuid(), new SubmitRoomFeedbackDto { Rating = 5 });

        Assert.IsType<UnauthorizedResult>(result);
        _feedbackServiceMock.Verify(s => s.SubmitFeedbackAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<SubmitRoomFeedbackDto>()), Times.Never);
    }

    [Fact]
    public async Task Submit_InvalidNameIdentifierClaim_ReturnsUnauthorized()
    {
        var controller = CreateControllerWithInvalidClaim("not-a-valid-guid");
        var result = await controller.Submit(Guid.NewGuid(), new SubmitRoomFeedbackDto { Rating = 5 });

        Assert.IsType<UnauthorizedResult>(result);
        _feedbackServiceMock.Verify(s => s.SubmitFeedbackAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<SubmitRoomFeedbackDto>()), Times.Never);
    }

    [Fact]
    public async Task GetMine_MissingNameIdentifierClaim_ReturnsUnauthorized()
    {
        var controller = CreateController();
        var result = await controller.GetMine(Guid.NewGuid());

        Assert.IsType<UnauthorizedResult>(result);
        _feedbackServiceMock.Verify(s => s.GetMyFeedbackStatusAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task GetMine_InvalidNameIdentifierClaim_ReturnsUnauthorized()
    {
        var controller = CreateControllerWithInvalidClaim("not-a-valid-guid");
        var result = await controller.GetMine(Guid.NewGuid());

        Assert.IsType<UnauthorizedResult>(result);
        _feedbackServiceMock.Verify(s => s.GetMyFeedbackStatusAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task GetSummary_MissingNameIdentifierClaim_ReturnsUnauthorized()
    {
        var controller = CreateController();
        var result = await controller.GetSummary(Guid.NewGuid());

        Assert.IsType<UnauthorizedResult>(result);
        _feedbackServiceMock.Verify(s => s.GetSummaryAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task GetSummary_InvalidNameIdentifierClaim_ReturnsUnauthorized()
    {
        var controller = CreateControllerWithInvalidClaim("not-a-valid-guid");
        var result = await controller.GetSummary(Guid.NewGuid());

        Assert.IsType<UnauthorizedResult>(result);
        _feedbackServiceMock.Verify(s => s.GetSummaryAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task Submit_InvalidModelState_ReturnsBadRequestObjectResultAndNeverCallsService()
    {
        var userId = Guid.NewGuid();
        var roomId = Guid.NewGuid();
        var controller = CreateController(userId);
        controller.ModelState.AddModelError("Rating", "Rating must be between 1 and 5.");

        var dto = new SubmitRoomFeedbackDto { Rating = 0 };
        var result = await controller.Submit(roomId, dto);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        // BadRequest(ModelState) wraps the dictionary in a SerializableError.
        var errors = Assert.IsType<SerializableError>(badRequest.Value);
        var ratingErrors = Assert.IsType<string[]>(errors["Rating"]);
        Assert.Contains("Rating must be between 1 and 5.", ratingErrors);
        _feedbackServiceMock.Verify(s => s.SubmitFeedbackAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<SubmitRoomFeedbackDto>()), Times.Never);
    }

    [Fact]
    public async Task GetSummary_PrincipalWithAdminRole_PassesIsAdminTrueToService()
    {
        var userId = Guid.NewGuid();
        var roomId = Guid.NewGuid();
        var controller = CreateController(userId, role: "Admin");

        var serviceResponse = new Response<RoomFeedbackSummaryDto>
        {
            StatusCode = HttpStatusCode.OK,
            Succeeded = true,
            Data = new RoomFeedbackSummaryDto { RoomId = roomId, Count = 1, AverageRating = 5 }
        };

        _feedbackServiceMock.Setup(s => s.GetSummaryAsync(roomId, userId, true))
            .ReturnsAsync(serviceResponse);

        var result = await controller.GetSummary(roomId);

        _feedbackServiceMock.Verify(s => s.GetSummaryAsync(roomId, userId, true), Times.Once);
        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(200, obj.StatusCode);
        Assert.Equal(serviceResponse, obj.Value);
    }

    [Fact]
    public async Task GetSummary_PrincipalWithoutAdminRole_PassesIsAdminFalseToService()
    {
        var userId = Guid.NewGuid();
        var roomId = Guid.NewGuid();
        var controller = CreateController(userId, role: "User");

        var serviceResponse = new Response<RoomFeedbackSummaryDto>
        {
            StatusCode = HttpStatusCode.OK,
            Succeeded = true,
            Data = new RoomFeedbackSummaryDto { RoomId = roomId, Count = 0, AverageRating = 0 }
        };

        _feedbackServiceMock.Setup(s => s.GetSummaryAsync(roomId, userId, false))
            .ReturnsAsync(serviceResponse);

        var result = await controller.GetSummary(roomId);

        _feedbackServiceMock.Verify(s => s.GetSummaryAsync(roomId, userId, false), Times.Once);
        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(200, obj.StatusCode);
        Assert.Equal(serviceResponse, obj.Value);
    }

    [Fact]
    public async Task GetAll_ClampsPageSize500To50_AndPageNumber0To1()
    {
        var controller = CreateController(Guid.NewGuid(), role: "Admin");
        var roomId = Guid.NewGuid();

        var serviceResponse = new PagedResponse<AdminRoomFeedbackDto>
        {
            StatusCode = HttpStatusCode.OK,
            Succeeded = true,
            Data = new List<AdminRoomFeedbackDto>()
        };

        _feedbackServiceMock.Setup(s => s.GetAdminFeedbackAsync(roomId, 5, 1, 50))
            .ReturnsAsync(serviceResponse);

        var result = await controller.GetAll(roomId, 5, pageNumber: 0, pageSize: 500);

        _feedbackServiceMock.Verify(s => s.GetAdminFeedbackAsync(roomId, 5, 1, 50), Times.Once);
        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(200, obj.StatusCode);
    }

    [Fact]
    public async Task GetAll_ClampsNegativeValuesToMinimumAllowed()
    {
        var controller = CreateController(Guid.NewGuid(), role: "Admin");

        var serviceResponse = new PagedResponse<AdminRoomFeedbackDto>
        {
            StatusCode = HttpStatusCode.OK,
            Succeeded = true,
            Data = new List<AdminRoomFeedbackDto>()
        };

        _feedbackServiceMock.Setup(s => s.GetAdminFeedbackAsync(null, null, 1, 1))
            .ReturnsAsync(serviceResponse);

        var result = await controller.GetAll(null, null, pageNumber: -10, pageSize: -5);

        _feedbackServiceMock.Verify(s => s.GetAdminFeedbackAsync(null, null, 1, 1), Times.Once);
        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(200, obj.StatusCode);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, 200)]
    [InlineData(HttpStatusCode.BadRequest, 400)]
    [InlineData(HttpStatusCode.Forbidden, 403)]
    [InlineData(HttpStatusCode.NotFound, 404)]
    public async Task Submit_ServiceStatusCode_PropagatedToObjectResultStatusCode(
        HttpStatusCode serviceStatus, int expectedHttpCode)
    {
        var userId = Guid.NewGuid();
        var roomId = Guid.NewGuid();
        var controller = CreateController(userId);
        var dto = new SubmitRoomFeedbackDto { Rating = 5 };

        var serviceResponse = new Response<RoomFeedbackDto>
        {
            StatusCode = serviceStatus,
            Succeeded = expectedHttpCode == 200,
            Message = "Status propagation test"
        };

        _feedbackServiceMock.Setup(s => s.SubmitFeedbackAsync(roomId, userId, dto))
            .ReturnsAsync(serviceResponse);

        var result = await controller.Submit(roomId, dto);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(expectedHttpCode, obj.StatusCode);
        Assert.Equal(serviceResponse, obj.Value);
    }

    [Fact]
    public async Task GetMine_ServiceStatusCode_PropagatedToObjectResultStatusCode()
    {
        var userId = Guid.NewGuid();
        var roomId = Guid.NewGuid();
        var controller = CreateController(userId);

        var serviceResponse = new Response<MyRoomFeedbackStatusDto>
        {
            StatusCode = HttpStatusCode.NotFound,
            Succeeded = false,
            Message = "Room not found."
        };

        _feedbackServiceMock.Setup(s => s.GetMyFeedbackStatusAsync(roomId, userId))
            .ReturnsAsync(serviceResponse);

        var result = await controller.GetMine(roomId);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(404, obj.StatusCode);
        Assert.Equal(serviceResponse, obj.Value);
    }

    [Fact]
    public async Task GetSummary_ServiceStatusCode_PropagatedToObjectResultStatusCode()
    {
        var userId = Guid.NewGuid();
        var roomId = Guid.NewGuid();
        var controller = CreateController(userId);

        var serviceResponse = new Response<RoomFeedbackSummaryDto>
        {
            StatusCode = HttpStatusCode.Forbidden,
            Succeeded = false,
            Message = "Only the room host or an admin can view feedback."
        };

        _feedbackServiceMock.Setup(s => s.GetSummaryAsync(roomId, userId, false))
            .ReturnsAsync(serviceResponse);

        var result = await controller.GetSummary(roomId);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(403, obj.StatusCode);
        Assert.Equal(serviceResponse, obj.Value);
    }

    [Fact]
    public async Task GetAll_ServiceStatusCode_PropagatedToObjectResultStatusCode()
    {
        var controller = CreateController(Guid.NewGuid(), role: "Admin");

        var serviceResponse = new PagedResponse<AdminRoomFeedbackDto>
        {
            StatusCode = HttpStatusCode.OK,
            Succeeded = true,
            Data = new List<AdminRoomFeedbackDto>()
        };

        _feedbackServiceMock.Setup(s => s.GetAdminFeedbackAsync(null, null, 1, 10))
            .ReturnsAsync(serviceResponse);

        var result = await controller.GetAll(null, null, 1, 10);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(200, obj.StatusCode);
        Assert.Equal(serviceResponse, obj.Value);
    }

    [Fact]
    public void Controller_HasAuthorizeAndApiControllerAttributes()
    {
        var controllerType = typeof(RoomFeedbackController);

        var authorizeAttributes = controllerType.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true);
        Assert.NotEmpty(authorizeAttributes);

        var apiControllerAttributes = controllerType.GetCustomAttributes(typeof(ApiControllerAttribute), inherit: true);
        Assert.NotEmpty(apiControllerAttributes);
    }

    [Fact]
    public void GetAll_HasAuthorizeAttributeWithAdminRole()
    {
        var method = typeof(RoomFeedbackController).GetMethod(nameof(RoomFeedbackController.GetAll));
        Assert.NotNull(method);

        var authorizeAttribute = method!
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .OfType<AuthorizeAttribute>()
            .FirstOrDefault();

        Assert.NotNull(authorizeAttribute);
        Assert.Equal("Admin", authorizeAttribute!.Roles);
    }
}
