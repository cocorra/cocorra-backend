using System;
using System.Linq;
using System.Threading.Tasks;
using Cocorra.DAL.Data;
using Cocorra.DAL.DTOS.AnalyticsDto;
using Cocorra.DAL.Models.Analytics;
using Cocorra.DAL.Repository.AnalyticsRepository;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cocorra.Tests
{
    /// <summary>
    /// A-1 — <c>GET /Analytics/Platform/Health</c>.
    ///
    /// The behaviour worth pinning is what happens when there is nothing to report. Before
    /// aggregation has run in production this endpoint is the dashboard's landing view over an
    /// empty read model, and a landing page that renders six zeros would state that the platform
    /// had no users, no hosts and no rooms.
    /// </summary>
    public class PlatformHealthTests : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly IServiceProvider _serviceProvider;

        private readonly DateTime _today = new(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc);

        public PlatformHealthTests()
        {
            _connection = new SqliteConnection("DataSource=:memory:");
            _connection.Open();

            var services = new ServiceCollection();
            services.AddDbContext<AppDbContext>(o => o.UseSqlite(_connection));
            _serviceProvider = services.BuildServiceProvider();

            using var scope = _serviceProvider.CreateScope();
            scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();
        }

        public void Dispose() => _connection.Dispose();

        private void AddDay(DateTime date, int joiners, int speakers, int hosts, int roomsLive, int activations, int registrations)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.DailyPlatformMetrics.Add(new DailyPlatformMetrics
            {
                Date = date.Date,
                DistinctJoiningUsers = joiners,
                DistinctSpeakingUsers = speakers,
                DistinctActiveHosts = hosts,
                RoomsGoneLive = roomsLive,
                VoiceVerificationsApproved = activations,
                NewRegistrations = registrations
            });
            db.SaveChanges();
        }

        private async Task<PlatformHealthDto> RunAsync(DateTime from, DateTime to, bool compare = true)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return await new AnalyticsRepository(db).GetPlatformHealthAsync(from, to, compare);
        }

        private static PlatformHealthMetricDto Metric(PlatformHealthDto dto, string key)
            => dto.Metrics.Single(m => m.MetricKey == key);

        // ── The empty case, which is production today ────────────────────────

        [Fact]
        public async Task EmptyReadModels_ReturnNullValuesWithAReason_NotZeros()
        {
            var result = await RunAsync(_today.AddDays(-6), _today);

            Assert.False(result.ReadModelsPopulated);
            Assert.Null(result.DataAvailableFromUtc);
            Assert.NotEmpty(result.Metrics);

            Assert.All(result.Metrics, m =>
            {
                Assert.False(m.IsMeasured);
                Assert.Null(m.Value);
                Assert.NotNull(m.NotMeasuredReason);
                Assert.Contains("not zero", m.NotMeasuredReason!);
            });
        }

        [Fact]
        public async Task WindowWithNoRows_ButPopulatedReadModels_SaysSoDistinctly()
        {
            AddDay(_today.AddDays(-90), joiners: 10, speakers: 2, hosts: 3, roomsLive: 4, activations: 1, registrations: 5);

            var result = await RunAsync(_today.AddDays(-6), _today);

            // The pipeline IS running — that is a different statement from "this window has data".
            Assert.True(result.ReadModelsPopulated);
            Assert.Equal(_today.AddDays(-90).Date, result.DataAvailableFromUtc);
            Assert.All(result.Metrics, m => Assert.False(m.IsMeasured));
            Assert.Contains("Backfill", Metric(result, "M-100").NotMeasuredReason!);
        }

        // ── Values and comparison ────────────────────────────────────────────

        [Fact]
        public async Task CountMetrics_SumAcrossTheWindow()
        {
            AddDay(_today.AddDays(-2), 10, 3, 2, 4, 1, 7);
            AddDay(_today.AddDays(-1), 20, 5, 3, 6, 2, 9);

            var result = await RunAsync(_today.AddDays(-2), _today.AddDays(-1), compare: false);

            Assert.Equal(30, Metric(result, "M-100").Value);
            Assert.Equal(10, Metric(result, "M-205").Value);
            Assert.Equal(3, Metric(result, "M-508").Value);
            Assert.Equal(16, Metric(result, "M-501").Value);
            Assert.False(result.HasComparison);
            Assert.Null(result.PreviousFrom);
        }

        [Fact]
        public async Task SpeakingConversion_IsRecomputedFromTotals_NotAveragedAcrossDays()
        {
            // Day 1: 10 joiners, 5 speakers → 50%. Day 2: 90 joiners, 9 speakers → 10%.
            // Averaging the daily rates gives 30%. The correct window figure is 14/100 = 14%.
            AddDay(_today.AddDays(-2), 10, 5, 1, 1, 0, 0);
            AddDay(_today.AddDays(-1), 90, 9, 1, 1, 0, 0);

            var result = await RunAsync(_today.AddDays(-2), _today.AddDays(-1), compare: false);

            Assert.Equal(14.0, Metric(result, "M-101").Value);
        }

        [Fact]
        public async Task ActiveHosts_UsesMaxNotSum_SoAHostActiveTwiceIsNotCountedTwice()
        {
            AddDay(_today.AddDays(-2), 10, 1, 3, 1, 0, 0);
            AddDay(_today.AddDays(-1), 10, 1, 4, 1, 0, 0);

            var result = await RunAsync(_today.AddDays(-2), _today.AddDays(-1), compare: false);

            Assert.Equal(4, Metric(result, "M-200").Value);
        }

        [Fact]
        public async Task PreviousPeriod_IsAnEqualLengthAdjacentWindow()
        {
            // Current: the 3 days ending yesterday. Previous: the 3 days before that.
            var from = _today.AddDays(-3);
            var to = _today.AddDays(-1);

            AddDay(_today.AddDays(-6), 5, 0, 1, 1, 0, 0);
            AddDay(_today.AddDays(-5), 5, 0, 1, 1, 0, 0);
            AddDay(_today.AddDays(-4), 10, 0, 1, 1, 0, 0);
            AddDay(from, 20, 0, 1, 1, 0, 0);
            AddDay(_today.AddDays(-2), 20, 0, 1, 1, 0, 0);
            AddDay(to, 20, 0, 1, 1, 0, 0);

            var result = await RunAsync(from, to);
            var wpu = Metric(result, "M-100");

            Assert.True(result.HasComparison);
            Assert.Equal(_today.AddDays(-6).Date, result.PreviousFrom!.Value.Date);
            Assert.Equal(_today.AddDays(-4).Date, result.PreviousTo!.Value.Date);

            Assert.Equal(60, wpu.Value);
            Assert.Equal(20, wpu.PreviousValue);
            Assert.Equal(40, wpu.DeltaAbsolute);
            Assert.Equal(200, wpu.DeltaPercent);
        }

        [Fact]
        public async Task GrowthFromZero_ReportsAnAbsoluteDeltaButNoPercentage()
        {
            var from = _today.AddDays(-1);

            AddDay(_today.AddDays(-2), joiners: 0, speakers: 0, hosts: 0, roomsLive: 0, activations: 0, registrations: 0);
            AddDay(from, joiners: 25, speakers: 0, hosts: 1, roomsLive: 1, activations: 0, registrations: 0);

            var wpu = Metric(await RunAsync(from, from), "M-100");

            Assert.Equal(25, wpu.Value);
            Assert.Equal(0, wpu.PreviousValue);
            Assert.Equal(25, wpu.DeltaAbsolute);
            // A change from nothing is not "+100%" and not infinite.
            Assert.Null(wpu.DeltaPercent);
        }

        [Fact]
        public async Task ZeroJoiners_LeavesConversionNullRatherThanZeroPercent()
        {
            var day = _today.AddDays(-1);
            AddDay(day, joiners: 0, speakers: 0, hosts: 1, roomsLive: 2, activations: 0, registrations: 0);

            var result = await RunAsync(day, day, compare: false);

            // Rooms ran, nobody joined. 0% conversion would assert that listeners declined to
            // speak; there were no listeners.
            Assert.Equal(2, Metric(result, "M-205").Value);
            Assert.Null(Metric(result, "M-101").Value);
            Assert.False(Metric(result, "M-101").IsMeasured);
        }

        // ── Contract surface ────────────────────────────────────────────────

        [Fact]
        public async Task EveryMetric_CarriesAResolvableDrillDownRoute()
        {
            var result = await RunAsync(_today.AddDays(-6), _today);

            Assert.All(result.Metrics, m =>
            {
                Assert.False(string.IsNullOrWhiteSpace(m.DrillDownEndpoint));
                Assert.Contains("Analytics", m.DrillDownEndpoint);
            });
        }

        [Fact]
        public async Task NorthStarIsPresent_BecauseNoOtherUngatedEndpointServesIt()
        {
            // M-100 was previously reachable only through /Analytics/Decisions, which must not
            // be relied on until 4-6 weeks of baseline exist. That left the declared north star
            // unreadable on any page anyone could trust.
            var result = await RunAsync(_today.AddDays(-6), _today);

            Assert.Contains(result.Metrics, m => m.MetricKey == "M-100");
        }

        [Fact]
        public async Task WindowEndingToday_IsFlaggedPartial()
        {
            var result = await RunAsync(DateTime.UtcNow.AddDays(-6), DateTime.UtcNow);
            Assert.True(result.IsPartialPeriod);
        }
    }
}
