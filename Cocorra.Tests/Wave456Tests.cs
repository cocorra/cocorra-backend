using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Cocorra.BLL.Services.Analytics;
using Cocorra.DAL.Data;
using Cocorra.DAL.DTOS.AnalyticsDto;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;
using Cocorra.DAL.Models.Analytics;
using Cocorra.DAL.Repository.AnalyticsRepository;
using Cocorra.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cocorra.Tests
{
    /// <summary>AN-029, AN-037, AN-038, AN-039.</summary>
    public class Wave456Tests : IDisposable
    {
        private readonly SqliteTestHost _host = new();

        public void Dispose() => _host.Dispose();

        private AnalyticsRepository Repo(IServiceScope scope) =>
            new(scope.ServiceProvider.GetRequiredService<AppDbContext>());

        private static ApplicationUser User(Guid id, string name, string? mbti = null) => new()
        {
            Id = id, UserName = name, FirstName = name, LastName = "T",
            Status = UserStatus.Active, MBTI = mbti,
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };

        private static UserEvent Event(string type, Guid userId, DateTime at, string? props = null, Guid? roomId = null) => new()
        {
            EventId = Guid.NewGuid(), EventType = type, UserId = userId,
            RoomId = roomId, PropertiesJson = props, OccurredAtUtc = at
        };

        // ── AN-029 ──────────────────────────────────────────────────────────

        [Fact]
        public async Task SocialGraph_ReportsReciprocityAndSenderConcentration()
        {
            var spammer = Guid.NewGuid();
            var normal = Guid.NewGuid();
            var from = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

            using (var scope = _host.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.Users.AddRange(User(spammer, "spammer"), User(normal, "normal"));

                // One sender produces 9 of 10 requests. Volume alone would read as a healthy
                // social platform; the concentration figure is what exposes it.
                for (var i = 0; i < 9; i++)
                {
                    db.UserEvents.Add(Event(EventTypes.FriendRequestSent, spammer, from.AddMinutes(i)));
                }
                db.UserEvents.Add(Event(EventTypes.FriendRequestSent, normal, from));
                db.UserEvents.Add(Event(EventTypes.FriendRequestAccepted, normal, from.AddHours(3),
                    "{\"hoursToAccept\":3.0}"));

                await db.SaveChangesAsync();
            }

            using (var scope = _host.CreateScope())
            {
                var result = await Repo(scope).GetSocialGraphAsync(from.AddDays(-1), from.AddDays(1));

                Assert.Equal(10, result.FriendRequestsSent);
                Assert.Equal(1, result.FriendRequestsAccepted);
                Assert.Equal(10.0, result.AcceptanceRatePercent);
                Assert.Equal(3.0, result.MedianHoursToAccept);
                Assert.Equal(2, result.DistinctSenders);
                Assert.Equal(9, result.MaxRequestsBySingleSender);
            }
        }

        [Fact]
        public async Task SocialGraph_ReportsConversationsStartedAsNullBeforeInstrumentation()
        {
            var user = Guid.NewGuid();
            var from = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

            using (var scope = _host.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.Users.Add(User(user, "u"));
                // Pre-AN-030 payload: no isFirstMessageToRecipient property.
                db.UserEvents.Add(Event(EventTypes.MessageSent, user, from, "{\"receiverId\":\"x\"}"));
                await db.SaveChangesAsync();
            }

            using (var scope = _host.CreateScope())
            {
                var result = await Repo(scope).GetSocialGraphAsync(from.AddDays(-1), from.AddDays(1));

                Assert.Equal(1, result.MessagesSent);
                // Null, not 0. "No conversations started" would be a claim this data cannot make.
                Assert.Null(result.ConversationsStarted);
            }
        }

        // ── AN-037 ──────────────────────────────────────────────────────────

        [Fact]
        public async Task MbtiAnalysis_SplitsOnFourDichotomies_OverJoinersOnly()
        {
            var from = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
            var extrovertSpeaker = Guid.NewGuid();
            var extrovertSilent = Guid.NewGuid();
            var introvertSilent = Guid.NewGuid();
            var neverJoined = Guid.NewGuid();

            using (var scope = _host.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.Users.AddRange(
                    User(extrovertSpeaker, "es", "ENFP"),
                    User(extrovertSilent, "ex", "ESTJ"),
                    User(introvertSilent, "is", "INFP"),
                    // Never joined: must not appear in the denominator. Someone who never showed
                    // up cannot be said to have declined to speak.
                    User(neverJoined, "nj", "ENFJ"));

                foreach (var id in new[] { extrovertSpeaker, extrovertSilent, introvertSilent })
                {
                    db.UserEvents.Add(Event(EventTypes.RoomJoined, id, from));
                }
                db.UserEvents.Add(Event(EventTypes.MicActivated, extrovertSpeaker, from.AddMinutes(5)));

                await db.SaveChangesAsync();
            }

            using (var scope = _host.CreateScope())
            {
                var result = await Repo(scope).GetMbtiDichotomyAnalysisAsync(from.AddDays(-1), from.AddDays(1));

                Assert.Equal(3, result.UsersWithMbti);
                Assert.Equal(4, result.Dichotomies.Count());

                var ei = result.Dichotomies.Single(d => d.Dichotomy == "E/I");
                Assert.Equal(2, ei.LeftUsers);   // ENFP, ESTJ
                Assert.Equal(1, ei.LeftUsersWhoSpoke);
                Assert.Equal(50.0, ei.LeftSpeakingRatePercent);
                Assert.Equal(1, ei.RightUsers);  // INFP
                Assert.Equal(0.0, ei.RightSpeakingRatePercent);
                Assert.Equal(50.0, ei.DifferencePercentagePoints);

                // The caveat travels with the number: this is observational, self-selected data.
                Assert.Contains("association", result.InterpretationCaveat, StringComparison.OrdinalIgnoreCase);
            }
        }

        // ── AN-038 ──────────────────────────────────────────────────────────

        [Fact]
        public async Task CohortGrid_AssignsEachUserToExactlyOneCohort_AndFlagsThinHistory()
        {
            var week1 = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc); // Monday
            var user = Guid.NewGuid();

            using (var scope = _host.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.Users.Add(User(user, "u"));
                db.UserEvents.Add(Event(EventTypes.RoomJoined, user, week1));
                db.UserEvents.Add(Event(EventTypes.RoomJoined, user, week1.AddDays(7)));
                db.UserEvents.Add(Event(EventTypes.RoomJoined, user, week1.AddDays(14)));
                await db.SaveChangesAsync();
            }

            using (var scope = _host.CreateScope())
            {
                var result = await Repo(scope).GetCohortGridAsync(week1.AddDays(-7), week1.AddDays(21));

                // Bucketing by every active week would list this user three times and make
                // retention look far better than it is.
                Assert.Single(result.Cohorts);

                var row = result.Cohorts.Single();
                Assert.Equal(week1.Date, row.CohortWeekStartUtc);
                Assert.Equal(1, row.CohortSize);
                Assert.All(row.WeeklyRetentionPercent, v => Assert.Equal(100.0, v));

                // Three weeks is not a trend. The flag is what stops a curve being drawn.
                Assert.False(result.HasSufficientHistory);
            }
        }

        // ── AN-039 ──────────────────────────────────────────────────────────

        [Fact]
        public async Task DecisionCenter_WithoutBaseline_AssertsNothing()
        {
            var monday = StartOfWeek(DateTime.UtcNow.Date).AddDays(-7);

            using (var scope = _host.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.DailyPlatformMetrics.Add(new DailyPlatformMetrics
                {
                    Date = monday, DistinctJoiningUsers = 40, DistinctSpeakingUsers = 10,
                    DistinctActiveHosts = 3, RoomsGoneLive = 5, NewRegistrations = 20,
                    VoiceVerificationsSubmitted = 10, VoiceVerificationsApproved = 8
                });
                await db.SaveChangesAsync();
            }

            var service = new DecisionCenterService(_host.Services);
            var result = await service.GetDecisionCenterAsync();

            Assert.False(result.HasBaseline);
            Assert.NotNull(result.BaselineCaveat);

            // Unknown, not Stable. "Stable" is a finding; this is the absence of one, and a
            // dashboard that conflates them starts asserting things it cannot support.
            Assert.All(result.Signals, sig => Assert.Equal(SignalDirection.Unknown, sig.Direction));
            Assert.All(result.Signals, sig => Assert.False(sig.IsSignificant));

            // Values are still shown for reference.
            Assert.Contains(result.Signals, sig => sig.CurrentValue.HasValue);
        }

        [Fact]
        public async Task DecisionCenter_WithBaseline_FlagsOnlyMovesBeyondNormalVariation()
        {
            var thisWeek = StartOfWeek(DateTime.UtcNow.Date);

            using (var scope = _host.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                // Six stable prior weeks, then a collapse in the most recent complete week.
                var joiners = new[] { 100, 102, 98, 101, 99, 100, 20 };

                for (var i = 0; i < joiners.Length; i++)
                {
                    var weekStart = thisWeek.AddDays(-7 * (joiners.Length - i));
                    db.DailyPlatformMetrics.Add(new DailyPlatformMetrics
                    {
                        Date = weekStart,
                        DistinctJoiningUsers = joiners[i],
                        DistinctSpeakingUsers = joiners[i] / 4,
                        DistinctActiveHosts = 5,
                        RoomsGoneLive = 10,
                        NewRegistrations = 30,
                        VoiceVerificationsSubmitted = 10,
                        VoiceVerificationsApproved = 8
                    });
                }

                await db.SaveChangesAsync();
            }

            var result = await new DecisionCenterService(_host.Services).GetDecisionCenterAsync();

            Assert.True(result.HasBaseline);
            Assert.Null(result.BaselineCaveat);

            var wpu = result.Signals.Single(s => s.SignalKey == "weekly_participating_users");
            Assert.True(wpu.IsSignificant, "a drop from ~100 to 20 must clear the noise threshold");
            Assert.Equal(SignalDirection.Worsening, wpu.Direction);
            Assert.Equal(MetricRegistry.WeeklyParticipatingUsers, wpu.MetricKey);
            Assert.False(string.IsNullOrWhiteSpace(wpu.DecisionSupported));

            // Flat signals must NOT be flagged. A detector that fires on everything is the
            // failure mode the baseline gate exists to prevent.
            var hosts = result.Signals.Single(s => s.SignalKey == "active_hosts");
            Assert.False(hosts.IsSignificant);
            Assert.Equal(SignalDirection.Stable, hosts.Direction);
        }

        [Fact]
        public async Task DecisionCenter_EverySignalNamesAContractAndADecision()
        {
            var result = await new DecisionCenterService(_host.Services).GetDecisionCenterAsync();
            var registry = new MetricRegistry();

            // A signal nobody would act on does not belong on the page, and a signal whose
            // trust level cannot be looked up cannot be acted on responsibly.
            Assert.All(result.Signals, sig =>
            {
                Assert.NotNull(registry.GetContract(sig.MetricKey));
                Assert.False(string.IsNullOrWhiteSpace(sig.DecisionSupported));
            });
        }

        private static DateTime StartOfWeek(DateTime value)
        {
            var date = value.Date;
            return date.AddDays(-(((int)date.DayOfWeek + 6) % 7));
        }
    }
}
