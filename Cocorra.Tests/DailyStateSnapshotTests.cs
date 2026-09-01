using System;
using System.Linq;
using System.Threading.Tasks;
using Cocorra.BLL.Services.Analytics;
using Cocorra.DAL.Data;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;
using Cocorra.DAL.Models.Analytics;
using Cocorra.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cocorra.Tests
{
    public class DailyStateSnapshotTests : IDisposable
    {
        private readonly SqliteTestHost _host = new();

        public void Dispose() => _host.Dispose();

        private StateSnapshotService CreateService() =>
            new(_host.Services, NullLogger<StateSnapshotService>.Instance);

        [Fact]
        public void ProviderGuard_TestHostUsesSqlite_NotInMemory()
        {
            // B-5: these tests are only meaningful on a provider that enforces unique indexes.
            // If someone swaps the host to EFCore.InMemory, every idempotency assertion below
            // would pass vacuously — so fail loudly here instead.
            using var scope = _host.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            Assert.Equal(SqliteTestHost.SqliteProviderName, db.Database.ProviderName);
        }

        [Fact]
        public async Task ProviderGuard_EnforcesUniqueIndexOn_DateAndMetricKey()
        {
            using var scope = _host.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var date = new DateTime(2026, 9, 1);

            // First insert succeeds
            db.DailyStateSnapshots.Add(new DailyStateSnapshot
            {
                Date = date,
                MetricKey = StateSnapshotService.MetricPendingQueue,
                Value = 10,
                ComputedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();

            // Duplicate insert on (Date, MetricKey) must fail with DbUpdateException
            db.DailyStateSnapshots.Add(new DailyStateSnapshot
            {
                Date = date,
                MetricKey = StateSnapshotService.MetricPendingQueue,
                Value = 20,
                ComputedAtUtc = DateTime.UtcNow
            });

            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }

        [Fact]
        public async Task CaptureSnapshotAsync_CalculatesAllStateMetricsAccurately()
        {
            // Arrange — seed test data
            var testDate = new DateTime(2026, 9, 1);
            var reporterId = Guid.NewGuid();

            using (var scope = _host.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                // Users: 3 Pending, 2 ReRecord, 4 Active (3 with FCM, 1 without), 1 Banned + Reporter
                db.Users.AddRange(
                    new ApplicationUser { Id = reporterId, UserName = "reporter", FirstName = "Rep", LastName = "User", Status = UserStatus.Active, FcmToken = "token-rep" },
                    new ApplicationUser { Id = Guid.NewGuid(), UserName = "p1", FirstName = "P1", LastName = "User", Status = UserStatus.Pending },
                    new ApplicationUser { Id = Guid.NewGuid(), UserName = "p2", FirstName = "P2", LastName = "User", Status = UserStatus.Pending },
                    new ApplicationUser { Id = Guid.NewGuid(), UserName = "p3", FirstName = "P3", LastName = "User", Status = UserStatus.Pending },
                    new ApplicationUser { Id = Guid.NewGuid(), UserName = "r1", FirstName = "R1", LastName = "User", Status = UserStatus.ReRecord },
                    new ApplicationUser { Id = Guid.NewGuid(), UserName = "r2", FirstName = "R2", LastName = "User", Status = UserStatus.ReRecord },
                    new ApplicationUser { Id = Guid.NewGuid(), UserName = "a1", FirstName = "A1", LastName = "User", Status = UserStatus.Active, FcmToken = "token-1" },
                    new ApplicationUser { Id = Guid.NewGuid(), UserName = "a2", FirstName = "A2", LastName = "User", Status = UserStatus.Active, FcmToken = "token-2" },
                    new ApplicationUser { Id = Guid.NewGuid(), UserName = "a3", FirstName = "A3", LastName = "User", Status = UserStatus.Active, FcmToken = "token-3" },
                    new ApplicationUser { Id = Guid.NewGuid(), UserName = "a4", FirstName = "A4", LastName = "User", Status = UserStatus.Active, FcmToken = null },
                    new ApplicationUser { Id = Guid.NewGuid(), UserName = "b1", FirstName = "B1", LastName = "User", Status = UserStatus.Banned }
                );

                // Reports: 2 Open, 1 Resolved
                db.Reports.AddRange(
                    new Report { Id = Guid.NewGuid(), ReporterId = reporterId, Description = "Report 1", Status = "Open" },
                    new Report { Id = Guid.NewGuid(), ReporterId = reporterId, Description = "Report 2", Status = "Open" },
                    new Report { Id = Guid.NewGuid(), ReporterId = reporterId, Description = "Report 3", Status = "Resolved" }
                );

                await db.SaveChangesAsync();
            }

            var service = CreateService();

            // Act
            var results = await service.CaptureSnapshotAsync(testDate);

            // Assert
            Assert.Equal(StateSnapshotService.ExpectedMetricKeys.Length, results.Count);

            var resultMap = results.ToDictionary(r => r.MetricKey, r => r.Value);
            Assert.Equal(3.0, resultMap[StateSnapshotService.MetricPendingQueue]);
            Assert.Equal(2.0, resultMap[StateSnapshotService.MetricRerecordQueue]);
            Assert.Equal(5.0, resultMap[StateSnapshotService.MetricActiveUsersTotal]); // 4 + 1 reporter = 5
            Assert.Equal(4.0, resultMap[StateSnapshotService.MetricActiveUsersWithFcm]);
            Assert.Equal(0.8, resultMap[StateSnapshotService.MetricFcmTokenCoverage]); // 4/5 = 0.8
            Assert.Equal(2.0, resultMap[StateSnapshotService.MetricOpenReports]);
        }

        [Fact]
        public async Task CaptureSnapshotAsync_StoresFcmCoverageNumeratorAndDenominator_SoTheRateIsRecomputable()
        {
            // The read-model rule is counts, not percentages: a rate must be derivable from a
            // stored numerator and denominator, otherwise the actionable absolute number
            // (how many active users lack a token) is lost to rounding as the platform grows.
            using (var scope = _host.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                for (var i = 0; i < 7; i++)
                {
                    db.Users.Add(new ApplicationUser
                    {
                        Id = Guid.NewGuid(),
                        UserName = $"u{i}",
                        FirstName = "U",
                        LastName = i.ToString(),
                        Status = UserStatus.Active,
                        FcmToken = i < 3 ? $"token-{i}" : null
                    });
                }
                await db.SaveChangesAsync();
            }

            var results = await CreateService().CaptureSnapshotAsync(new DateTime(2026, 9, 1));
            var map = results.ToDictionary(r => r.MetricKey, r => r.Value);

            var numerator = map[StateSnapshotService.MetricActiveUsersWithFcm];
            var denominator = map[StateSnapshotService.MetricActiveUsersTotal];

            Assert.Equal(3.0, numerator);
            Assert.Equal(7.0, denominator);

            // 3/7 is not representable at 4dp, which is exactly why the counts are stored.
            Assert.Equal(3.0 / 7.0, numerator / denominator, precision: 10);
            Assert.Equal(0.4286, map[StateSnapshotService.MetricFcmTokenCoverage]);
        }

        [Fact]
        public async Task CaptureSnapshotAsync_IsIdempotent_UpdatesExistingRowWithoutDuplication()
        {
            var testDate = new DateTime(2026, 9, 1);
            var userId = Guid.NewGuid();
            var expectedRows = StateSnapshotService.ExpectedMetricKeys.Length;

            using (var scope = _host.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.Users.Add(new ApplicationUser { Id = userId, UserName = "p1", FirstName = "P1", LastName = "User", Status = UserStatus.Pending });
                await db.SaveChangesAsync();
            }

            var service = CreateService();

            // First run
            var firstRun = await service.CaptureSnapshotAsync(testDate);

            using (var scope = _host.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                Assert.Equal(expectedRows, await db.DailyStateSnapshots.CountAsync());
                Assert.Equal(1.0, firstRun.First(r => r.MetricKey == StateSnapshotService.MetricPendingQueue).Value);

                // Change state: user becomes Active
                var user = await db.Users.FindAsync(userId);
                Assert.NotNull(user);
                user.Status = UserStatus.Active;
                user.FcmToken = "fcm-token";
                await db.SaveChangesAsync();
            }

            // Second run for the same date
            var secondRun = await service.CaptureSnapshotAsync(testDate);

            using (var scope = _host.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                // Must still have exactly one row per metric key (no duplicates)
                Assert.Equal(expectedRows, await db.DailyStateSnapshots.CountAsync());
            }

            var pendingMetric = secondRun.First(r => r.MetricKey == StateSnapshotService.MetricPendingQueue);
            var activeMetric = secondRun.First(r => r.MetricKey == StateSnapshotService.MetricActiveUsersTotal);

            Assert.Equal(0.0, pendingMetric.Value);
            Assert.Equal(1.0, activeMetric.Value);
        }

        [Fact]
        public async Task CaptureSnapshotAsync_AbsorbsConcurrentInsertOnTheNaturalKey_WithoutThrowing()
        {
            // A pre-existing row planted by "another writer" between read and write is the
            // shape of the race. CaptureSnapshotAsync must reconcile it as an update rather
            // than surfacing UX_DailyStateSnapshots_Date_MetricKey to a request-path caller.
            var testDate = new DateTime(2026, 9, 1);

            using (var scope = _host.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.DailyStateSnapshots.Add(new DailyStateSnapshot
                {
                    Date = testDate,
                    MetricKey = StateSnapshotService.MetricOpenReports,
                    Value = 999,
                    ComputedAtUtc = DateTime.UtcNow.AddHours(-1)
                });
                await db.SaveChangesAsync();
            }

            var results = await CreateService().CaptureSnapshotAsync(testDate);

            Assert.Equal(StateSnapshotService.ExpectedMetricKeys.Length, results.Count);
            Assert.Equal(0.0, results.First(r => r.MetricKey == StateSnapshotService.MetricOpenReports).Value);

            using (var scope = _host.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                Assert.Equal(StateSnapshotService.ExpectedMetricKeys.Length, await db.DailyStateSnapshots.CountAsync());
            }
        }

        [Fact]
        public async Task CaptureSnapshotAsync_SkipIfAlreadyCaptured_LeavesTheEarlierReadingIntact()
        {
            // A container restart must not replace a scheduled 00:15 reading with an
            // arbitrary-hour one: queue depth varies through the day, so a series mixing
            // capture hours is not comparable.
            var testDate = new DateTime(2026, 9, 1);
            var service = CreateService();

            var scheduled = await service.CaptureSnapshotAsync(testDate);
            var scheduledComputedAt = scheduled.First().ComputedAtUtc;

            using (var scope = _host.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.Users.Add(new ApplicationUser { Id = Guid.NewGuid(), UserName = "p1", FirstName = "P1", LastName = "User", Status = UserStatus.Pending });
                await db.SaveChangesAsync();
            }

            var startupRun = await service.CaptureSnapshotAsync(testDate, skipIfAlreadyCaptured: true);

            // The new Pending user is deliberately NOT reflected — the existing row is returned as-is.
            Assert.Equal(0.0, startupRun.First(r => r.MetricKey == StateSnapshotService.MetricPendingQueue).Value);
            Assert.Equal(scheduledComputedAt, startupRun.First().ComputedAtUtc);

            // A scheduled run (skip flag off) does overwrite.
            var overwritten = await service.CaptureSnapshotAsync(testDate);
            Assert.Equal(1.0, overwritten.First(r => r.MetricKey == StateSnapshotService.MetricPendingQueue).Value);
        }

        [Fact]
        public async Task GetSnapshotsAsync_FiltersAndOrdersByDateAndMetric()
        {
            var service = CreateService();

            var day1 = new DateTime(2026, 9, 1);
            var day2 = new DateTime(2026, 9, 2);
            var day3 = new DateTime(2026, 9, 3);

            await service.CaptureSnapshotAsync(day1);
            await service.CaptureSnapshotAsync(day2);
            await service.CaptureSnapshotAsync(day3);

            // Act — Query range [day1, day2]
            var fetched = await service.GetSnapshotsAsync(day1, day2);

            // Assert — 2 days * N metrics
            Assert.Equal(2 * StateSnapshotService.ExpectedMetricKeys.Length, fetched.Count);
            Assert.All(fetched, s => Assert.True(s.Date >= day1 && s.Date <= day2));
        }

        [Fact]
        public async Task GetGapReportAsync_FlagsMissingDates_AndNeverInterpolates()
        {
            var service = CreateService();
            var today = DateTime.UtcNow.Date;

            // Capture day-5 and day-3, skipping day-4 entirely.
            await service.CaptureSnapshotAsync(today.AddDays(-5));
            await service.CaptureSnapshotAsync(today.AddDays(-3));

            var report = await service.GetGapReportAsync(today.AddDays(-30), today);

            Assert.Equal(today.AddDays(-5), report.DataAvailableFromUtc);
            // Range is clamped to the first capture, so nothing before day-5 is claimed as a gap.
            Assert.Equal(today.AddDays(-5), report.FromDate);
            Assert.True(report.HasGaps);
            Assert.Contains(today.AddDays(-4), report.MissingDates);
            Assert.DoesNotContain(today.AddDays(-5), report.MissingDates);
            Assert.DoesNotContain(today.AddDays(-3), report.MissingDates);
            // day-2, day-1 and today are missing too — the hole is reported, not filled.
            Assert.Equal(4, report.MissingDates.Count);
            Assert.Empty(report.IncompleteDates);

            // The snapshot query itself stays sparse: absence must never read as zero.
            var rows = await service.GetSnapshotsAsync(today.AddDays(-5), today);
            Assert.DoesNotContain(rows, r => r.Date == today.AddDays(-4));
        }

        [Fact]
        public async Task GetGapReportAsync_FlagsIncompleteDates()
        {
            var day = DateTime.UtcNow.Date.AddDays(-2);

            using (var scope = _host.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.DailyStateSnapshots.Add(new DailyStateSnapshot
                {
                    Date = day,
                    MetricKey = StateSnapshotService.MetricPendingQueue,
                    Value = 4,
                    ComputedAtUtc = DateTime.UtcNow
                });
                await db.SaveChangesAsync();
            }

            var report = await CreateService().GetGapReportAsync(day, day);

            Assert.Contains(day, report.IncompleteDates);
            Assert.Empty(report.MissingDates);
            Assert.Equal(StateSnapshotService.ExpectedMetricKeys.Length, report.ExpectedMetricsPerDate);
        }

        [Fact]
        public async Task GetGapReportAsync_EmptyTable_ReportsNotMeasured_RatherThanEveryDateMissing()
        {
            var today = DateTime.UtcNow.Date;

            var report = await CreateService().GetGapReportAsync(today.AddDays(-30), today);

            Assert.Null(report.DataAvailableFromUtc);
            Assert.False(report.HasGaps);
            Assert.Empty(report.MissingDates);
        }
    }
}
