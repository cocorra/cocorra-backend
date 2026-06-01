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

        // Use dynamic to check the anonymous object
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        Assert.Contains("fresh-token", json);
        Assert.Contains("wss://test.livekit.dev", json);
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
}
