using System.Threading.Channels;
using Cocorra.BLL.Services.EventTracking;
using Cocorra.DAL.Data;
using Cocorra.DAL.Models;
using Cocorra.DAL.Repository.AnalyticsRepository;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Cocorra.Tests;

/// <summary>
/// Smoke checks for the event-tracking path that powers the analytics dashboard:
///   EventTracker.Track → in-memory channel → EventFlushService → DB → AnalyticsRepository.
/// Verifies (a) roomId is promoted to the indexed RoomId column, (b) tracking is
/// fire-and-forget (never throws), and (c) the four core metrics compute correctly
/// off the persisted events.
/// </summary>
public class EventTrackingSmokeTests
{
    // ── Helpers ─────────────────────────────────────────────────────────────

    private static EventTracker BuildTracker(Channel<UserEvent> queue)
    {
        // No HttpContext (as when firing from a SignalR hub) → enrichment is skipped,
        // but userId/roomId still flow through explicitly.
        var httpAccessor = new Mock<IHttpContextAccessor>();
        httpAccessor.Setup(a => a.HttpContext).Returns((HttpContext?)null);

        var config = new Mock<IConfiguration>();
        config.Setup(c => c["Analytics:IpHashSalt"]).Returns("test-salt");

        return new EventTracker(queue, NullLogger<EventTracker>.Instance, httpAccessor.Object, config.Object);
    }

