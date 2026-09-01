using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Cocorra.BLL.Services.Analytics;
using Cocorra.DAL.Data;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;
using Cocorra.DAL.Repository.AnalyticsRepository;
using Cocorra.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cocorra.Tests
{
    /// <summary>
    /// AN-006, AN-007 and AN-008: the replacement metrics.
    /// </summary>
    public class QueryCorrectionsReplacementTests : IDisposable
    {
        private readonly SqliteTestHost _host = new();

        public void Dispose() => _host.Dispose();

        private AnalyticsRepository CreateRepository(IServiceScope scope) =>
            new(scope.ServiceProvider.GetRequiredService<AppDbContext>());

        private static ApplicationUser User(Guid id, string name, UserStatus status = UserStatus.Active, DateTime? createdAt = null) => new()
        {
            Id = id,
            UserName = name,
            FirstName = name,
            LastName = "T",
            Status = status,
            CreatedAt = createdAt ?? DateTime.UtcNow.AddYears(-1)
        };

        private static UserEvent Event(string type, Guid userId, DateTime at, Guid? roomId = null, string? props = null) => new()
        {
            EventId = Guid.NewGuid(),
            EventType = type,
            UserId = userId,
            RoomId = roomId,
            PropertiesJson = props,
            OccurredAtUtc = at
        };

        // ── AN-006 / M-102 ──────────────────────────────────────────────────

        [Fact]
        public async Task WeeklyReturnRate_CountsAUserWhoJoinsInWeek1AndWeek3AsReturned()
        {
            var returner = Guid.NewGuid();
            var oneAndDone = Guid.NewGuid();
            var week1 = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc); // Monday
            var week3 = week1.AddDays(14);

            using (var scope = _host.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.Users.AddRange(User(returner, "returner"), User(oneAndDone, "oneshot"));
                db.UserEvents.AddRange(
                    Event(EventTypes.RoomJoined, returner, week1),
                    Event(EventTypes.RoomJoined, returner, week3),
                    Event(EventTypes.RoomJoined, oneAndDone, week1));
                await db.SaveChangesAsync();
            }

            using (var scope = _host.CreateScope())
            {
                var result = await CreateRepository(scope)
                    .GetWeeklyReturnRateAsync(week1.AddDays(-7), week3.AddDays(7));

                var firstCohort = result.Cohorts.First(c => c.WeekStartUtc == week1.Date);

                Assert.Equal(2, firstCohort.CohortSize);
                Assert.Equal(1, firstCohort.ReturnedInLaterWeek);
                Assert.Equal(50.0, firstCohort.ReturnRatePercent);
                Assert.True(firstCohort.IsComplete);

                // The final cohort has no later week yet, so its 0% is an artefact of the
                // window and must be labelled incomplete rather than charted as a collapse.
                Assert.False(result.Cohorts.Last().IsComplete);
                Assert.NotNull(result.DataAvailableFromUtc);
            }
        }

        [Fact]
        public async Task WeeklyReturnRate_ReadsNoSessionStartedRows_AndExcludesHosts()
        {
            var host = Guid.NewGuid();
            var listener = Guid.NewGuid();
            var roomId = Guid.NewGuid();
            var week1 = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

            using (var scope = _host.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.Users.AddRange(User(host, "host"), User(listener, "listener"));
                db.Rooms.Add(new Room
                {
                    Id = roomId,
                    HostId = host,
                    RoomTitle = "R",
                    Category = RoomCategory.Others,
                    StartDate = week1,
                    CreatedAt = week1
                });

                // The host joins their own room in both weeks; only the listener should count.
                db.UserEvents.AddRange(
                    Event(EventTypes.RoomJoined, host, week1, roomId),
                    Event(EventTypes.RoomJoined, host, week1.AddDays(7), roomId),
                    Event(EventTypes.RoomJoined, listener, week1, roomId),
                    Event(EventTypes.RoomJoined, listener, week1.AddDays(7), roomId),
                    // session_started must be irrelevant to the result.
                    Event("session_started", listener, week1.AddDays(1)),
                    Event("session_started", host, week1.AddDays(1)));
                await db.SaveChangesAsync();
            }

            using (var scope = _host.CreateScope())
            {
                var result = await CreateRepository(scope)
                    .GetWeeklyReturnRateAsync(week1.AddDays(-7), week1.AddDays(14));

                var firstCohort = result.Cohorts.First(c => c.WeekStartUtc == week1.Date);

                Assert.Equal(1, firstCohort.CohortSize);
                Assert.Equal(1, firstCohort.ReturnedInLaterWeek);
            }
        }

        // ── AN-007 / M-507 ──────────────────────────────────────────────────

        [Fact]
        public async Task ActivationFunnel_IsMonotonic_AndReportsMedianElapsedTime()
        {
            var t0 = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
            var users = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToList();

            using (var scope = _host.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.Users.AddRange(users.Select((id, i) => User(id, $"u{i}")));

                // All three register. Two submit, at +100s and +300s. One completes.
                foreach (var id in users)
                {
                    db.UserEvents.Add(Event(EventTypes.UserRegistered, id, t0));
                }
                db.UserEvents.Add(Event(EventTypes.VoiceVerificationSubmitted, users[0], t0.AddSeconds(100)));
                db.UserEvents.Add(Event(EventTypes.VoiceVerificationSubmitted, users[1], t0.AddSeconds(300)));
                db.UserEvents.Add(Event(EventTypes.ActivationCompleted, users[0], t0.AddSeconds(400)));
                await db.SaveChangesAsync();
            }

            using (var scope = _host.CreateScope())
            {
                var steps = new[]
                {
                    EventTypes.UserRegistered,
                    EventTypes.VoiceVerificationSubmitted,
                    EventTypes.ActivationCompleted
                };

                var result = await CreateRepository(scope)
                    .GetActivationFunnelAsync(steps, t0.AddDays(-1), t0.AddDays(1));

                var ordered = result.Steps.ToList();
                Assert.Equal(3, ordered[0].Count);
                Assert.Equal(2, ordered[1].Count);
                Assert.Equal(1, ordered[2].Count);

                // Monotonicity for any input, which the independent-count version could not hold.
                for (var i = 1; i < ordered.Count; i++)
                {
                    Assert.True(ordered[i].Count <= ordered[i - 1].Count);
                }

                // Median of {100, 300} = 200. A mean would say the same here; the point is that
                // the value exists at all, so an 18-hour review wait is distinguishable from an
                // instant drop-off.
                Assert.Equal(200, ordered[1].MedianSecondsFromPreviousStep);
                Assert.Equal(300, ordered[2].MedianSecondsFromPreviousStep);

                // No user made this transition, so null rather than 0: "not measured" is not
                // the same statement as "took no time".
                Assert.Null(ordered[0].MedianSecondsFromPreviousStep);
                Assert.NotNull(result.DataAvailableFromUtc);
            }
        }

        [Fact]
        public async Task ActivationFunnel_ExcludesOutOfOrderCompletions()
        {
            var t0 = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
            var user = Guid.NewGuid();

            using (var scope = _host.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.Users.Add(User(user, "u"));
                // activation_completed BEFORE the submission it supposedly follows.
                db.UserEvents.AddRange(
                    Event(EventTypes.UserRegistered, user, t0),
                    Event(EventTypes.ActivationCompleted, user, t0.AddSeconds(10)),
                    Event(EventTypes.VoiceVerificationSubmitted, user, t0.AddSeconds(50)));
                await db.SaveChangesAsync();
            }

            using (var scope = _host.CreateScope())
            {
                var result = await CreateRepository(scope).GetActivationFunnelAsync(
                    new[] { EventTypes.UserRegistered, EventTypes.VoiceVerificationSubmitted, EventTypes.ActivationCompleted },
                    t0.AddDays(-1), t0.AddDays(1));

                var ordered = result.Steps.ToList();
                Assert.Equal(1, ordered[0].Count);
                Assert.Equal(1, ordered[1].Count);
                Assert.Equal(0, ordered[2].Count); // out of order, so it does not count
            }
        }

        // ── AN-008 / M-501 + M-502 ──────────────────────────────────────────

        [Fact]
        public async Task UserGrowth_DoesNotProjectTodaysStatusOntoPastBuckets()
        {
            // The defect: a user who registered in month 1 and was banned in month 3 was
            // counted as banned in month 1, rewriting history.
            var user = Guid.NewGuid();
            var month1 = new DateTime(2026, 4, 10, 0, 0, 0, DateTimeKind.Utc);
            var month3 = new DateTime(2026, 6, 10, 0, 0, 0, DateTimeKind.Utc);

            using (var scope = _host.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                // Current status is Banned — what the old query would have shown in month 1.
                db.Users.Add(User(user, "u", UserStatus.Banned, month1));
                db.UserEvents.AddRange(
                    Event(EventTypes.VoiceVerificationResult, user, month1.AddDays(1), props: "{\"status\":\"Active\"}"),
                    Event(EventTypes.VoiceVerificationResult, user, month3, props: "{\"status\":\"Banned\"}"));
                await db.SaveChangesAsync();
            }

            using (var scope = _host.CreateScope())
            {
                var result = await CreateRepository(scope)
                    .GetUserGrowthAsync("monthly", month1.AddDays(-30), month3.AddDays(20));

                // Registration count is unchanged by the correction.
                Assert.Equal(1, result.TotalUsersInPeriod);
                Assert.Equal(1, result.DataPoints.Single(d => d.Period == "2026-04").NewUsers);

                var april = result.StatusAtTime.Single(s => s.Period == "2026-04");
                var june = result.StatusAtTime.Single(s => s.Period == "2026-06");

                Assert.Equal(1, april.ActiveUsers);
                Assert.Equal(0, april.BannedUsers);
                Assert.Equal(0, june.ActiveUsers);
                Assert.Equal(1, june.BannedUsers);

                Assert.NotNull(result.StatusHistoryAvailableFromUtc);
            }
        }

        [Fact]
        public async Task UserGrowth_ReturnsNoStatusSeriesWhenNoStatusEventsExist()
        {
            // An uninstrumented period must render as a gap, never as "everyone was Pending".
            using (var scope = _host.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.Users.Add(User(Guid.NewGuid(), "u", UserStatus.Active, new DateTime(2026, 4, 10, 0, 0, 0, DateTimeKind.Utc)));
                await db.SaveChangesAsync();
            }

            using (var scope = _host.CreateScope())
            {
                var result = await CreateRepository(scope)
                    .GetUserGrowthAsync("monthly", new DateTime(2026, 4, 1), new DateTime(2026, 5, 1));

                Assert.Equal(1, result.TotalUsersInPeriod);
                Assert.Empty(result.StatusAtTime);
                Assert.Null(result.StatusHistoryAvailableFromUtc);
            }
        }
    }
}
