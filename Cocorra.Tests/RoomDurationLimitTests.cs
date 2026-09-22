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
/// DurationHours was validated on creation — 2 or 3, nothing else — and then enforced by
/// nothing at all. A host who closed the app without ending their room left it Live forever:
/// still in the feed, still accepting joins, still minting LiveKit tokens. It is also the only
/// way a participant's token can expire mid-session, since the TTL is longer than any bookable
/// duration, so capping the room is what makes reducing that TTL safe.
///
/// The trap these tests exist to guard is StartDate: it is the *scheduled* start and is never
/// updated when a host starts late, so a deadline measured from it would have ended a
/// late-started room the instant it opened. The deadline runs from WentLiveAt instead.
/// </summary>
public class RoomDurationLimitTests
{
    private readonly Mock<IRoomRepository> _roomRepo = new();
    private readonly Mock<IEventTracker> _eventTracker = new();
    private readonly Mock<ILiveKitService> _liveKit = new();

    private static readonly TimeSpan Overtime = TimeSpan.FromMinutes(15);

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

    private Room LiveRoom(int durationHours, DateTime? wentLiveAt)
    {
        var room = new Room
        {
            Id = Guid.NewGuid(),
            HostId = Guid.NewGuid(),
            Status = RoomStatus.Live,
            DurationHours = durationHours,
            // Deliberately absurd: nothing may key off the scheduled start.
            StartDate = DateTime.UtcNow.AddDays(-3),
            WentLiveAt = wentLiveAt
        };

        _roomRepo.Setup(r => r.GetByIdAsync(room.Id)).ReturnsAsync(room);
        _roomRepo.Setup(r => r.GetRoomParticipantsAsync(room.Id))
            .ReturnsAsync(new List<RoomParticipant>());

        return room;
    }

    /// <summary>
    /// Stands in for the repository prefilter so the tests exercise the real per-room deadline
    /// arithmetic rather than a hand-picked candidate set.
    /// </summary>
    private void GivenLiveRooms(params Room[] rooms)
    {
        _roomRepo.Setup(r => r.GetLiveRoomsStartedBeforeAsync(It.IsAny<DateTime>()))
            .ReturnsAsync((DateTime before) => rooms
                .Where(r => r.WentLiveAt.HasValue && r.WentLiveAt < before)
                .OrderBy(r => r.WentLiveAt)
                .ToList());
    }

    // ── The deadline ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task EndsARoom_ThatHasRunPastItsDurationPlusOvertime()
    {
        var room = LiveRoom(durationHours: 2, wentLiveAt: DateTime.UtcNow.AddHours(-2.5));
        GivenLiveRooms(room);

        var ended = await CreateService()
            .EndRoomsPastScheduledDurationAsync(DateTime.UtcNow, Overtime);

        Assert.Equal(new[] { room.Id }, ended);
        Assert.Equal(RoomStatus.Ended, room.Status);
    }

    [Fact]
    public async Task LeavesARoom_StillInsideItsBookedDuration()
    {
        var room = LiveRoom(durationHours: 3, wentLiveAt: DateTime.UtcNow.AddHours(-2));
        GivenLiveRooms(room);

        var ended = await CreateService()
            .EndRoomsPastScheduledDurationAsync(DateTime.UtcNow, Overtime);

        Assert.Empty(ended);
        Assert.Equal(RoomStatus.Live, room.Status);
    }

    [Fact]
    public async Task LeavesARoom_InsideTheOvertimeAllowance()
    {
        // Ten minutes over a two-hour booking. Cutting a coaching session off at exactly the
        // two-hour mark would end it mid-sentence; the allowance is the wrap-up window.
        var room = LiveRoom(durationHours: 2, wentLiveAt: DateTime.UtcNow.AddHours(-2).AddMinutes(-10));
        GivenLiveRooms(room);

        var ended = await CreateService()
            .EndRoomsPastScheduledDurationAsync(DateTime.UtcNow, Overtime);

        Assert.Empty(ended);
        Assert.Equal(RoomStatus.Live, room.Status);
    }

