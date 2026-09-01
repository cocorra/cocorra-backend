using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Cocorra.DAL.Data;
using Cocorra.DAL.DTOS.AnalyticsDto;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;
using Cocorra.DAL.Repository.AnalyticsRepository;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cocorra.Tests
{
    public class QueryCorrectionsTests : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly IServiceProvider _serviceProvider;

        public QueryCorrectionsTests()
        {
            _connection = new SqliteConnection("DataSource=:memory:");
            _connection.Open();

            var services = new ServiceCollection();
            services.AddDbContext<AppDbContext>(options => options.UseSqlite(_connection));
            _serviceProvider = services.BuildServiceProvider();

            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Database.EnsureCreated();
        }

        public void Dispose()
        {
            _connection.Dispose();
        }

        [Fact]
        public async Task Test4_HostExclusion_HostExcludedFromParticipationAndActivePassiveInOwnRoom()
        {
            var hostUser = new ApplicationUser { Id = Guid.NewGuid(), UserName = "host", FirstName = "Host", LastName = "User" };
            var listenerUser = new ApplicationUser { Id = Guid.NewGuid(), UserName = "listener", FirstName = "List", LastName = "User" };
            var speakerUser = new ApplicationUser { Id = Guid.NewGuid(), UserName = "speaker", FirstName = "Spk", LastName = "User" };

            var room = new Room
            {
                Id = Guid.NewGuid(),
                HostId = hostUser.Id,
                RoomTitle = "Host Test Room",
                Status = RoomStatus.Live,
                CreatedAt = DateTime.UtcNow
            };

            using (var scope = _serviceProvider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.Users.AddRange(hostUser, listenerUser, speakerUser);
                db.Rooms.Add(room);

                // Participants in room:
                // 1. Host (even with TotalSpokenSeconds > 0)
                // 2. Listener (TotalSpokenSeconds = 0)
                // 3. Speaker (TotalSpokenSeconds = 120)
                db.RoomParticipants.AddRange(
                    new RoomParticipant { RoomId = room.Id, UserId = hostUser.Id, JoinedAt = DateTime.UtcNow, TotalSpokenSeconds = 3600, Status = ParticipantStatus.Active },
                    new RoomParticipant { RoomId = room.Id, UserId = listenerUser.Id, JoinedAt = DateTime.UtcNow, TotalSpokenSeconds = 0, Status = ParticipantStatus.Active },
                    new RoomParticipant { RoomId = room.Id, UserId = speakerUser.Id, JoinedAt = DateTime.UtcNow, TotalSpokenSeconds = 120, Status = ParticipantStatus.Active }
                );

                // UserEvents in room:
                db.UserEvents.AddRange(
                    new UserEvent { EventId = Guid.NewGuid(), RoomId = room.Id, UserId = hostUser.Id, EventType = EventTypes.RoomJoined, OccurredAtUtc = DateTime.UtcNow },
                    new UserEvent { EventId = Guid.NewGuid(), RoomId = room.Id, UserId = listenerUser.Id, EventType = EventTypes.RoomJoined, OccurredAtUtc = DateTime.UtcNow },
                    new UserEvent { EventId = Guid.NewGuid(), RoomId = room.Id, UserId = speakerUser.Id, EventType = EventTypes.RoomJoined, OccurredAtUtc = DateTime.UtcNow },
                    new UserEvent { EventId = Guid.NewGuid(), RoomId = room.Id, UserId = speakerUser.Id, EventType = EventTypes.MicActivated, OccurredAtUtc = DateTime.UtcNow }
                );

                await db.SaveChangesAsync();
            }

            using (var scope = _serviceProvider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var repo = new AnalyticsRepository(db);

                var from = DateTime.UtcNow.AddHours(-1);
                var to = DateTime.UtcNow.AddHours(1);

                // Act
                var participation = await repo.GetParticipationStatsAsync(from, to);
                var activePassive = await repo.GetActiveVsPassiveRateAsync(from, to);

                // Assert Host Exclusion
                // Non-host participants = 2 (listener + speaker)
                Assert.Equal(2, participation.TotalParticipations);
                Assert.Equal(1, participation.UsersWhoSpoke); // Only speakerUser

                // AN-005: TopSpeakers and UsersWhoRaisedHand are gone from the contract, not
                // zeroed. Asserted on the type so a future re-add fails here rather than
                // quietly reintroducing "0 users raised their hand".
                Assert.Null(typeof(ParticipationStatsDto).GetProperty("TopSpeakers"));
                Assert.Null(typeof(ParticipationStatsDto).GetProperty("UsersWhoRaisedHand"));

                Assert.Equal(2, activePassive.TotalParticipants);
                Assert.Equal(1, activePassive.ActiveSpeakers);
                Assert.Equal(1, activePassive.PassiveListeners);
                Assert.Equal(50.0, activePassive.ActiveRate);
            }
        }

        [Fact]
        public async Task Test6_SequentialFunnel_EnforcesMonotonicity()
        {
            var user1 = Guid.NewGuid();
            var user2 = Guid.NewGuid();
            var user3 = Guid.NewGuid();

            var t0 = DateTime.UtcNow.AddMinutes(-30);
            var t1 = DateTime.UtcNow.AddMinutes(-20);
            var t2 = DateTime.UtcNow.AddMinutes(-10);

            using (var scope = _serviceProvider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                // Seed users first for Foreign Key constraints in SQLite
                db.Users.AddRange(
                    new ApplicationUser { Id = user1, UserName = "u1", FirstName = "U1", LastName = "User" },
                    new ApplicationUser { Id = user2, UserName = "u2", FirstName = "U2", LastName = "User" },
                    new ApplicationUser { Id = user3, UserName = "u3", FirstName = "U3", LastName = "User" }
                );

                // User 1: completes Step 0 -> Step 1 -> Step 2 in order
                db.UserEvents.AddRange(
                    new UserEvent { EventId = Guid.NewGuid(), UserId = user1, EventType = "step_register", OccurredAtUtc = t0 },
                    new UserEvent { EventId = Guid.NewGuid(), UserId = user1, EventType = "step_verify", OccurredAtUtc = t1 },
                    new UserEvent { EventId = Guid.NewGuid(), UserId = user1, EventType = "step_join_room", OccurredAtUtc = t2 }
                );

                // User 2: completes Step 0 -> Step 1, but NOT Step 2
                db.UserEvents.AddRange(
                    new UserEvent { EventId = Guid.NewGuid(), UserId = user2, EventType = "step_register", OccurredAtUtc = t0 },
                    new UserEvent { EventId = Guid.NewGuid(), UserId = user2, EventType = "step_verify", OccurredAtUtc = t1 }
                );

                // User 3: has Step 2 event WITHOUT Step 0 or Step 1 (out of order / rogue client event)
                db.UserEvents.Add(
                    new UserEvent { EventId = Guid.NewGuid(), UserId = user3, EventType = "step_join_room", OccurredAtUtc = t0 }
                );

                await db.SaveChangesAsync();
            }

            using (var scope = _serviceProvider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var repo = new AnalyticsRepository(db);

                var steps = new[] { "step_register", "step_verify", "step_join_room" };
                var funnel = await repo.GetFunnelAsync(steps, DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(1));

                // Step 0: User 1 and User 2 = 2
                // Step 1: User 1 and User 2 = 2
                // Step 2: Only User 1 = 1 (User 3 excluded because Step 0 & 1 were missing)
                Assert.Equal(2, funnel["step_register"]);
                Assert.Equal(2, funnel["step_verify"]);
                Assert.Equal(1, funnel["step_join_room"]);

                // Assert Monotonicity: Count(N) <= Count(N-1)
                Assert.True(funnel["step_verify"] <= funnel["step_register"]);
                Assert.True(funnel["step_join_room"] <= funnel["step_verify"]);
            }
        }
    }
}
