using Cocorra.BLL.Services.EventTracking;
using Cocorra.BLL.Services.LiveKit;
using Cocorra.BLL.Services.NotificationService;
using Cocorra.BLL.Services.RealTimeNotifier;
using Cocorra.BLL.Services.RoomService;
using Cocorra.BLL.Services.Upload;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;
using Cocorra.DAL.Repository.RoomRepository;
using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Cocorra.Tests;

/// <summary>
/// A host's socket dropping used to end the room instantly and irreversibly, so a lift or a
/// tunnel killed the session for everyone. The room is now held Live for a grace window and
/// only closed if the host genuinely does not return. These tests pin both halves — the
/// reprieve and the eventual close — and the analytics distinction between them.
/// </summary>
public class HostReconnectGraceTests
{
    private readonly Mock<IRoomRepository> _roomRepo = new();
    private readonly Mock<IEventTracker> _eventTracker = new();
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
            _eventTracker.Object,
            new Mock<ILogger<RoomService>>().Object);
    }

    private static Room LiveRoom(Guid hostId, DateTime? hostDisconnectedAt = null) => new()
    {
        Id = Guid.NewGuid(),
        HostId = hostId,
        Status = RoomStatus.Live,
        StartDate = DateTime.UtcNow.AddMinutes(-30),
        HostDisconnectedAt = hostDisconnectedAt
    };

    // ── Starting the window ──────────────────────────────────────────────────────

    [Fact]
    public async Task MarkHostDisconnected_StartsTheWindow_WithoutEndingTheRoom()
    {
        var hostId = Guid.NewGuid();
        var room = LiveRoom(hostId);
        _roomRepo.Setup(r => r.GetByIdAsync(room.Id)).ReturnsAsync(room);

        var started = await CreateService().MarkHostDisconnectedAsync(room.Id, hostId);

        Assert.True(started);
        Assert.NotNull(room.HostDisconnectedAt);
        // The whole point: the audience is still in a live room.
        Assert.Equal(RoomStatus.Live, room.Status);
    }

    [Fact]
    public async Task MarkHostDisconnected_DoesNotExtendAWindowAlreadyRunning()
    {
        // A flapping connection must not renew the deadline forever, or the audience would
        // never be told the room is dead.
        var hostId = Guid.NewGuid();
        var firstDrop = DateTime.UtcNow.AddSeconds(-60);
        var room = LiveRoom(hostId, firstDrop);
        _roomRepo.Setup(r => r.GetByIdAsync(room.Id)).ReturnsAsync(room);

        var started = await CreateService().MarkHostDisconnectedAsync(room.Id, hostId);

        Assert.False(started);
        Assert.Equal(firstDrop, room.HostDisconnectedAt);
    }

    [Fact]
    public async Task MarkHostDisconnected_IgnoresANonHostParticipant()
    {
        var room = LiveRoom(Guid.NewGuid());
        _roomRepo.Setup(r => r.GetByIdAsync(room.Id)).ReturnsAsync(room);

        var started = await CreateService().MarkHostDisconnectedAsync(room.Id, Guid.NewGuid());

        Assert.False(started);
        Assert.Null(room.HostDisconnectedAt);
    }

    [Fact]
    public async Task MarkHostDisconnected_IgnoresARoomThatIsNotLive()
    {
        var hostId = Guid.NewGuid();
        var room = LiveRoom(hostId);
        room.Status = RoomStatus.Ended;
        _roomRepo.Setup(r => r.GetByIdAsync(room.Id)).ReturnsAsync(room);

        var started = await CreateService().MarkHostDisconnectedAsync(room.Id, hostId);

        Assert.False(started);
        Assert.Null(room.HostDisconnectedAt);
    }

    // ── The host coming back ─────────────────────────────────────────────────────

    [Fact]
    public async Task ClearHostDisconnected_CancelsTheWindow_AndReportsItWasOpen()
    {
        var hostId = Guid.NewGuid();
        var room = LiveRoom(hostId, DateTime.UtcNow.AddSeconds(-20));
        _roomRepo.Setup(r => r.GetByIdAsync(room.Id)).ReturnsAsync(room);

        var cancelled = await CreateService().ClearHostDisconnectedAsync(room.Id, hostId);

        Assert.True(cancelled);
        Assert.Null(room.HostDisconnectedAt);
        Assert.Equal(RoomStatus.Live, room.Status);
    }

    [Fact]
    public async Task ClearHostDisconnected_ReportsFalse_OnAnOrdinaryHostJoin()
    {
        // No window was open, so the hub must not broadcast a spurious HostReconnected.
        var hostId = Guid.NewGuid();
        var room = LiveRoom(hostId);
        _roomRepo.Setup(r => r.GetByIdAsync(room.Id)).ReturnsAsync(room);

        var cancelled = await CreateService().ClearHostDisconnectedAsync(room.Id, hostId);

        Assert.False(cancelled);
    }

    // ── The window expiring ──────────────────────────────────────────────────────

    [Fact]
    public async Task ExpiredGrace_EndsTheRoom_AndRecordsItAsADisconnectNotAChoice()
    {
        var hostId = Guid.NewGuid();
        var room = LiveRoom(hostId, DateTime.UtcNow.AddSeconds(-120));

        _roomRepo.Setup(r => r.GetRoomsWithExpiredHostGraceAsync(It.IsAny<DateTime>()))
            .ReturnsAsync(new List<Room> { room });
        _roomRepo.Setup(r => r.GetByIdAsync(room.Id)).ReturnsAsync(room);
        _roomRepo.Setup(r => r.GetRoomParticipantsAsync(room.Id))
            .ReturnsAsync(new List<RoomParticipant>());

        string? capturedEndReason = null;
        _eventTracker.Setup(t => t.Track(
                EventTypes.RoomEnded, It.IsAny<Guid?>(), It.IsAny<object>(),
                It.IsAny<string?>(), It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<byte>()))
            .Callback<string, Guid?, object?, string?, Guid?, Guid?, byte>((_, _, props, _, _, _, _) =>
                capturedEndReason = props?.GetType().GetProperty("endReason")?
                    .GetValue(props)?.ToString());

        var ended = await CreateService()
            .EndRoomsWithExpiredHostGraceAsync(DateTime.UtcNow.AddSeconds(-90));

        Assert.Single(ended);
        Assert.Equal(room.Id, ended[0]);
        Assert.Equal(RoomStatus.Ended, room.Status);
        // Without this the dashboard cannot tell an abandoned session from a finished one.
        Assert.Equal(RoomEndReasons.HostDisconnected, capturedEndReason);
    }

    [Fact]
    public async Task ExpiredGrace_ClearsTheTimestamp_SoAnEndedRoomIsNotSweptTwice()
    {
        var hostId = Guid.NewGuid();
        var room = LiveRoom(hostId, DateTime.UtcNow.AddSeconds(-120));

        _roomRepo.Setup(r => r.GetRoomsWithExpiredHostGraceAsync(It.IsAny<DateTime>()))
            .ReturnsAsync(new List<Room> { room });
        _roomRepo.Setup(r => r.GetByIdAsync(room.Id)).ReturnsAsync(room);
        _roomRepo.Setup(r => r.GetRoomParticipantsAsync(room.Id))
            .ReturnsAsync(new List<RoomParticipant>());

        await CreateService().EndRoomsWithExpiredHostGraceAsync(DateTime.UtcNow.AddSeconds(-90));

        Assert.Null(room.HostDisconnectedAt);
    }

    [Fact]
    public async Task ExpiredGrace_EndsNothing_WhenNoWindowHasExpired()
    {
        _roomRepo.Setup(r => r.GetRoomsWithExpiredHostGraceAsync(It.IsAny<DateTime>()))
            .ReturnsAsync(new List<Room>());

        var ended = await CreateService()
            .EndRoomsWithExpiredHostGraceAsync(DateTime.UtcNow.AddSeconds(-90));

        Assert.Empty(ended);
    }

    // ── A deliberate end is still immediate and still labelled as such ───────────

    [Fact]
    public async Task HostEndingDeliberately_IsStillRecordedAsHostEnded()
    {
        var hostId = Guid.NewGuid();
        var room = LiveRoom(hostId);
        _roomRepo.Setup(r => r.GetByIdAsync(room.Id)).ReturnsAsync(room);
        _roomRepo.Setup(r => r.GetRoomParticipantsAsync(room.Id))
            .ReturnsAsync(new List<RoomParticipant>());

        string? capturedEndReason = null;
        _eventTracker.Setup(t => t.Track(
                EventTypes.RoomEnded, It.IsAny<Guid?>(), It.IsAny<object>(),
                It.IsAny<string?>(), It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<byte>()))
            .Callback<string, Guid?, object?, string?, Guid?, Guid?, byte>((_, _, props, _, _, _, _) =>
                capturedEndReason = props?.GetType().GetProperty("endReason")?
                    .GetValue(props)?.ToString());

        var result = await CreateService().EndRoomAsync(room.Id, hostId);

        Assert.True(result.Succeeded);
        Assert.Equal(RoomStatus.Ended, room.Status);
        Assert.Equal(RoomEndReasons.HostEnded, capturedEndReason);
    }

    // ── Media teardown: ending the room must also stop the audio ────────────────

    [Fact]
    public async Task EndingARoom_TearsDownTheLiveKitRoom()
    {
        // Ending in the database and broadcasting RoomEnded only asks clients to leave.
        // Without this call a client that ignores the message keeps talking to everyone.
        var hostId = Guid.NewGuid();
        var room = LiveRoom(hostId);
        _roomRepo.Setup(r => r.GetByIdAsync(room.Id)).ReturnsAsync(room);
        _roomRepo.Setup(r => r.GetRoomParticipantsAsync(room.Id))
            .ReturnsAsync(new List<RoomParticipant>());

        await CreateService().EndRoomAsync(room.Id, hostId);

        _liveKit.Verify(l => l.CloseRoomAsync(room.Id), Times.Once);
    }

    [Fact]
    public async Task ExpiredGrace_AlsoTearsDownTheLiveKitRoom()
    {
        var hostId = Guid.NewGuid();
        var room = LiveRoom(hostId, DateTime.UtcNow.AddSeconds(-120));

        _roomRepo.Setup(r => r.GetRoomsWithExpiredHostGraceAsync(It.IsAny<DateTime>()))
            .ReturnsAsync(new List<Room> { room });
        _roomRepo.Setup(r => r.GetByIdAsync(room.Id)).ReturnsAsync(room);
        _roomRepo.Setup(r => r.GetRoomParticipantsAsync(room.Id))
            .ReturnsAsync(new List<RoomParticipant>());

        await CreateService().EndRoomsWithExpiredHostGraceAsync(DateTime.UtcNow.AddSeconds(-90));

        _liveKit.Verify(l => l.CloseRoomAsync(room.Id), Times.Once);
    }

    [Fact]
    public async Task EndingARoom_StillSucceeds_WhenLiveKitTeardownFails()
    {
        // The room IS ended. Failing here would tell the host their end did not work.
        var hostId = Guid.NewGuid();
        var room = LiveRoom(hostId);
        _roomRepo.Setup(r => r.GetByIdAsync(room.Id)).ReturnsAsync(room);
        _roomRepo.Setup(r => r.GetRoomParticipantsAsync(room.Id))
            .ReturnsAsync(new List<RoomParticipant>());
        _liveKit.Setup(l => l.CloseRoomAsync(room.Id))
            .ThrowsAsync(new Exception("LiveKit unreachable"));

        var result = await CreateService().EndRoomAsync(room.Id, hostId);

        Assert.True(result.Succeeded);
        Assert.Equal(RoomStatus.Ended, room.Status);
    }

    [Fact]
    public async Task ARejectedEnd_DoesNotTouchTheMediaServer()
    {
        // Someone who is not the host asking to end a room must not be able to cut the audio.
        var room = LiveRoom(Guid.NewGuid());
        _roomRepo.Setup(r => r.GetByIdAsync(room.Id)).ReturnsAsync(room);

        var result = await CreateService().EndRoomAsync(room.Id, Guid.NewGuid());

        Assert.False(result.Succeeded);
        _liveKit.Verify(l => l.CloseRoomAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task EndingAnAlreadyEndedRoom_DoesNotTearDownTwice()
    {
        var hostId = Guid.NewGuid();
        var room = LiveRoom(hostId);
        room.Status = RoomStatus.Ended;
        _roomRepo.Setup(r => r.GetByIdAsync(room.Id)).ReturnsAsync(room);

        var result = await CreateService().EndRoomAsync(room.Id, hostId);

        Assert.False(result.Succeeded);
        _liveKit.Verify(l => l.CloseRoomAsync(It.IsAny<Guid>()), Times.Never);
    }

    // ── The sweep loop ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Sweep_TellsTheParticipantsOfEveryRoomItEnds()
    {
        var endedRoomIds = new[] { Guid.NewGuid(), Guid.NewGuid() };

        var roomService = new Mock<IRoomService>();
        roomService.Setup(s => s.EndRoomsWithExpiredHostGraceAsync(It.IsAny<DateTime>()))
            .ReturnsAsync(endedRoomIds);

        var notifier = new Mock<IRealTimeNotifier>();

        var services = new ServiceCollection();
        services.AddScoped(_ => roomService.Object);
        services.AddScoped(_ => notifier.Object);

        var sweeper = new HostReconnectGraceService(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            new Mock<ILogger<HostReconnectGraceService>>().Object,
            Options.Create(new RoomLifecycleSettings()));

        var count = await sweeper.SweepAsync(TimeSpan.FromSeconds(90));

        Assert.Equal(2, count);
        foreach (var roomId in endedRoomIds)
        {
            notifier.Verify(n => n.RoomEndedAsync(roomId, It.IsAny<string>()), Times.Once);
        }
    }

    [Fact]
    public void GraceWindow_OutlastsTheSignalRReconnectLadder()
    {
        // SignalR's default automatic-reconnect retries at 0, 2, 10 and 30 seconds, so its
        // last attempt begins at 42. A window shorter than that would end the room while the
        // client is still legitimately trying to come back.
        Assert.True(new RoomLifecycleSettings().HostReconnectGraceSeconds > 42);
    }
}
