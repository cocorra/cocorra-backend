using Cocorra.BLL.Services.EventTracking;
using Cocorra.BLL.Services.LiveKit;
using Cocorra.BLL.Services.NotificationService;
using Cocorra.BLL.Services.RoomService;
using Cocorra.BLL.Services.Upload;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;
using Cocorra.DAL.Repository.RoomRepository;
using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Cocorra.Tests;

/// <summary>
/// A LiveKit access token is a stateless signed JWT — nothing records that it was issued, so
/// nothing can revoke it. The only defences are refusing to mint one and evicting the holder,
/// and both depend on the database being the authority on who may be in a room. These tests
/// pin the mint-side guard.
/// </summary>
public class LiveKitCredentialLifecycleTests
{
    private readonly Mock<IRoomRepository> _roomRepo = new();
    private readonly Mock<ILiveKitService> _liveKit = new();

    private RoomService CreateService()
    {
        var config = new Mock<IConfiguration>();
        config.Setup(c => c["AppSettings:BaseUrl"]).Returns("https://api.test.com");

        var store = new Mock<IUserStore<ApplicationUser>>();
        var userManager = new Mock<UserManager<ApplicationUser>>(
            store.Object, null!, null!, null!, null!, null!, null!, null!, null!);

        return new RoomService(
            _roomRepo.Object,
            new Mock<IMediator>().Object,
            new Mock<IUploadImage>().Object,
            config.Object,
            new Mock<IPushNotificationService>().Object,
            userManager.Object,
            _liveKit.Object,
            Options.Create(new LiveKitSettings
            {
                ServerUrl = "wss://test.livekit.dev",
                ApiKey = "k",
                ApiSecret = "s"
            }),
            new Mock<IEventTracker>().Object,
            new Mock<ILogger<RoomService>>().Object);
    }

    private static Room RoomWith(RoomStatus status, Guid hostId) => new()
    {
        Id = Guid.NewGuid(),
        HostId = hostId,
        Status = status,
        StartDate = DateTime.UtcNow.AddMinutes(-10)
    };

    [Theory]
    [InlineData(RoomStatus.Ended)]
    [InlineData(RoomStatus.Scheduled)]
    [InlineData(RoomStatus.Cancelled)]
    public async Task GetRoomState_RefusesToMintAToken_WhenTheRoomIsNotLive(RoomStatus status)
    {
        // GET /Room/{id}/Token delegates here purely to obtain a credential, so this guard is
        // authorization. It previously relied on participants being flipped to Left when a room
        // ended — an indirect check that DeleteAccountAsync bypassed entirely.
        var userId = Guid.NewGuid();
        var room = RoomWith(status, Guid.NewGuid());

        _roomRepo.Setup(r => r.GetByIdAsync(room.Id)).ReturnsAsync(room);
        // Deliberately still Active: the room's status alone must be enough to refuse.
        _roomRepo.Setup(r => r.GetParticipantAsync(room.Id, userId))
            .ReturnsAsync(new RoomParticipant
            {
                RoomId = room.Id,
                UserId = userId,
                Status = ParticipantStatus.Active
            });

        var result = await CreateService().GetRoomStateAsync(room.Id, userId);

        Assert.False(result.Succeeded);
        _liveKit.Verify(l => l.GenerateToken(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task GetRoomState_StillMintsAToken_ForAnActiveMemberOfALiveRoom()
    {
        var userId = Guid.NewGuid();
        var room = RoomWith(RoomStatus.Live, Guid.NewGuid());

        _roomRepo.Setup(r => r.GetByIdAsync(room.Id)).ReturnsAsync(room);
        _roomRepo.Setup(r => r.GetParticipantAsync(room.Id, userId))
            .ReturnsAsync(new RoomParticipant
            {
                RoomId = room.Id,
                UserId = userId,
                Status = ParticipantStatus.Active
            });
        _roomRepo.Setup(r => r.GetRoomParticipantsAsync(room.Id))
            .ReturnsAsync(new List<RoomParticipant>());
        _liveKit.Setup(l => l.GenerateToken(room.Id, userId, It.IsAny<string>(), It.IsAny<bool>()))
            .Returns("jwt");

        var result = await CreateService().GetRoomStateAsync(room.Id, userId);

        Assert.True(result.Succeeded);
        Assert.Equal("jwt", result.Data!.LiveKitToken);
    }

    [Fact]
    public async Task GetRoomState_RefusesAKickedMemberOfALiveRoom()
    {
        var userId = Guid.NewGuid();
        var room = RoomWith(RoomStatus.Live, Guid.NewGuid());

        _roomRepo.Setup(r => r.GetByIdAsync(room.Id)).ReturnsAsync(room);
        _roomRepo.Setup(r => r.GetParticipantAsync(room.Id, userId))
            .ReturnsAsync(new RoomParticipant
            {
                RoomId = room.Id,
                UserId = userId,
                Status = ParticipantStatus.Kicked
            });

        var result = await CreateService().GetRoomStateAsync(room.Id, userId);

        Assert.False(result.Succeeded);
        _liveKit.Verify(l => l.GenerateToken(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
    }
}