    [Fact]
    public async Task AppliesEachRoomsOwnDuration_NotASharedCutoff()
    {
        // Both went live 2h20m ago. The 2h booking is overdue (2h15m deadline); the 3h one has
        // over half an hour left. A single cutoff for the whole sweep would end both.
        var wentLive = DateTime.UtcNow.AddHours(-2).AddMinutes(-20);
        var twoHour = LiveRoom(durationHours: 2, wentLiveAt: wentLive);
        var threeHour = LiveRoom(durationHours: 3, wentLiveAt: wentLive);
        GivenLiveRooms(twoHour, threeHour);

        var ended = await CreateService()
            .EndRoomsPastScheduledDurationAsync(DateTime.UtcNow, Overtime);

        Assert.Equal(new[] { twoHour.Id }, ended);
        Assert.Equal(RoomStatus.Ended, twoHour.Status);
        Assert.Equal(RoomStatus.Live, threeHour.Status);
    }

    // ── The StartDate trap ───────────────────────────────────────────────────────

    [Fact]
    public async Task DoesNotEndARoom_ThatStartedLongAfterItsScheduledTime()
    {
        // Scheduled three days ago (LiveRoom sets that for every room here), started a minute
        // ago. Measured from StartDate this room is wildly overdue; measured from when it
        // actually went live it has barely begun. This is the regression that matters most —
        // getting it wrong would kill sessions the moment a host opened them.
        var room = LiveRoom(durationHours: 2, wentLiveAt: DateTime.UtcNow.AddMinutes(-1));
        GivenLiveRooms(room);

        var ended = await CreateService()
            .EndRoomsPastScheduledDurationAsync(DateTime.UtcNow, Overtime);

        Assert.Empty(ended);
        Assert.Equal(RoomStatus.Live, room.Status);
    }

    [Fact]
    public async Task ExemptsRooms_ThatHaveNoGoLiveTimestamp()
    {
        // Rooms already Live when the column was added. Deliberately exempt rather than
        // backfilled from StartDate, because a backfill would have ended every late-started
        // room mid-session on deploy.
        var room = LiveRoom(durationHours: 2, wentLiveAt: null);
        GivenLiveRooms(room);

        var ended = await CreateService()
            .EndRoomsPastScheduledDurationAsync(DateTime.UtcNow, Overtime);

        Assert.Empty(ended);
        Assert.Equal(RoomStatus.Live, room.Status);
    }

    // ── Going live records the timestamp ─────────────────────────────────────────

    [Fact]
    public async Task StartingAScheduledRoom_RecordsWhenItActuallyWentLive()
    {
        var hostId = Guid.NewGuid();
        var room = new Room
        {
            Id = Guid.NewGuid(),
            HostId = hostId,
            Status = RoomStatus.Scheduled,
            DurationHours = 2,
            StartDate = DateTime.UtcNow.AddHours(-5), // the host is starting five hours late
            WentLiveAt = null
        };
        _roomRepo.Setup(r => r.GetByIdAsync(room.Id)).ReturnsAsync(room);
        _roomRepo.Setup(r => r.GetRoomParticipantsAsync(room.Id))
            .ReturnsAsync(new List<RoomParticipant>());
        _roomRepo.Setup(r => r.GetRemindersByRoomIdAsync(room.Id))
            .ReturnsAsync(new List<RoomReminder>());

        var before = DateTime.UtcNow;
        var result = await CreateService().StartScheduledRoomAsync(room.Id, hostId);

        Assert.True(result.Succeeded);
        Assert.NotNull(room.WentLiveAt);
        Assert.InRange(room.WentLiveAt!.Value, before, DateTime.UtcNow);

        // StartDate must survive untouched — the analytics distinguish scheduled from actual.
        Assert.True(room.StartDate < before.AddHours(-4));
    }

    // ── Teardown and attribution ─────────────────────────────────────────────────

