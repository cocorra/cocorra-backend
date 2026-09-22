using Cocorra.BLL.Services.EventTracking;
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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Cocorra.Tests;

/// <summary>
/// Phase 4 — closing the auto_create gap.
///
/// LiveKit's auto_create defaults to true, so the first valid token to arrive brings the room
/// into existence. That quietly undoes EndRoomAsync's teardown: someone holding an unexpired
/// token who reconnects to an ended room recreates it and is let in, and two of them can hear
/// each other in a session the database considers finished. The participant_joined webhook
/// evicts them, but only after a round-trip and only while that feed is healthy.
///
/// Turning auto_create off removes the window entirely — but only if the backend creates every
/// room itself first, because with it off a room nobody created is a room nobody can join.
/// These tests pin that the backend does, on both go-live paths, and that a failure to do so
/// stays non-fatal while auto_create is still the safety net.
/// </summary>
public class LiveKitRoomCreationTests
{
    private readonly Mock<IRoomRepository> _roomRepo = new();
    private readonly Mock<IEventTracker> _eventTracker = new();
    private readonly Mock<ILiveKitService> _liveKit = new();
    private readonly Mock<ILogger<RoomService>> _logger = new();

    private readonly LiveKitSettings _settings = new()
    {
        ServerUrl = "wss://test.livekit.dev",
        ApiKey = "k",
        ApiSecret = "s"
    };

    public LiveKitRoomCreationTests()
    {
        _liveKit.Setup(l => l.EnsureRoomExistsAsync(It.IsAny<Guid>(), It.IsAny<TimeSpan>()))
            .ReturnsAsync(true);
    }

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
            Options.Create(_settings),
            _eventTracker.Object,
            _logger.Object);
    }

    private static CreateRoomDto LiveRoomDto() => new()
    {
        RoomTitle = "Coaching session",
        TotalCapacity = 20,
        StageCapacity = 4,
        DurationHours = 2,
        Category = RoomCategory.MentalHealth
        // No ScheduledStartDate — created directly as live.
    };

    // ── Both go-live paths claim the room ────────────────────────────────────────

    [Fact]
    public async Task CreatingARoomLive_ClaimsItOnTheMediaServer()
    {
        Room? saved = null;
        _roomRepo.Setup(r => r.AddAsync(It.IsAny<Room>()))
            .Callback<Room>(r => saved = r)
            .ReturnsAsync((Room r) => r);

        var result = await CreateService().CreateRoomAsync(LiveRoomDto(), Guid.NewGuid());

        Assert.True(result.Succeeded);
        Assert.NotNull(saved);
        _liveKit.Verify(l => l.EnsureRoomExistsAsync(saved!.Id, It.IsAny<TimeSpan>()), Times.Once);
    }

    [Fact]
    public async Task StartingAScheduledRoom_ClaimsItOnTheMediaServer()
    {
        var hostId = Guid.NewGuid();
        var room = new Room
        {
            Id = Guid.NewGuid(),
            HostId = hostId,
            Status = RoomStatus.Scheduled,
            DurationHours = 2,
            StartDate = DateTime.UtcNow
        };
        _roomRepo.Setup(r => r.GetByIdAsync(room.Id)).ReturnsAsync(room);
        _roomRepo.Setup(r => r.GetRemindersByRoomIdAsync(room.Id))
            .ReturnsAsync(new List<RoomReminder>());

        var result = await CreateService().StartScheduledRoomAsync(room.Id, hostId);

        Assert.True(result.Succeeded);
        _liveKit.Verify(l => l.EnsureRoomExistsAsync(room.Id, It.IsAny<TimeSpan>()), Times.Once);
    }

    [Fact]
    public async Task SchedulingARoomForLater_DoesNotClaimItYet()
    {
        var dto = LiveRoomDto();
        dto.ScheduledStartDate = DateTime.UtcNow.AddHours(4);

        _roomRepo.Setup(r => r.AddAsync(It.IsAny<Room>())).ReturnsAsync((Room r) => r);

        var result = await CreateService().CreateRoomAsync(dto, Guid.NewGuid());

        Assert.True(result.Succeeded);
        // A room hours away has no business holding a slot on the media server, and with a
        // finite empty_timeout it would be reaped long before anyone arrived anyway.
        _liveKit.Verify(
            l => l.EnsureRoomExistsAsync(It.IsAny<Guid>(), It.IsAny<TimeSpan>()), Times.Never);
    }

    // ── The empty_timeout has to outlast the room ────────────────────────────────

    [Fact]
    public async Task TheRoomIsHeldOpen_ForTheConfiguredEmptyTimeout()
    {
        _settings.RoomEmptyTimeoutMinutes = 240;

        TimeSpan? passed = null;
        _liveKit.Setup(l => l.EnsureRoomExistsAsync(It.IsAny<Guid>(), It.IsAny<TimeSpan>()))
            .Callback<Guid, TimeSpan>((_, t) => passed = t)
            .ReturnsAsync(true);

        _roomRepo.Setup(r => r.AddAsync(It.IsAny<Room>())).ReturnsAsync((Room r) => r);

        await CreateService().CreateRoomAsync(LiveRoomDto(), Guid.NewGuid());

        Assert.Equal(TimeSpan.FromMinutes(240), passed);
    }

    [Fact]
    public void TheEmptyTimeout_OutlastsTheLongestBookableRoom()
    {
        // LiveKit's own default is 5 minutes. Left at that, a room would be reaped during any
        // lull — before the first participant arrives, or while everyone is briefly
        // disconnected. Invisible today because auto_create silently recreates it; with
        // auto_create off it locks everyone out of a room that is still Live in the database.
        var liveKit = new LiveKitSettings();
        var lifecycle = new RoomLifecycleSettings();

        var longestRoomMinutes = (3 * 60) + lifecycle.RoomOvertimeGraceMinutes;

        Assert.True(
            liveKit.RoomEmptyTimeoutMinutes > longestRoomMinutes,
            $"empty_timeout is {liveKit.RoomEmptyTimeoutMinutes}m but the longest room can run " +
            $"for {longestRoomMinutes}m. LiveKit would reap a live room mid-session, and with " +
            "auto_create disabled nobody could rejoin it.");
    }

    // ── Failure stays non-fatal while auto_create is the safety net ──────────────

    [Fact]
    public async Task AFailureToClaimTheRoom_DoesNotStopTheRoomGoingLive()
    {
        _liveKit.Setup(l => l.EnsureRoomExistsAsync(It.IsAny<Guid>(), It.IsAny<TimeSpan>()))
            .ReturnsAsync(false);
        _roomRepo.Setup(r => r.AddAsync(It.IsAny<Room>())).ReturnsAsync((Room r) => r);

        var result = await CreateService().CreateRoomAsync(LiveRoomDto(), Guid.NewGuid());

        // auto_create still covers the join. Failing a coach's go-live over a media-server
        // hiccup would be a worse outcome than the hardening is worth.
        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task AFailureToClaimTheRoom_IsWarnedAboutLoudly()
    {
        _liveKit.Setup(l => l.EnsureRoomExistsAsync(It.IsAny<Guid>(), It.IsAny<TimeSpan>()))
            .ReturnsAsync(false);
        _roomRepo.Setup(r => r.AddAsync(It.IsAny<Room>())).ReturnsAsync((Room r) => r);

        await CreateService().CreateRoomAsync(LiveRoomDto(), Guid.NewGuid());

        // This warning is the gate on turning auto_create off. If it ever fires, flipping the
        // flag would have made that room unjoinable — so it has to be visible, not swallowed.
        _logger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("without being created")),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }
}
