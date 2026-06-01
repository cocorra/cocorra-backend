using Cocorra.BLL.Base;
using Cocorra.BLL.Services.LiveKit;
using Cocorra.BLL.Services.NotificationService;
using Cocorra.BLL.Services.RoomService;
using Cocorra.BLL.Services.Upload;
using Cocorra.DAL.DTOS.RoomDto;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;
using Cocorra.DAL.Repository.RoomRepository;
using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Moq;

namespace Cocorra.Tests;

public class RoomServiceTests
{
    private readonly Mock<IRoomRepository> _roomRepoMock = new();
    private readonly Mock<IMediator> _mediatorMock = new();
    private readonly Mock<IUploadImage> _uploadImageMock = new();
    private readonly Mock<IPushNotificationService> _pushServiceMock = new();
    private readonly Mock<ILiveKitService> _liveKitServiceMock = new();
    private readonly LiveKitSettings _liveKitSettings = new()
    {
        ServerUrl = "wss://test.livekit.dev",
        ApiKey = "testkey",
        ApiSecret = "testsecret"
    };

    private RoomService CreateService()
    {
        var configMock = new Mock<IConfiguration>();
        configMock.Setup(c => c["AppSettings:BaseUrl"]).Returns("https://api.test.com");

        // Mock UserManager
        var store = new Mock<IUserStore<ApplicationUser>>();
        var userManager = new Mock<UserManager<ApplicationUser>>(
            store.Object, null!, null!, null!, null!, null!, null!, null!, null!);

        return new RoomService(
            _roomRepoMock.Object,
            _mediatorMock.Object,
            _uploadImageMock.Object,
            configMock.Object,
            _pushServiceMock.Object,
            userManager.Object,
            _liveKitServiceMock.Object,
            Options.Create(_liveKitSettings)
        );
    }

    // ===================================================================
    // JoinRoomAsync Tests
    // ===================================================================

