using System.Security.Claims;
using Cocorra.API.Controllers;
using Cocorra.BLL.Base;
using Cocorra.BLL.Services.NotificationService;
using Cocorra.DAL.DTOS.NotificationDto;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Cocorra.Tests;

public class NotificationsControllerTests
{
    private readonly Mock<INotificationService> _notificationServiceMock = new();

    private NotificationsController CreateController(Guid? userId = null)
    {
        var controller = new NotificationsController(_notificationServiceMock.Object);

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
    public async Task GetMyNotifications_Unauthorized_WhenNoUser()
    {
        var controller = CreateController();
        var result = await controller.GetMyNotifications();

        Assert.IsType<UnauthorizedResult>(result);
    }

    [Fact]
    public async Task GetMyNotifications_Success_ReturnsStatusCodeResult()
    {
        var userId = Guid.NewGuid();
        var notifications = new List<NotificationResponseDto>
        {
            new() { Id = Guid.NewGuid(), Title = "Test Title", Message = "Test Message" }
        };
        var serviceResponse = new Response<IEnumerable<NotificationResponseDto>>
        {
            Succeeded = true,
            StatusCode = System.Net.HttpStatusCode.OK,
            Data = notifications
        };

        _notificationServiceMock.Setup(s => s.GetMyNotificationsAsync(userId, 1, 20)).ReturnsAsync(serviceResponse);

        var controller = CreateController(userId);
        var result = await controller.GetMyNotifications(1, 20);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(200, obj.StatusCode);
        Assert.Equal(serviceResponse, obj.Value);
    }

    [Fact]
    public async Task MarkNotificationRead_Success_ReturnsOk()
    {
        var userId = Guid.NewGuid();
        var notificationId = Guid.NewGuid();
        var serviceResponse = new Response<string>
        {
            Succeeded = true,
            StatusCode = System.Net.HttpStatusCode.OK,
            Data = "Marked as read"
        };

        _notificationServiceMock.Setup(s => s.MarkNotificationAsReadAsync(notificationId, userId)).ReturnsAsync(serviceResponse);

        var controller = CreateController(userId);
        var result = await controller.MarkNotificationRead(notificationId);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(200, obj.StatusCode);
        Assert.Equal(serviceResponse, obj.Value);
    }

    [Fact]
    public async Task MarkAllRead_Success_ReturnsOk()
    {
        var userId = Guid.NewGuid();
        var serviceResponse = new Response<string>
        {
            Succeeded = true,
            StatusCode = System.Net.HttpStatusCode.OK,
            Data = "All marked as read"
        };

        _notificationServiceMock.Setup(s => s.MarkAllAsReadAsync(userId)).ReturnsAsync(serviceResponse);

        var controller = CreateController(userId);
        var result = await controller.MarkAllRead();

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(200, obj.StatusCode);
        Assert.Equal(serviceResponse, obj.Value);
    }
}
