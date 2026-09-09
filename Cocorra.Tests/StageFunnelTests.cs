using System;
using System.Linq;
using System.Threading.Tasks;
using Cocorra.BLL.Services.Analytics;
using Cocorra.DAL.Data;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;
using Cocorra.DAL.Repository.AnalyticsRepository;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cocorra.Tests
{
    /// <summary>
    /// AN-027 / M-400 — stage participation funnel.
    ///
    /// The tests that matter most here are the ones about ABSENCE: an uninstrumented step must
    /// return null, and every step after it must also return null. Returning 0 would be a
    /// fabricated finding ("nobody raised their hand") and it is what most implementations do
    /// by default, which is exactly why it is asserted rather than assumed.
    /// </summary>
    public class StageFunnelTests : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly IServiceProvider _serviceProvider;

        private readonly Guid _hostId = Guid.NewGuid();
        private readonly Guid _roomId = Guid.NewGuid();
        private readonly DateTime _base = new(2026, 6, 1, 10, 0, 0, DateTimeKind.Utc);

        private DateTime From => _base.AddDays(-1);
        private DateTime To => _base.AddDays(1);

        public StageFunnelTests()
        {
            _connection = new SqliteConnection("DataSource=:memory:");
            _connection.Open();

            var services = new ServiceCollection();
            services.AddDbContext<AppDbContext>(options => options.UseSqlite(_connection));
            _serviceProvider = services.BuildServiceProvider();

            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Database.EnsureCreated();

            db.Users.Add(new ApplicationUser { Id = _hostId, UserName = "host", FirstName = "H", LastName = "U" });
            db.Rooms.Add(new Room
            {
                Id = _roomId,
                HostId = _hostId,
                RoomTitle = "Stage Funnel Room",
                Status = RoomStatus.Live,
                CreatedAt = _base
            });
            db.SaveChanges();
        }

        public void Dispose() => _connection.Dispose();

        // ── Fixture helpers ──────────────────────────────────────────────────

        private Guid AddUser(string name)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var id = Guid.NewGuid();
            db.Users.Add(new ApplicationUser { Id = id, UserName = name, FirstName = name, LastName = "U" });
            db.SaveChanges();
            return id;
        }

        private void AddEvent(Guid userId, string eventType, int minuteOffset, Guid? roomId = null)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.UserEvents.Add(new UserEvent
            {
                EventId = Guid.NewGuid(),
                UserId = userId,
                RoomId = roomId ?? _roomId,
                EventType = eventType,
                OccurredAtUtc = _base.AddMinutes(minuteOffset)
            });
            db.SaveChanges();
        }

        private async Task<DAL.DTOS.AnalyticsDto.StageFunnelDto> RunAsync()
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var repo = new AnalyticsRepository(db);
            return await repo.GetStageFunnelAsync(From, To);
        }

        /// <summary>
        /// Emits one of each step event well before the window opens.
        ///
        /// This is what production looks like: room_joined has been instrumented since launch,
        /// so its first-ever occurrence predates any recent query window. Without this the
        /// fixture would report every step as "instrumentation started mid-window", which is a
        /// true statement about an empty database and a false one about Cocorra.
        /// </summary>
        private void SeedPriorInstrumentation()
        {
            var priorUser = AddUser("pre-window-baseline");
            foreach (var eventType in new[]
                     {
                         EventTypes.RoomJoined, EventTypes.HandRaised,
                         EventTypes.StagePromoted, EventTypes.MicActivated
                     })
            {
                // Two days before _base, i.e. a full day before `From`, so these rows establish
                // instrumentation history without entering the funnel's window.
                AddEvent(priorUser, eventType, -60 * 48);
            }
        }

        /// <summary>Walks one participant through all four steps, five minutes apart.</summary>
        private Guid AddCompleteJourney(string name, int startOffset = 0)
        {
            var id = AddUser(name);
            AddEvent(id, EventTypes.RoomJoined, startOffset);
            AddEvent(id, EventTypes.HandRaised, startOffset + 5);
            AddEvent(id, EventTypes.StagePromoted, startOffset + 10);
            AddEvent(id, EventTypes.MicActivated, startOffset + 15);
            return id;
        }

        // ── Core funnel behaviour ────────────────────────────────────────────

        [Fact]
        public async Task FullyInstrumentedFunnel_CountsEveryStepAndReportsFullInstrumentation()
        {
            SeedPriorInstrumentation();
            AddCompleteJourney("complete-a");
            AddCompleteJourney("complete-b", 30);

            var result = await RunAsync();
            var steps = result.Steps.ToList();

            Assert.Equal(4, steps.Count);
            Assert.True(result.IsFullyInstrumented);
            Assert.Equal(1, result.RoomsInScope);

            Assert.All(steps, s =>
            {
                Assert.True(s.IsMeasured);
                Assert.Equal(2, s.Count);
                Assert.Null(s.NotMeasuredReason);
            });

            Assert.Equal(100, steps[3].ConversionFromFirstStepPercent);
        }

        [Fact]
        public async Task PartialCompletion_NarrowsAtTheStepWhereParticipantsStop()
        {
            AddCompleteJourney("full");

            // Joins and raises, never promoted.
            var stalled = AddUser("stalled");
            AddEvent(stalled, EventTypes.RoomJoined, 0);
            AddEvent(stalled, EventTypes.HandRaised, 5);

            // Joins only.
            var lurker = AddUser("lurker");
            AddEvent(lurker, EventTypes.RoomJoined, 0);

            var steps = (await RunAsync()).Steps.ToList();

            Assert.Equal(3, steps[0].Count);   // joined
            Assert.Equal(2, steps[1].Count);   // raised
            Assert.Equal(1, steps[2].Count);   // promoted
            Assert.Equal(1, steps[3].Count);   // mic

            Assert.Equal(1, steps[1].DropOffFromPreviousStep);
            Assert.Equal(50.0, steps[2].DropOffFromPreviousStepPercent);
        }

        [Fact]
        public async Task Monotonicity_HoldsForEveryStep()
        {
            AddCompleteJourney("a");
            AddCompleteJourney("b", 20);

            // Someone who unmutes without ever being promoted — must not inflate step 4.
            var rogue = AddUser("rogue");
            AddEvent(rogue, EventTypes.RoomJoined, 0);
            AddEvent(rogue, EventTypes.MicActivated, 2);

            var steps = (await RunAsync()).Steps.ToList();

            for (var i = 1; i < steps.Count; i++)
            {
                Assert.True(steps[i].Count <= steps[i - 1].Count,
                    $"Step '{steps[i].Step}' ({steps[i].Count}) exceeds '{steps[i - 1].Step}' ({steps[i - 1].Count}).");
            }

            Assert.Equal(3, steps[0].Count);
            Assert.Equal(2, steps[3].Count);
        }

        [Fact]
        public async Task MicActivatedWithoutPriorPromotion_DoesNotCountAtStepFour()
        {
            var user = AddUser("out-of-order");
            AddEvent(user, EventTypes.RoomJoined, 0);
            AddEvent(user, EventTypes.HandRaised, 5);
            // Mic BEFORE the promotion — the ordering constraint must reject it.
            AddEvent(user, EventTypes.MicActivated, 8);
            AddEvent(user, EventTypes.StagePromoted, 10);

            var steps = (await RunAsync()).Steps.ToList();

            Assert.Equal(1, steps[2].Count);
            Assert.Equal(0, steps[3].Count);
        }

        // ── Duplicates, repeats and unique users ─────────────────────────────

        [Fact]
        public async Task RepeatedHandRaises_CountOnceInFunnelAndTwiceInTotalEvents()
        {
            var user = AddUser("repeat-raiser");
            AddEvent(user, EventTypes.RoomJoined, 0);
            AddEvent(user, EventTypes.HandRaised, 5);
            AddEvent(user, EventTypes.HandRaised, 9);   // raise → lower → raise
            AddEvent(user, EventTypes.StagePromoted, 12);
            AddEvent(user, EventTypes.MicActivated, 15);

            var steps = (await RunAsync()).Steps.ToList();

            Assert.Equal(1, steps[1].Count);            // one participant reached the step
            Assert.Equal(1, steps[1].ObservedParticipations);
            Assert.Equal(2, steps[1].TotalEvents);      // two distinct asks for the stage
            Assert.Equal(1, steps[3].Count);
        }

        [Fact]
        public async Task SameUserInTwoRooms_CountsAsTwoParticipationsAndOneDistinctUser()
        {
            var secondRoomId = Guid.NewGuid();
            using (var scope = _serviceProvider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.Rooms.Add(new Room
                {
                    Id = secondRoomId,
                    HostId = _hostId,
                    RoomTitle = "Second Room",
                    Status = RoomStatus.Live,
                    CreatedAt = _base
                });
                db.SaveChanges();
            }

            var user = AddUser("two-rooms");
            AddEvent(user, EventTypes.RoomJoined, 0);
            AddEvent(user, EventTypes.RoomJoined, 20, secondRoomId);

            var result = await RunAsync();
            var steps = result.Steps.ToList();

            Assert.Equal(2, steps[0].Count);          // two participations
            Assert.Equal(1, steps[0].DistinctUsers);  // one person
            Assert.Equal(2, result.RoomsInScope);
        }

        // ── Instrumentation gaps: the assertions that matter most ────────────

        [Fact]
        public async Task UninstrumentedStep_ReturnsNullNotZero_AndSaysWhy()
        {
            // Only the two always-on events exist. hand_raised and stage_promoted are what the
            // flags gate, so this is the real production shape while both flags are off.
            var user = AddUser("flags-off");
            AddEvent(user, EventTypes.RoomJoined, 0);
            AddEvent(user, EventTypes.MicActivated, 10);

            var result = await RunAsync();
            var steps = result.Steps.ToList();

            Assert.False(result.IsFullyInstrumented);

            Assert.True(steps[0].IsMeasured);
            Assert.Equal(1, steps[0].Count);

            // hand_raised was never recorded.
            Assert.False(steps[1].IsMeasured);
            Assert.Null(steps[1].Count);
            Assert.Null(steps[1].ObservedParticipations);
            Assert.NotNull(steps[1].NotMeasuredReason);
            Assert.Contains("hand_raised", steps[1].NotMeasuredReason!);

            // stage_promoted was never recorded either.
            Assert.False(steps[2].IsMeasured);
            Assert.Null(steps[2].Count);

            // mic_activated IS instrumented, but its sequential position is unknowable through
            // two missing steps, so the funnel count is null while the observed count is real.
            Assert.True(steps[3].IsMeasured);
            Assert.Null(steps[3].Count);
            Assert.Equal(1, steps[3].ObservedParticipations);
            Assert.NotNull(steps[3].NotMeasuredReason);
        }

        [Fact]
        public async Task NoDataAtAll_ReturnsNullCountsRatherThanAFunnelOfZeros()
        {
            var result = await RunAsync();
            var steps = result.Steps.ToList();

            Assert.False(result.IsFullyInstrumented);
            Assert.Null(result.DataAvailableFromUtc);
            Assert.All(steps, s =>
            {
                Assert.False(s.IsMeasured);
                Assert.Null(s.Count);
                Assert.NotNull(s.NotMeasuredReason);
            });
        }

        [Fact]
        public async Task StepThatStartedMidWindow_IsFlaggedPartiallyMeasuredWithABoundary()
        {
            var user = AddCompleteJourney("mid-window");

            var result = await RunAsync();
            var handRaised = result.Steps.First(s => s.EventType == EventTypes.HandRaised);

            Assert.True(handRaised.IsMeasured);
            Assert.True(handRaised.IsPartiallyMeasured);
            Assert.Equal(_base.AddMinutes(5), handRaised.MeasurementStartedUtc);
            Assert.False(result.IsFullyInstrumented);
            Assert.NotEqual(Guid.Empty, user);
        }

        // ── Population and boundaries ────────────────────────────────────────

        [Fact]
        public async Task RoomHost_IsExcludedFromEveryStep()
        {
            AddEvent(_hostId, EventTypes.RoomJoined, 0);
            AddEvent(_hostId, EventTypes.HandRaised, 1);
            AddEvent(_hostId, EventTypes.StagePromoted, 2);
            AddEvent(_hostId, EventTypes.MicActivated, 3);

            AddCompleteJourney("real-participant");

            var steps = (await RunAsync()).Steps.ToList();

            Assert.All(steps, s => Assert.Equal(1, s.Count));
        }

        [Fact]
        public async Task EventsOutsideTheWindow_AreExcluded()
        {
            AddCompleteJourney("inside");

            var outside = AddUser("outside");
            AddEvent(outside, EventTypes.RoomJoined, -60 * 48);   // two days before `From`

            var steps = (await RunAsync()).Steps.ToList();

            Assert.Equal(1, steps[0].Count);
        }

        [Fact]
        public async Task EventsWithNoRoomContext_AreExcluded()
        {
            AddCompleteJourney("with-room");

            using (var scope = _serviceProvider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.UserEvents.Add(new UserEvent
                {
                    EventId = Guid.NewGuid(),
                    UserId = AddUser("no-room"),
                    RoomId = null,
                    EventType = EventTypes.RoomJoined,
                    OccurredAtUtc = _base
                });
                db.SaveChanges();
            }

            var steps = (await RunAsync()).Steps.ToList();

            Assert.Equal(1, steps[0].Count);
        }

        [Fact]
        public async Task DirectPromotionWithoutHandRaise_IsReportedSeparatelyNotSilentlyDropped()
        {
            AddCompleteJourney("via-hand-raise");

            // Host invites someone straight to the stage. Strict sequencing drops this at
            // step 2; without the side-channel count it would look like a promotion vanishing.
            var invited = AddUser("host-invited");
            AddEvent(invited, EventTypes.RoomJoined, 0);
            AddEvent(invited, EventTypes.StagePromoted, 10);
            AddEvent(invited, EventTypes.MicActivated, 12);

            var result = await RunAsync();
            var steps = result.Steps.ToList();

            Assert.Equal(2, steps[0].Count);
            Assert.Equal(1, steps[1].Count);
            Assert.Equal(1, steps[2].Count);
            Assert.Equal(1, result.DirectPromotionsWithoutHandRaise);
        }

        [Fact]
        public async Task DirectPromotionCount_IsNullWhenEitherEventIsUninstrumented()
        {
            var user = AddUser("joined-only");
            AddEvent(user, EventTypes.RoomJoined, 0);

            var result = await RunAsync();

            Assert.Null(result.DirectPromotionsWithoutHandRaise);
        }

        // ── Registry contract ────────────────────────────────────────────────

        [Fact]
        public void M400_IsRegisteredAsExperimentalWithTheNotMeasuredLimitation()
        {
            var contract = new MetricRegistry().GetContract(MetricRegistry.StageFunnel);

            Assert.NotNull(contract);
            Assert.Equal("M-400", contract!.MetricKey);
            Assert.Equal(MetricTrustLevel.Experimental, contract.TrustLevel);

            // The metric is only honest if the caveat travels with it.
            Assert.Contains(contract.Limitations, l => l.Contains("CANNOT BE BACKFILLED", StringComparison.Ordinal));
            Assert.Contains(contract.Limitations, l => l.Contains("never as 0", StringComparison.Ordinal));
            Assert.Contains(contract.Exclusions, e => e.Contains("Room hosts", StringComparison.Ordinal));
        }
    }
}
