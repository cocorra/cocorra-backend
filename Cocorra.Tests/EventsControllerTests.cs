using System.Security.Claims;
using Cocorra.API.Controllers;
using Cocorra.BLL.Services.EventTracking;
using Cocorra.DAL.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Cocorra.Tests;

public class EventsControllerTests
{
    private readonly Mock<IEventTracker> _trackerMock = new();

    private EventsController CreateController(Guid? userId = null)
    {
        var controller = new EventsController(_trackerMock.Object);

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
    public void Track_EmptyEventType_ReturnsBadRequest()
    {
        var controller = CreateController();
        var result = controller.Track(new TrackEventDto { EventType = "" });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public void Track_DisallowedServerEventType_ReturnsBadRequest()
    {
        var controller = CreateController();
        // activation_completed is server-only, not client-allowed
        var result = controller.Track(new TrackEventDto { EventType = EventTypes.ActivationCompleted });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public void Track_AllowedEventType_CallsTracker_AndReturnsOk()
    {
        var userId = Guid.NewGuid();
        var controller = CreateController(userId);

        var dto = new TrackEventDto
        {
            EventType = EventTypes.RoomCreateStarted,
            Properties = new { Source = "Mobile" }
        };

        var result = controller.Track(dto);

        var ok = Assert.IsType<OkObjectResult>(result);
        _trackerMock.Verify(t => t.Track(EventTypes.RoomCreateStarted, (Guid?)userId, dto.Properties), Times.Once);
    }

    [Fact]
    public void Track_AllowedEventType_AnonymousUser_CallsTrackerWithNullUserId()
    {
        var controller = CreateController(); // No user claims

        var dto = new TrackEventDto
        {
            EventType = EventTypes.FeatureViewed,
            Properties = new { Feature = "DarkTheme" }
        };

        var result = controller.Track(dto);

        var ok = Assert.IsType<OkObjectResult>(result);
        _trackerMock.Verify(t => t.Track(EventTypes.FeatureViewed, (Guid?)null, dto.Properties), Times.Once);
    }
}