    [Fact]
    public async Task JoinRoomAsync_RoomNotFound_ReturnsNotFound()
    {
        // Arrange
        _roomRepoMock.Setup(r => r.GetByIdAsync(It.IsAny<Guid>()))
            .ReturnsAsync((Room?)null);
        var service = CreateService();

        // Act
        var result = await service.JoinRoomAsync(Guid.NewGuid(), Guid.NewGuid());

        // Assert
        Assert.False(result.Succeeded);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, result.StatusCode);
    }

    [Fact]
    public async Task JoinRoomAsync_ScheduledRoom_ReturnsBadRequest()
    {
        // Arrange
        var roomId = Guid.NewGuid();
        _roomRepoMock.Setup(r => r.GetByIdAsync(roomId))
            .ReturnsAsync(new Room { Id = roomId, Status = RoomStatus.Scheduled });
        var service = CreateService();

        // Act
        var result = await service.JoinRoomAsync(roomId, Guid.NewGuid());

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains("not started yet", result.Message!);
    }

    [Fact]
    public async Task JoinRoomAsync_EndedRoom_ReturnsBadRequest()
    {
        // Arrange
        var roomId = Guid.NewGuid();
        _roomRepoMock.Setup(r => r.GetByIdAsync(roomId))
            .ReturnsAsync(new Room { Id = roomId, Status = RoomStatus.Ended });
        var service = CreateService();

        // Act
        var result = await service.JoinRoomAsync(roomId, Guid.NewGuid());

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains("no longer available", result.Message!);
    }

    [Fact]
    public async Task JoinRoomAsync_KickedUser_ReturnsBadRequest()
    {
        // Arrange
        var roomId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var room = new Room { Id = roomId, Status = RoomStatus.Live, TotalCapacity = 50 };

        _roomRepoMock.Setup(r => r.GetByIdAsync(roomId)).ReturnsAsync(room);
        _roomRepoMock.Setup(r => r.GetRoomParticipantsAsync(roomId))
            .ReturnsAsync(new List<RoomParticipant>
            {
                new() { RoomId = roomId, UserId = userId, Status = ParticipantStatus.Kicked }
            });
        var service = CreateService();

        // Act
        var result = await service.JoinRoomAsync(roomId, userId);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains("banned", result.Message!);
    }

    [Fact]
    public async Task JoinRoomAsync_ActiveUser_ReturnsTokenSuccessfully()
    {
        // Arrange
        var roomId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var room = new Room { Id = roomId, Status = RoomStatus.Live, TotalCapacity = 50 };

        var participant = new RoomParticipant
        {
            RoomId = roomId,
            UserId = userId,
            Status = ParticipantStatus.Active,
            User = new ApplicationUser { FirstName = "Ali", LastName = "Hassan" }
        };

        _roomRepoMock.Setup(r => r.GetByIdAsync(roomId)).ReturnsAsync(room);
        _roomRepoMock.Setup(r => r.GetRoomParticipantsAsync(roomId))
            .ReturnsAsync(new List<RoomParticipant> { participant });
        _liveKitServiceMock
            .Setup(l => l.GenerateToken(roomId, userId, "Ali Hassan"))
            .Returns("mock-jwt-token");
        var service = CreateService();

        // Act
        var result = await service.JoinRoomAsync(roomId, userId);

        // Assert
        Assert.True(result.Succeeded);
        Assert.NotNull(result.Data);
        Assert.Equal("mock-jwt-token", result.Data!.LiveKitToken);
        Assert.Equal(_liveKitSettings.ServerUrl, result.Data.LiveKitServerUrl);
    }

    [Fact]
    public async Task JoinRoomAsync_PrivateRoom_ReturnsNullToken()
    {
        // Arrange
        var roomId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        var room = new Room
        {
            Id = roomId,
            Status = RoomStatus.Live,
            IsPrivate = true,
            TotalCapacity = 50,
            HostId = hostId,
            Host = new ApplicationUser { Id = hostId, FirstName = "Host", LastName = "User" }
        };

        _roomRepoMock.Setup(r => r.GetByIdAsync(roomId)).ReturnsAsync(room);
        _roomRepoMock.Setup(r => r.GetRoomParticipantsAsync(roomId))
            .ReturnsAsync(new List<RoomParticipant>()); // No existing participant
        _roomRepoMock.Setup(r => r.AddParticipantAsync(It.IsAny<RoomParticipant>())).Returns(Task.CompletedTask);
        _roomRepoMock.Setup(r => r.SaveChangesAsync()).Returns(Task.CompletedTask);
        _roomRepoMock.Setup(r => r.AddNotificationsAsync(It.IsAny<IEnumerable<Notification>>())).Returns(Task.CompletedTask);

        var service = CreateService();

        // Act
        var result = await service.JoinRoomAsync(roomId, userId);

        // Assert
        Assert.True(result.Succeeded);
        Assert.NotNull(result.Data);
        Assert.Null(result.Data!.LiveKitToken); // Token should be null for pending approval
        Assert.Null(result.Data.LiveKitServerUrl);
        Assert.Contains("waiting for approval", result.Message!);
    }

    [Fact]
    public async Task JoinRoomAsync_RoomFull_ReturnsBadRequest()
    {
        // Arrange
        var roomId = Guid.NewGuid();
        var room = new Room { Id = roomId, Status = RoomStatus.Live, TotalCapacity = 1 };

        _roomRepoMock.Setup(r => r.GetByIdAsync(roomId)).ReturnsAsync(room);
        _roomRepoMock.Setup(r => r.GetRoomParticipantsAsync(roomId))
            .ReturnsAsync(new List<RoomParticipant>
            {
                new() { RoomId = roomId, UserId = Guid.NewGuid(), Status = ParticipantStatus.Active }
            });
        var service = CreateService();

        // Act
        var result = await service.JoinRoomAsync(roomId, Guid.NewGuid());

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains("full", result.Message!);
    }

    // ===================================================================
    // GetRoomStateAsync Tests
    // ===================================================================

    [Fact]
    public async Task GetRoomStateAsync_ValidRoom_IncludesLiveKitToken()
    {
        // Arrange
        var roomId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var room = new Room
        {
            Id = roomId,
            RoomTitle = "Test Room",
            HostId = Guid.NewGuid(),
            TotalCapacity = 50,
            StageCapacity = 5,
            Status = RoomStatus.Live,
            Category = RoomCategory.Others
        };

        var participant = new RoomParticipant
        {
            RoomId = roomId,
            UserId = userId,
            Status = ParticipantStatus.Active,
            User = new ApplicationUser { FirstName = "Test", LastName = "User" }
        };

        _roomRepoMock.Setup(r => r.GetByIdAsync(roomId)).ReturnsAsync(room);
        _roomRepoMock.Setup(r => r.GetParticipantAsync(roomId, userId)).ReturnsAsync(participant);
        _roomRepoMock.Setup(r => r.GetRoomParticipantsAsync(roomId))
            .ReturnsAsync(new List<RoomParticipant> { participant });
        _liveKitServiceMock
            .Setup(l => l.GenerateToken(roomId, userId, "Test User"))
            .Returns("state-jwt-token");
        var service = CreateService();

        // Act
        var result = await service.GetRoomStateAsync(roomId, userId);

        // Assert
        Assert.True(result.Succeeded);
        Assert.NotNull(result.Data);
        Assert.Equal("state-jwt-token", result.Data!.LiveKitToken);
        Assert.Equal(_liveKitSettings.ServerUrl, result.Data.LiveKitServerUrl);
    }

    [Fact]
    public async Task GetRoomStateAsync_RoomNotFound_ReturnsNotFound()
    {
        // Arrange
        _roomRepoMock.Setup(r => r.GetByIdAsync(It.IsAny<Guid>()))
            .ReturnsAsync((Room?)null);
        var service = CreateService();

        // Act
        var result = await service.GetRoomStateAsync(Guid.NewGuid(), Guid.NewGuid());

        // Assert
        Assert.False(result.Succeeded);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, result.StatusCode);
    }

    [Fact]
    public async Task GetRoomStateAsync_NonParticipant_ReturnsBadRequest()
    {
        // Arrange
        var roomId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var room = new Room { Id = roomId, Status = RoomStatus.Live };

        _roomRepoMock.Setup(r => r.GetByIdAsync(roomId)).ReturnsAsync(room);
        _roomRepoMock.Setup(r => r.GetParticipantAsync(roomId, userId))
            .ReturnsAsync((RoomParticipant?)null);
        var service = CreateService();

        // Act
        var result = await service.GetRoomStateAsync(roomId, userId);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, result.StatusCode);
    }

    // ===================================================================
    // EndRoomAsync Tests
    // ===================================================================

    [Fact]
    public async Task EndRoomAsync_ByHost_ReturnsSuccess()
    {
        // Arrange
        var roomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        var room = new Room { Id = roomId, HostId = hostId, Status = RoomStatus.Live };

        _roomRepoMock.Setup(r => r.GetByIdAsync(roomId)).ReturnsAsync(room);
        _roomRepoMock.Setup(r => r.UpdateAsync(It.IsAny<Room>())).Returns(Task.CompletedTask);
        _roomRepoMock.Setup(r => r.GetRoomParticipantsAsync(roomId))
            .ReturnsAsync(new List<RoomParticipant>());
        _roomRepoMock.Setup(r => r.SaveChangesAsync()).Returns(Task.CompletedTask);
        var service = CreateService();

        // Act
        var result = await service.EndRoomAsync(roomId, hostId);

        // Assert
        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task EndRoomAsync_ByNonHost_ReturnsBadRequest()
    {
        // Arrange
        var roomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        var room = new Room { Id = roomId, HostId = hostId, Status = RoomStatus.Live };

        _roomRepoMock.Setup(r => r.GetByIdAsync(roomId)).ReturnsAsync(room);
        var service = CreateService();

        // Act
        var result = await service.EndRoomAsync(roomId, otherId);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains("host", result.Message!, StringComparison.OrdinalIgnoreCase);
    }
}