    private static Channel<UserEvent> NewQueue()
        => Channel.CreateBounded<UserEvent>(
            new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.DropWrite });

    private static UserEvent DrainOne(Channel<UserEvent> queue)
    {
        Assert.True(queue.Reader.TryRead(out var evt), "Expected an event to be enqueued.");
        return evt!;
    }

    // ── Enqueue + RoomId promotion ──────────────────────────────────────────

    [Fact]
    public void Track_PromotesLowercaseRoomId_ToIndexedColumn()
    {
        var queue = NewQueue();
        var tracker = BuildTracker(queue);
        var userId = Guid.NewGuid();
        var roomId = Guid.NewGuid();

        // Matches the real emit sites, e.g. RoomHub.JoinRoom: new { roomId = roomGuid }.
        tracker.Track(EventTypes.RoomJoined, userId, new { roomId });

        var evt = DrainOne(queue);
        Assert.Equal(EventTypes.RoomJoined, evt.EventType);
        Assert.Equal(userId, evt.UserId);
        Assert.Equal(roomId, evt.RoomId);
        Assert.Contains(roomId.ToString(), evt.PropertiesJson);
    }

    [Fact]
    public void Track_PromotesPascalCaseRoomId_CaseInsensitive()
    {
        var queue = NewQueue();
        var tracker = BuildTracker(queue);
        var roomId = Guid.NewGuid();

        // Guards the case-insensitive ExtractRoomId fix.
        tracker.Track(EventTypes.RoomCreated, Guid.NewGuid(), new { RoomId = roomId });

        Assert.Equal(roomId, DrainOne(queue).RoomId);
    }

    [Fact]
    public void Track_WithoutRoomId_LeavesColumnNull()
    {
        var queue = NewQueue();
        var tracker = BuildTracker(queue);

        tracker.Track(EventTypes.VoiceVerificationSubmitted, Guid.NewGuid());

        Assert.Null(DrainOne(queue).RoomId);
    }

    // ── Fire-and-forget guarantee ───────────────────────────────────────────

    [Fact]
    public void Track_MalformedProperties_DoesNotThrow_AndLeavesRoomIdNull()
    {
        var queue = NewQueue();
        var tracker = BuildTracker(queue);

        // A non-object payload can't yield a roomId; must not surface an exception.
        var ex = Record.Exception(() => tracker.Track(EventTypes.FeatureViewed, Guid.NewGuid(), 42));

        Assert.Null(ex);
        Assert.Null(DrainOne(queue).RoomId);
    }

    [Fact]
    public void Track_WhenQueueIsFull_DropsSilentlyWithoutThrowing()
    {
        // Capacity 1 + DropWrite: the second write is dropped, never throws.
        var queue = Channel.CreateBounded<UserEvent>(
            new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
        var tracker = BuildTracker(queue);

        var ex = Record.Exception(() =>
        {
            tracker.Track(EventTypes.RoomJoined, Guid.NewGuid(), new { roomId = Guid.NewGuid() });
            tracker.Track(EventTypes.RoomJoined, Guid.NewGuid(), new { roomId = Guid.NewGuid() });
        });

        Assert.Null(ex);
    }

    // ── Whole path: Track → FlushService → DB → AnalyticsRepository ──────────

    [Fact]
    public async Task WholePath_EventsPersist_AndCoreMetricsCompute()
    {
        var dbName = "events_smoke_" + Guid.NewGuid();
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(dbName));
        await using var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        var queue = NewQueue();
        var tracker = BuildTracker(queue);

        var roomA = Guid.NewGuid();
        var roomB = Guid.NewGuid();
        var user1 = Guid.NewGuid();
        var user2 = Guid.NewGuid();
        var user3 = Guid.NewGuid();

        // Scenario:
        //   room_joined: user1→A, user2→A, user3→B   → most active = A (2 joins, 2 unique)
        //   mic_activated: user1 in A                 → 3 joiners, 1 speaker → active rate ≈ 33.33%
        //   voice: 2 submitted, 1 completed           → drop-off 50%
        tracker.Track(EventTypes.RoomJoined, user1, new { roomId = roomA });
        tracker.Track(EventTypes.RoomJoined, user2, new { roomId = roomA });
        tracker.Track(EventTypes.RoomJoined, user3, new { roomId = roomB });
        tracker.Track(EventTypes.MicActivated, user1, new { roomId = roomA });
        tracker.Track(EventTypes.VoiceVerificationSubmitted, user1);
        tracker.Track(EventTypes.VoiceVerificationSubmitted, user2);
        tracker.Track(EventTypes.ActivationCompleted, user1);

        // Complete the writer so the flush loop drains the buffer and exits on its own;
        // awaiting ExecuteTask is deterministic (no cancellation race mid-save).
        queue.Writer.Complete();
        var flush = new EventFlushService(queue, scopeFactory, NullLogger<EventFlushService>.Instance);
        await flush.StartAsync(CancellationToken.None);
        await flush.ExecuteTask!;

        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(7, await db.UserEvents.CountAsync());
        Assert.Equal(4, await db.UserEvents.CountAsync(e => e.RoomId != null)); // 3 joins + 1 mic

        var repo = new AnalyticsRepository(db);
        var from = DateTime.UtcNow.AddDays(-1);
        var to = DateTime.UtcNow.AddDays(1);

        // 1. Most active room
        var activeRooms = await repo.GetMostActiveRoomsAsync(from, to);
        Assert.Equal(roomA, activeRooms.First().RoomId);
        Assert.Equal(2, activeRooms.First().JoinEvents);
        Assert.Equal(2, activeRooms.First().UniqueJoiners);

        // 2. Peak active hours — all 7 events land in one UTC hour bucket.
        var hours = await repo.GetPeakActiveHoursAsync(from, to);
        Assert.Equal(24, hours.Count); // 0–23 gap-filled
        Assert.Equal(7, hours.Sum(h => h.EventCount));

        // 3. Voice verification drop-off — 2 started, 1 completed.
        var voice = await repo.GetVoiceVerificationDropOffAsync(from, to);
        Assert.Equal(2, voice.Started);
        Assert.Equal(1, voice.Completed);
        Assert.Equal(50.0, voice.DropOffRate);

        // 4. Active vs passive — 3 joiners, 1 spoke.
        var mode = await repo.GetActiveVsPassiveRateAsync(from, to);
        Assert.Equal(3, mode.TotalParticipants);
        Assert.Equal(1, mode.ActiveSpeakers);
        Assert.Equal(2, mode.PassiveListeners);
        Assert.Equal(33.33, mode.ActiveRate);
    }
}
