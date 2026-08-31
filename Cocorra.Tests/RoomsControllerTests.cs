using Cocorra.BLL.Base;
using Cocorra.BLL.Services.LiveKit;
using Cocorra.BLL.Services.RoomService;
using Cocorra.DAL.DTOS.RoomDto;
using Cocorra.DAL.Enums;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Moq;
using System.Security.Claims;
using Xunit;

namespace Cocorra.Tests;

public class RoomsControllerTests
{
    private readonly Mock<IRoomService> _roomServiceMock = new();
    private readonly Mock<ILiveKitService> _liveKitServiceMock = new();
    private readonly LiveKitSettings _liveKitSettings = new()
    {
        ServerUrl = "wss://test.livekit.dev",
        ApiKey = "testkey",
        ApiSecret = "testsecret"
    };

    private Cocorra.API.Controllers.RoomsController CreateController(Guid? userId = null)
    {
        var controller = new Cocorra.API.Controllers.RoomsController(
            _roomServiceMock.Object,
            _liveKitServiceMock.Object,
            Options.Create(_liveKitSettings)
        );

        // Set up a fake authenticated user
        var uid = userId ?? Guid.NewGuid();
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, uid.ToString()),
            new("VerificationStatus", "Active")
        };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        var principal = new ClaimsPrincipal(identity);

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };

        return controller;
    }

    // ===================================================================
    // Create Endpoint Tests
    // ===================================================================

    [Fact]
    public async Task Create_Success_ReturnsOk()
    {
        var hostId = Guid.NewGuid();
        var dto = new CreateRoomDto { RoomTitle = "New Room" };
        var serviceResponse = new Response<Guid>
        {
            Succeeded = true,
            StatusCode = System.Net.HttpStatusCode.OK,
            Data = Guid.NewGuid()
        };

        _roomServiceMock.Setup(s => s.CreateRoomAsync(dto, hostId, null)).ReturnsAsync(serviceResponse);

        var controller = CreateController(hostId);
        var result = await controller.Create(dto, null);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(serviceResponse, ok.Value);
    }

    // ===================================================================
    // Join Endpoint Tests
    // ===================================================================

    [Fact]
    public async Task Join_Success_ReturnsOkWithToken()
    {
        // Arrange
        var roomId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        _roomServiceMock
            .Setup(s => s.JoinRoomAsync(roomId, userId))
            .ReturnsAsync(new Response<JoinRoomResultDto>
            {
                Succeeded = true,
                StatusCode = System.Net.HttpStatusCode.OK,
                Data = new JoinRoomResultDto
                {
                    LiveKitToken = "jwt-token-here",
                    LiveKitServerUrl = "wss://test.livekit.dev"
                }
            });

        var controller = CreateController(userId);

        // Act
        var result = await controller.Join(roomId);

        // Assert
        var ok = Assert.IsType<OkObjectResult>(result);
        var response = ok.Value as Response<JoinRoomResultDto>;
        Assert.NotNull(response);
        Assert.True(response!.Succeeded);
        Assert.Equal("jwt-token-here", response.Data!.LiveKitToken);
    }

    [Fact]
    public async Task Join_RoomNotFound_ReturnsBadRequest()
    {
        // Arrange
        var roomId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        _roomServiceMock
            .Setup(s => s.JoinRoomAsync(roomId, userId))
            .ReturnsAsync(new Response<JoinRoomResultDto>
            {
                Succeeded = false,
                StatusCode = System.Net.HttpStatusCode.NotFound,
                Message = "Room not found."
            });

        var controller = CreateController(userId);

        // Act
        var result = await controller.Join(roomId);

        // Assert
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var response = badRequest.Value as Response<JoinRoomResultDto>;
        Assert.NotNull(response);
        Assert.False(response!.Succeeded);
    }

    // ===================================================================
    // Approve Endpoint Tests
    // ===================================================================

    [Fact]
    public async Task Approve_Success_ReturnsOk()
    {
        var hostId = Guid.NewGuid();
        var roomId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();
        var serviceResponse = new Response<bool>
        {
            Succeeded = true,
            StatusCode = System.Net.HttpStatusCode.OK,
            Data = true
        };

        _roomServiceMock.Setup(s => s.ApproveUserAsync(roomId, targetUserId, hostId)).ReturnsAsync(serviceResponse);

        var controller = CreateController(hostId);
        var result = await controller.Approve(roomId, targetUserId);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(serviceResponse, ok.Value);
    }

    // ===================================================================
    // GetRoomState Endpoint Tests
    // ===================================================================

    [Fact]
    public async Task GetRoomState_Success_ReturnsOkWithToken()
    {
        // Arrange
        var roomId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        _roomServiceMock
            .Setup(s => s.GetRoomStateAsync(roomId, userId))
            .ReturnsAsync(new Response<RoomStateDto>
            {
                Succeeded = true,
                StatusCode = System.Net.HttpStatusCode.OK,
                Data = new RoomStateDto
                {
                    RoomId = roomId,
                    RoomTitle = "Test Room",
                    HostId = Guid.NewGuid(),
                    Category = RoomCategory.Others,
                    CategoryName = "Education",
                    LiveKitToken = "state-token",
                    LiveKitServerUrl = "wss://test.livekit.dev"
                }
            });

        var controller = CreateController(userId);

        // Act
        var result = await controller.GetRoomState(roomId);

        // Assert
        var ok = Assert.IsType<OkObjectResult>(result);
        var response = ok.Value as Response<RoomStateDto>;
        Assert.NotNull(response);
        Assert.Equal("state-token", response!.Data!.LiveKitToken);
    }

    // ===================================================================
    // Feed & Reminders Tests
    // ===================================================================

    [Fact]
    public async Task GetRoomsFeed_ReturnsOk()
    {
        var userId = Guid.NewGuid();
        var feedItems = new List<RoomSummaryDto>();
        var serviceResponse = new Response<IEnumerable<RoomSummaryDto>>
        {
            Succeeded = true,
            StatusCode = System.Net.HttpStatusCode.OK,
            Data = feedItems
        };

        _roomServiceMock.Setup(s => s.GetRoomsFeedAsync(userId, null, 1, 20)).ReturnsAsync(serviceResponse);

        var controller = CreateController(userId);
        var result = await controller.GetRoomsFeed(null, 1, 20);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(200, obj.StatusCode);
    }

    [Fact]
    public async Task ToggleReminder_ReturnsOk()
    {
        var userId = Guid.NewGuid();
        var roomId = Guid.NewGuid();
        var serviceResponse = new Response<string>
        {
            Succeeded = true,
            StatusCode = System.Net.HttpStatusCode.OK,
            Data = "Reminder toggled"
        };

        _roomServiceMock.Setup(s => s.ToggleReminderAsync(roomId, userId)).ReturnsAsync(serviceResponse);

        var controller = CreateController(userId);
        var result = await controller.ToggleReminder(roomId);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(200, obj.StatusCode);
    }

    [Fact]
    public async Task StartScheduledRoom_ReturnsOk()
    {
        var hostId = Guid.NewGuid();
        var roomId = Guid.NewGuid();
        var serviceResponse = new Response<string>
        {
            Succeeded = true,
            StatusCode = System.Net.HttpStatusCode.OK,
            Data = "Room started"
        };

        _roomServiceMock.Setup(s => s.StartScheduledRoomAsync(roomId, hostId)).ReturnsAsync(serviceResponse);

        var controller = CreateController(hostId);
        var result = await controller.StartScheduledRoom(roomId);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(200, obj.StatusCode);
    }

    // ===================================================================
    // GetLiveKitToken Endpoint Tests
    // ===================================================================

    [Fact]
    public async Task GetLiveKitToken_ValidParticipant_ReturnsToken()
    {
        // Arrange
        var roomId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        _roomServiceMock
            .Setup(s => s.GetRoomStateAsync(roomId, userId))
            .ReturnsAsync(new Response<RoomStateDto>
            {
                Succeeded = true,
                StatusCode = System.Net.HttpStatusCode.OK,
                Data = new RoomStateDto
                {
                    RoomId = roomId,
                    LiveKitToken = "fresh-token",
                    LiveKitServerUrl = "wss://test.livekit.dev"
                }
            });

        var controller = CreateController(userId);

        // Act
        var result = await controller.GetLiveKitToken(roomId);

        // Assert
        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(ok.Value);

        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        Assert.Contains("fresh-token", json);
        Assert.Contains("wss://test.livekit.dev", json);
        Assert.Contains("IceServers", json);
    }

    [Fact]
    public async Task GetLiveKitToken_NotParticipant_ReturnsError()
    {
        // Arrange
        var roomId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        _roomServiceMock
            .Setup(s => s.GetRoomStateAsync(roomId, userId))
            .ReturnsAsync(new Response<RoomStateDto>
            {
                Succeeded = false,
                StatusCode = System.Net.HttpStatusCode.BadRequest,
                Message = "You are not an active member of this room."
            });

        var controller = CreateController(userId);

        // Act
        var result = await controller.GetLiveKitToken(roomId);

        // Assert
        var statusResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(400, statusResult.StatusCode);
    }

    // ===================================================================
    // EndRoom Endpoint Tests
    // ===================================================================

    [Fact]
    public async Task EndRoom_Success_ReturnsOk()
    {
        // Arrange
        var roomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();

        _roomServiceMock
            .Setup(s => s.EndRoomAsync(roomId, hostId))
            .ReturnsAsync(new Response<string>
            {
                Succeeded = true,
                StatusCode = System.Net.HttpStatusCode.OK,
                Data = "Room has been ended successfully."
            });

        var controller = CreateController(hostId);

        // Act
        var result = await controller.EndRoom(roomId);

        // Assert
        var statusResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(200, statusResult.StatusCode);
    }

    [Fact]
    public async Task GetRoomHistory_ReturnsOk()
    {
        var serviceResponse = new Response<IEnumerable<RoomSummaryDto>>
        {
            Succeeded = true,
            StatusCode = System.Net.HttpStatusCode.OK,
            Data = new List<RoomSummaryDto>()
        };

        _roomServiceMock.Setup(s => s.GetEndedRoomsHistoryAsync(1, 20)).ReturnsAsync(serviceResponse);

        var controller = CreateController();
        var result = await controller.GetRoomHistory(1, 20);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(200, obj.StatusCode);
    }
}
