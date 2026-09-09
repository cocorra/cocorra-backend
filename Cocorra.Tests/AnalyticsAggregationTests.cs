using System;
using System.Linq;
using System.Threading.Tasks;
using Cocorra.BLL.Services.Analytics;
using Cocorra.BLL.Services.EventTracking;
using Cocorra.DAL.Data;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;
using Cocorra.DAL.Models.Analytics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Cocorra.Tests
{
    public class AnalyticsAggregationTests : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly IServiceProvider _serviceProvider;

        public AnalyticsAggregationTests()
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
        public void MetricRegistry_ReturnsValidVerifiedContracts()
        {
            var registry = new MetricRegistry();
            var contracts = registry.GetAllContracts();

            Assert.NotEmpty(contracts);
            Assert.Contains(contracts, c => c.MetricKey == "M-100");
            Assert.Contains(contracts, c => c.MetricKey == "M-101");
            Assert.Contains(contracts, c => c.MetricKey == "M-102");
            Assert.Contains(contracts, c => c.MetricKey == "M-300");

            var wpu = registry.GetContract("M-100");
            Assert.NotNull(wpu);
            Assert.Equal(MetricTrustLevel.Verified, wpu.TrustLevel);
            Assert.NotEmpty(wpu.Formula);
            Assert.NotEmpty(wpu.ValidationMethod);
        }

        [Fact]
        public async Task AggregationService_RollsUpPlatformMetrics_AndAdvancesWatermark()
        {
            // Every row must sit behind AnalyticsAggregationService's safety lag, which holds
            // back the most recent AggregationSafetyLagSeconds of inserts so in-flight
            // transactions commit before their identity values are stepped over. Anchoring to
            // today.AddHours(n) made this test pass or fail depending on the UTC hour it ran at:
            // before 05:02 UTC the seeded events were still inside the lag window and the
            // service correctly returned 0.
            var anchor = DateTime.UtcNow.AddSeconds(-(AnalyticsAggregationService.AggregationSafetyLagSeconds + 60));
            if (anchor.TimeOfDay < TimeSpan.FromMinutes(1))
            {
                // Keep the whole fixture inside one UTC day when the run lands on midnight.
                anchor = anchor.AddMinutes(-2);
            }

            var today = anchor.Date;
            var hostId = Guid.NewGuid();
            var userId = Guid.NewGuid();

            using (var scope = _serviceProvider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                db.Users.Add(new ApplicationUser { Id = hostId, UserName = "host", FirstName = "H", LastName = "U", CreatedAt = anchor.AddSeconds(-5) });
                db.Users.Add(new ApplicationUser { Id = userId, UserName = "user", FirstName = "U", LastName = "U", CreatedAt = anchor.AddSeconds(-5) });

                var room = new Room { Id = Guid.NewGuid(), HostId = hostId, RoomTitle = "Agg Room", Status = RoomStatus.Live, CreatedAt = anchor.AddSeconds(-4) };
                db.Rooms.Add(room);

                db.RoomParticipants.Add(new RoomParticipant { RoomId = room.Id, UserId = hostId, JoinedAt = anchor.AddSeconds(-4), TotalSpokenSeconds = 1000, Status = ParticipantStatus.Active });
                db.RoomParticipants.Add(new RoomParticipant { RoomId = room.Id, UserId = userId, JoinedAt = anchor.AddSeconds(-4), TotalSpokenSeconds = 300, Status = ParticipantStatus.Active });

                // Add UserEvents that need rollup
                db.UserEvents.Add(new UserEvent { EventId = Guid.NewGuid(), UserId = userId, EventType = EventTypes.VoiceVerificationSubmitted, OccurredAtUtc = anchor.AddSeconds(-3) });
                db.UserEvents.Add(new UserEvent { EventId = Guid.NewGuid(), UserId = userId, EventType = EventTypes.ActivationCompleted, OccurredAtUtc = anchor.AddSeconds(-2) });

                await db.SaveChangesAsync();
            }

            var scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();
            var options = Options.Create(new EventTrackingOptions());
            var aggregationService = new AnalyticsAggregationService(scopeFactory, NullLogger<AnalyticsAggregationService>.Instance, options);

            // Act — Run first aggregation pass
            var processedCount = await aggregationService.PerformAggregationCycleAsync();

            Assert.Equal(2, processedCount);

            // Assert — RM-1 DailyPlatformMetrics has been populated
            using (var scope = _serviceProvider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                var platformMetric = await db.DailyPlatformMetrics.FirstOrDefaultAsync(m => m.Date == today);
                Assert.NotNull(platformMetric);
                Assert.Equal(1, platformMetric.RoomsCreated);
                Assert.Equal(1, platformMetric.RoomsGoneLive);
                Assert.Equal(1, platformMetric.DistinctActiveHosts);
                Assert.Equal(1, platformMetric.DistinctJoiningUsers); // only userId (host excluded)
                Assert.Equal(1, platformMetric.DistinctSpeakingUsers);
                Assert.Equal(300, platformMetric.TotalSpokenSeconds);
                Assert.Equal(2, platformMetric.NewRegistrations);
                Assert.Equal(1, platformMetric.VoiceVerificationsSubmitted);
                Assert.Equal(1, platformMetric.VoiceVerificationsApproved);

                // Watermark check
                var checkpoint = await db.AggregationCheckpoints.FirstOrDefaultAsync(c => c.PipelineName == AnalyticsAggregationService.PipelineName);
                Assert.NotNull(checkpoint);
                Assert.True(checkpoint.LastProcessedEventId > 0);
            }

            // Act 2 — Run second aggregation pass immediately (idempotency check)
            var secondProcessed = await aggregationService.PerformAggregationCycleAsync();
            Assert.Equal(0, secondProcessed); // No new events

            using (var scope = _serviceProvider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                Assert.Equal(1, await db.DailyPlatformMetrics.CountAsync());
            }
        }
    }
}
