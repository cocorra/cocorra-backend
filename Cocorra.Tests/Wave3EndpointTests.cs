using System;
using System.Linq;
using System.Threading.Tasks;
using Cocorra.BLL.Services.Analytics;
using Cocorra.DAL.Data;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;
using Cocorra.DAL.Models.Analytics;
using Cocorra.DAL.Repository.AnalyticsRepository;
using Cocorra.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cocorra.Tests
{
    /// <summary>AN-020, AN-021, AN-022, AN-023, AN-016.</summary>
    public class Wave3EndpointTests : IDisposable
    {
        private readonly SqliteTestHost _host = new();

        public void Dispose() => _host.Dispose();

        private AnalyticsRepository Repo(IServiceScope scope) =>
            new(scope.ServiceProvider.GetRequiredService<AppDbContext>());

        private static ApplicationUser User(Guid id, string name, UserStatus status = UserStatus.Active) => new()
        {
            Id = id, UserName = name, FirstName = name, LastName = "T", Status = status,
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };

        private static Room Room(Guid id, Guid hostId, DateTime createdAt, RoomCategory category = RoomCategory.Others,
            RoomStatus status = RoomStatus.Live) => new()
        {
            Id = id, HostId = hostId, RoomTitle = "R", Category = category, Status = status,
            CreatedAt = createdAt, StartDate = createdAt
        };

        // ── AN-020 ──────────────────────────────────────────────────────────

        [Fact]
        public async Task SupplyHealth_MeasuresHostRetentionAndConcentration()
        {
            var heavyHost = Guid.NewGuid();
            var oneRoomHost = Guid.NewGuid();
            var from = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

            using (var scope = _host.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.Users.AddRange(User(heavyHost, "heavy"), User(oneRoomHost, "light"));

                // Heavy host runs 4 rooms (its second is 5 days after its first); light host 1.
                db.Rooms.Add(Room(Guid.NewGuid(), heavyHost, from));
                db.Rooms.Add(Room(Guid.NewGuid(), heavyHost, from.AddDays(5)));
                db.Rooms.Add(Room(Guid.NewGuid(), heavyHost, from.AddDays(10)));
                db.Rooms.Add(Room(Guid.NewGuid(), heavyHost, from.AddDays(15), status: RoomStatus.Scheduled));
                db.Rooms.Add(Room(Guid.NewGuid(), oneRoomHost, from.AddDays(2)));
                await db.SaveChangesAsync();
            }

            using (var scope = _host.CreateScope())
            {
                var result = await Repo(scope).GetSupplyHealthAsync("monthly", from.AddDays(-1), from.AddDays(30));

                Assert.Equal(2, result.HostRetention.NewHosts);
                Assert.Equal(1, result.HostRetention.HostsWithSecondRoom);
                Assert.Equal(50.0, result.HostRetention.SecondRoomRatePercent);
                Assert.Equal(5, result.HostRetention.MedianDaysToSecondRoom);

                Assert.Equal(2, result.Concentration.TotalHostsInWindow);
                Assert.Equal(5, result.Concentration.TotalRoomsInWindow);
                Assert.Equal(80.0, result.Concentration.TopHostSharePercent);
                // One host carries more than half the rooms: the key-person risk a bare host
                // count would have hidden entirely.
                Assert.Equal(1, result.Concentration.HostsCoveringHalfOfRooms);

                // Scheduled rooms count as created but not as gone live.
                var march = result.ActiveHosts.Single(a => a.Period == "2026-03");
                Assert.Equal(5, march.RoomsCreated);
                Assert.Equal(4, march.RoomsGoneLive);
                Assert.Equal(2, march.DistinctHosts);

                Assert.Equal(180, result.SuggestedDisplayOffsetMinutes);
            }
        }

        [Fact]
        public async Task SupplyHealth_DoesNotCountAPreExistingHostAsNew()
        {
            var host = Guid.NewGuid();
            var windowStart = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

            using (var scope = _host.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.Users.Add(User(host, "veteran"));
                db.Rooms.Add(Room(Guid.NewGuid(), host, windowStart.AddDays(-60))); // first room, before window
                db.Rooms.Add(Room(Guid.NewGuid(), host, windowStart.AddDays(3)));
                await db.SaveChangesAsync();
            }

            using (var scope = _host.CreateScope())
            {
                var result = await Repo(scope).GetSupplyHealthAsync("monthly", windowStart, windowStart.AddDays(30));

                // Counting them as a new host would depress the second-room rate for no reason.
                Assert.Equal(0, result.HostRetention.NewHosts);
            }
        }

        // ── AN-021 ──────────────────────────────────────────────────────────

        [Fact]
        public async Task ReportRate_NormalisesByExposure_AndExcludesReportsWithoutRoomContext()
        {
            var host = Guid.NewGuid();
            var reporter = Guid.NewGuid();
            var mentalHealthRoom = Guid.NewGuid();
            var othersRoom = Guid.NewGuid();
            var from = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);

            using (var scope = _host.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.Users.AddRange(User(host, "host"), User(reporter, "reporter"));
                db.Rooms.Add(Room(mentalHealthRoom, host, from, RoomCategory.MentalHealth));
                db.Rooms.Add(Room(othersRoom, host, from, RoomCategory.Others));

                // MentalHealth: 2 joins, 1 report. Others: 8 joins, 1 report.
                // Absolute counts are equal; only the rate reveals the difference.
                for (var i = 0; i < 2; i++)
                {
                    var u = Guid.NewGuid();
                    db.Users.Add(User(u, $"mh{i}"));
                    db.RoomParticipants.Add(new RoomParticipant { RoomId = mentalHealthRoom, UserId = u, JoinedAt = from.AddHours(1) });
                }
                for (var i = 0; i < 8; i++)
                {
                    var u = Guid.NewGuid();
                    db.Users.Add(User(u, $"ot{i}"));
                    db.RoomParticipants.Add(new RoomParticipant { RoomId = othersRoom, UserId = u, JoinedAt = from.AddHours(1) });
                }

                db.Reports.AddRange(
                    new Report { Id = Guid.NewGuid(), ReporterId = reporter, ReportedRoomId = mentalHealthRoom, Description = "a", CreatedAt = from.AddHours(2) },
                    new Report { Id = Guid.NewGuid(), ReporterId = reporter, ReportedRoomId = othersRoom, Description = "b", CreatedAt = from.AddHours(2) },
                    new Report { Id = Guid.NewGuid(), ReporterId = reporter, ReportedRoomId = null, Description = "no room", CreatedAt = from.AddHours(2) });

                await db.SaveChangesAsync();
            }

            using (var scope = _host.CreateScope())
            {
                var result = await Repo(scope).GetReportRateByCategoryAsync(from.AddDays(-1), from.AddDays(1));

                var mh = result.Categories.Single(c => c.Category == nameof(RoomCategory.MentalHealth));
                var others = result.Categories.Single(c => c.Category == nameof(RoomCategory.Others));

                Assert.Equal(1, mh.ReportCount);
                Assert.Equal(1, others.ReportCount);

                // 1/2 vs 1/8 joins — a 4x difference invisible in the raw counts.
                Assert.Equal(500, mh.ReportsPer1000Joins);
                Assert.Equal(125, others.ReportsPer1000Joins);

                // Excluded, not bucketed into Others, which would have inflated it.
                Assert.Equal(1, result.ReportsWithoutRoomContext);
                Assert.Equal(3, result.TotalReports);

                // No exposure means no rate — null, not a zero that reads as "safe".
                var relationships = result.Categories.Single(c => c.Category == nameof(RoomCategory.Relationships));
                Assert.Null(relationships.ReportsPer1000Joins);
            }
        }

        // ── AN-022 ──────────────────────────────────────────────────────────

        [Fact]
        public async Task ReviewLatency_ReturnsPercentiles_AndNeverAMean()
        {
            var from = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
            // Bimodal on purpose: three fast reviews and one three-day outlier. A mean would
            // report ~18h, describing none of the four.
            var latencies = new[] { 1.0, 1.0, 1.0, 72.0 };

            using (var scope = _host.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                for (var i = 0; i < latencies.Length; i++)
                {
                    var u = Guid.NewGuid();
                    db.Users.Add(User(u, $"u{i}", UserStatus.Pending));
                    db.UserEvents.Add(new UserEvent { EventId = Guid.NewGuid(), EventType = EventTypes.VoiceVerificationSubmitted, UserId = u, OccurredAtUtc = from });
                    db.UserEvents.Add(new UserEvent { EventId = Guid.NewGuid(), EventType = EventTypes.VoiceVerificationResult, UserId = u, OccurredAtUtc = from.AddHours(latencies[i]) });
                }

                // A submission with no result yet must be excluded, not counted as zero wait.
                var pending = Guid.NewGuid();
                db.Users.Add(User(pending, "pending", UserStatus.Pending));
                db.UserEvents.Add(new UserEvent { EventId = Guid.NewGuid(), EventType = EventTypes.VoiceVerificationSubmitted, UserId = pending, OccurredAtUtc = from });

                db.DailyStateSnapshots.Add(new DailyStateSnapshot
                {
                    Date = from.Date,
                    MetricKey = StateSnapshotService.MetricPendingQueue,
                    Value = 12,
                    ComputedAtUtc = from
                });

                await db.SaveChangesAsync();
            }

            using (var scope = _host.CreateScope())
            {
                var result = await Repo(scope).GetReviewLatencyAsync(from.AddDays(-1), from.AddDays(1));

                Assert.Equal(4, result.ReviewsMeasured);
                Assert.Equal(1.0, result.P50Hours);
                Assert.True(result.P90Hours > result.P50Hours);
                Assert.Equal(12, result.CurrentPendingQueueDepth);

                // Contract requirement, not a presentation preference.
                Assert.Null(typeof(DAL.DTOS.AnalyticsDto.ReviewLatencyDto).GetProperty("MeanHours"));
                Assert.Null(typeof(DAL.DTOS.AnalyticsDto.ReviewLatencyDto).GetProperty("AverageHours"));
            }
        }

        // ── AN-023 ──────────────────────────────────────────────────────────

        [Fact]
        public async Task SupportAnalytics_IncludesAnonymousTickets_AndCarriesTheProxyCaveat()
        {
            var from = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
            var user = Guid.NewGuid();
            var chatId = Guid.NewGuid();

            using (var scope = _host.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.Users.Add(User(user, "u"));
                db.SupportTickets.AddRange(
                    new SupportTicket { Id = Guid.NewGuid(), UserId = user, Type = SupportTicketType.TechnicalProblem, Message = "m", CreatedAt = from },
                    // Anonymous: a user who cannot log in is the reliability signal, not noise.
                    new SupportTicket { Id = Guid.NewGuid(), UserId = null, Type = SupportTicketType.TechnicalProblem, Message = "m", CreatedAt = from },
                    new SupportTicket { Id = Guid.NewGuid(), UserId = user, Type = SupportTicketType.GeneralQuestion, Message = "m", CreatedAt = from });

                db.SupportMessages.AddRange(
                    new SupportMessage { Id = Guid.NewGuid(), SupportChatId = chatId, SenderId = user.ToString(), Content = "help", IsFromAdmin = false, CreatedAt = from },
                    new SupportMessage { Id = Guid.NewGuid(), SupportChatId = chatId, SenderId = "admin", Content = "hi", IsFromAdmin = true, CreatedAt = from.AddMinutes(30) });

                // SupportChat.RowVersion is a SQL Server rowversion: EF marks it store-generated
                // and omits it from the INSERT, and SQLite has no generator to fill it. Inserting
                // this one row with raw SQL sidesteps the provider difference rather than
                // reshaping the production model to suit a test.
                await db.Database.ExecuteSqlRawAsync(
                    "INSERT INTO SupportChats (Id, UserId, AdminId, Status, CreatedAt, ClosedAt, RowVersion) " +
                    "VALUES ({0}, {1}, NULL, 0, {2}, {3}, X'0000000000000000')",
                    // EF Core stores Guid as uppercase TEXT on SQLite, and TEXT comparison is
                    // case-sensitive, so the SupportMessages foreign key only matches this form.
                    chatId.ToString().ToUpperInvariant(), user.ToString(), from, from.AddHours(4));

                await db.SaveChangesAsync();
            }

            using (var scope = _host.CreateScope())
            {
                var result = await Repo(scope).GetSupportAnalyticsAsync(from.AddDays(-1), from.AddDays(1));

                Assert.Equal(3, result.TotalTickets);
                Assert.Equal(1, result.AnonymousTickets);
                Assert.Equal(2, result.ByType.Single(t => t.Type == nameof(SupportTicketType.TechnicalProblem)).TicketCount);
                Assert.Equal(1, result.ChatsOpened);
                Assert.Equal(1, result.ChatsClosed);
                Assert.Equal(30, result.MedianFirstResponseMinutes);
                Assert.Equal(4, result.MedianResolutionHours);
                Assert.Contains("Proxy measure", result.ReliabilityCaveat);
            }
        }

        // ── AN-016 ──────────────────────────────────────────────────────────

        [Fact]
        public async Task Backfill_IsIdempotent_AndSkipsDatesAlreadyPresent()
        {
            var host = Guid.NewGuid();
            var day = new DateTime(2026, 5, 10, 0, 0, 0, DateTimeKind.Utc);

            using (var scope = _host.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.Users.Add(User(host, "host"));
                db.Rooms.Add(Room(Guid.NewGuid(), host, day.AddHours(3)));
                await db.SaveChangesAsync();
            }

            var backfill = new AnalyticsBackfillService(
                _host.Services.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<AnalyticsBackfillService>.Instance);

            var first = await backfill.BackfillAsync(day, day);
            Assert.True(first.Completed);
            Assert.Equal(1, first.DatesProcessed);

            // Re-running must skip rather than duplicate.
            var second = await backfill.BackfillAsync(day, day);
            Assert.Equal(0, second.DatesProcessed);
            Assert.Equal(1, second.DatesSkipped);

            using (var scope = _host.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                Assert.Equal(1, await db.DailyPlatformMetrics.CountAsync(m => m.Date == day));

                // RM-3 is the standout: it derives from Rooms, which is never purged, so host
                // history reconstructs from the platform's first day.
                Assert.Equal(1, await db.DailyHostMetrics.CountAsync(m => m.Date == day && m.HostId == host));
            }

            // Forced re-run recomputes in place: still exactly one row.
            var forced = await backfill.BackfillAsync(day, day, force: true);
            Assert.Equal(1, forced.DatesProcessed);

            using (var scope = _host.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                Assert.Equal(1, await db.DailyPlatformMetrics.CountAsync(m => m.Date == day));
            }
        }

        [Fact]
        public async Task Backfill_NeverFabricatesStateSnapshots()
        {
            var day = new DateTime(2026, 5, 10, 0, 0, 0, DateTimeKind.Utc);

            var backfill = new AnalyticsBackfillService(
                _host.Services.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<AnalyticsBackfillService>.Instance);

            var result = await backfill.BackfillAsync(day, day.AddDays(2));

            using var scope = _host.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // Pure state cannot be reconstructed after the fact. Writing plausible-looking rows
            // would be inventing history, which the storage strategy forbids outright.
            Assert.Equal(0, await db.DailyStateSnapshots.CountAsync());
            Assert.Contains(result.Notes, n => n.Contains("RM-5", StringComparison.OrdinalIgnoreCase));
        }
    }
}