    [Fact]
    public async Task EndingOnDuration_TearsDownTheLiveKitRoom()
    {
        var room = LiveRoom(durationHours: 2, wentLiveAt: DateTime.UtcNow.AddHours(-3));
        GivenLiveRooms(room);

        await CreateService().EndRoomsPastScheduledDurationAsync(DateTime.UtcNow, Overtime);

        // Otherwise the DB says ended while everyone is still audible on the bridge.
        _liveKit.Verify(l => l.CloseRoomAsync(room.Id), Times.Once);
    }

    [Fact]
    public async Task EndingOnDuration_IsAttributedDistinctlyFromAHostEnding()
    {
        var room = LiveRoom(durationHours: 2, wentLiveAt: DateTime.UtcNow.AddHours(-3));
        GivenLiveRooms(room);

        string? capturedReason = null;
        _eventTracker
            .Setup(t => t.Track(
                EventTypes.RoomEnded, It.IsAny<Guid?>(), It.IsAny<object?>(),
                It.IsAny<string?>(), It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<byte>()))
            .Callback<string, Guid?, object?, string?, Guid?, Guid?, byte>(
                (_, _, payload, _, _, _, _) =>
                {
                    capturedReason = payload?.GetType().GetProperty("endReason")?
                        .GetValue(payload) as string;
                });

        await CreateService().EndRoomsPastScheduledDurationAsync(DateTime.UtcNow, Overtime);

        // A host who never ends their own room is a different behaviour to one who does, and a
        // rising count here is a product signal, not a connectivity one.
        Assert.Equal(RoomEndReasons.DurationElapsed, capturedReason);
    }

    [Fact]
    public async Task NothingOverdue_EndsNothing()
    {
        GivenLiveRooms();

        var ended = await CreateService()
            .EndRoomsPastScheduledDurationAsync(DateTime.UtcNow, Overtime);

        Assert.Empty(ended);
        _liveKit.Verify(l => l.CloseRoomAsync(It.IsAny<Guid>()), Times.Never);
    }

    // ── The sweep loop ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Sweep_TellsTheParticipantsOfEveryRoomItEnds()
    {
        var endedRoomIds = new[] { Guid.NewGuid(), Guid.NewGuid() };

        var roomService = new Mock<IRoomService>();
        roomService.Setup(s => s.EndRoomsPastScheduledDurationAsync(
                It.IsAny<DateTime>(), It.IsAny<TimeSpan>()))
            .ReturnsAsync(endedRoomIds);

        var notifier = new Mock<IRealTimeNotifier>();

        var services = new ServiceCollection();
        services.AddScoped(_ => roomService.Object);
        services.AddScoped(_ => notifier.Object);

        var sweeper = new RoomDurationLimitService(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            new Mock<ILogger<RoomDurationLimitService>>().Object,
            Options.Create(new RoomLifecycleSettings()));

        var count = await sweeper.SweepAsync(Overtime);

        Assert.Equal(2, count);
        foreach (var roomId in endedRoomIds)
        {
            notifier.Verify(n => n.RoomEndedAsync(roomId, It.IsAny<string>()), Times.Once);
        }
    }

    [Fact]
    public void TheHardCeiling_StaysUnderTheLiveKitTokenTtl()
    {
        // The point of enforcing duration at all: a room that outlives a participant's token
        // leaves them unable to reconnect, with the client SDK re-presenting a credential the
        // server now rejects. Longest bookable room plus overtime must stay clear of the TTL.
        var settings = new RoomLifecycleSettings();
        var liveKit = new LiveKitSettings();

        var longestRoomMinutes = (3 * 60) + settings.RoomOvertimeGraceMinutes;

        Assert.True(
            longestRoomMinutes < liveKit.TokenTtlMinutes,
            $"A 3h room with {settings.RoomOvertimeGraceMinutes}m overtime runs for " +
            $"{longestRoomMinutes}m, which is not under the {liveKit.TokenTtlMinutes}m token TTL. " +
            "Either shorten the overtime allowance or raise the TTL — as configured, " +
            "participants can be unable to reconnect before the room ends.");
    }
}
