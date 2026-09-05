using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cocorra.DAL.Data;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models.Analytics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Cocorra.BLL.Services.EventTracking;

namespace Cocorra.BLL.Services.Analytics
{
    /// <summary>
    /// RM-5: StateSnapshotService runs daily (scheduled at 00:15 UTC with startup delay)
    /// to capture point-in-time state counts (pending queue, rerecord queue, active totals,
    /// FCM coverage, open reports) into DailyStateSnapshots with idempotent UPSERT semantics.
    ///
    /// These counts are pure state: they cannot be derived from the event stream and cannot be
    /// backfilled. A date that is not captured is lost permanently, so missing dates are
    /// reported explicitly (see <see cref="GetGapReportAsync"/>) and never interpolated.
    /// </summary>
    public class StateSnapshotService : BackgroundService, IStateSnapshotService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<StateSnapshotService> _logger;
        private readonly EventTrackingOptions _options;

        /// <summary>Attempts for the persist step, to absorb a concurrent insert on the natural key.</summary>
        private const int MaxPersistAttempts = 3;

        /// <summary>Trailing window scanned for gaps after each capture.</summary>
        private const int GapScanDays = 30;

        // Metric Keys constants
        public const string MetricPendingQueue = "pending_verification_queue";
        public const string MetricRerecordQueue = "rerecord_queue";
        public const string MetricActiveUsersTotal = "active_users_total";
        public const string MetricActiveUsersWithFcm = "active_users_with_fcm";
        public const string MetricFcmTokenCoverage = "fcm_token_coverage";
        public const string MetricOpenReports = "open_reports";

        /// <summary>
        /// The full metric set a complete date carries. Adding a key here will cause dates
        /// captured before the addition to be reported as incomplete, which is accurate:
        /// they genuinely do not carry the new key.
        /// </summary>
        public static readonly string[] ExpectedMetricKeys =
        {
            MetricPendingQueue,
            MetricRerecordQueue,
            MetricActiveUsersTotal,
            MetricActiveUsersWithFcm,
            MetricFcmTokenCoverage,
            MetricOpenReports
        };

        public StateSnapshotService(
            IServiceProvider serviceProvider,
            ILogger<StateSnapshotService> logger,
            IOptions<EventTrackingOptions>? options = null)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
            _options = options?.Value ?? new EventTrackingOptions();
        }

        /// <inheritdoc />
        public async Task<List<DailyStateSnapshot>> CaptureSnapshotAsync(
            DateTime? targetDate = null,
            bool skipIfAlreadyCaptured = false,
            CancellationToken cancellationToken = default)
        {
            var date = (targetDate ?? DateTime.UtcNow).Date;
            var computedAt = DateTime.UtcNow;

            if (skipIfAlreadyCaptured)
            {
                var existing = await LoadSnapshotsForDateAsync(date, cancellationToken);
                if (existing.Count > 0)
                {
                    _logger.LogInformation(
                        "Daily state snapshot for {Date:yyyy-MM-dd} was already captured at {ComputedAt:HH:mm} UTC; skipping. " +
                        "Overwriting it now would replace a scheduled reading with an arbitrary-hour one and make the series incomparable.",
                        date, existing[0].ComputedAtUtc);
                    return existing;
                }
            }

            _logger.LogInformation("Capturing daily state snapshot for {Date:yyyy-MM-dd}...", date);

            var metrics = await ComputeMetricsAsync(cancellationToken);

            // Persist with a bounded retry: the UPSERT below reads before it writes, so a
            // concurrent writer (another replica, or an on-demand call racing the timer) can
            // insert the same (Date, MetricKey) in between. Reloading and re-applying as an
            // update is correct; surfacing UX_DailyStateSnapshots_Date_MetricKey to the caller
            // is not, because this method is reachable from the request path.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    var persisted = await PersistSnapshotAsync(date, metrics, computedAt, cancellationToken);

                    _logger.LogInformation(
                        "Daily state snapshot for {Date:yyyy-MM-dd} persisted successfully ({Count} metrics).",
                        date, persisted.Count);

                    return persisted;
                }
                catch (DbUpdateException ex) when (attempt < MaxPersistAttempts)
                {
                    _logger.LogWarning(ex,
                        "Concurrent write detected while persisting the snapshot for {Date:yyyy-MM-dd} (attempt {Attempt}/{MaxAttempts}). Reloading and retrying as an update.",
                        date, attempt, MaxPersistAttempts);
                }
            }
        }

        /// <inheritdoc />
        public async Task<List<DailyStateSnapshot>> GetSnapshotsAsync(
            DateTime fromDate,
            DateTime toDate,
            CancellationToken cancellationToken = default)
        {
            using var scope = _serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var from = fromDate.Date;
            var to = toDate.Date;

            return await context.DailyStateSnapshots
                .AsNoTracking()
                .Where(s => s.Date >= from && s.Date <= to)
                .OrderBy(s => s.Date)
                .ThenBy(s => s.MetricKey)
                .ToListAsync(cancellationToken);
        }

        /// <inheritdoc />
        public async Task<SnapshotGapReport> GetGapReportAsync(
            DateTime fromDate,
            DateTime toDate,
            CancellationToken cancellationToken = default)
        {
            using var scope = _serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var requestedFrom = fromDate.Date;
            var requestedTo = toDate.Date;

            var earliest = await context.DailyStateSnapshots
                .AsNoTracking()
                .OrderBy(s => s.Date)
                .Select(s => (DateTime?)s.Date)
                .FirstOrDefaultAsync(cancellationToken);

            // Nothing captured yet: the whole range is "not measured", which is not the same
            // as a gap. Reporting hundreds of missing dates from before the service existed
            // would be noise, not a finding.
            if (earliest is null)
            {
                return new SnapshotGapReport
                {
                    DataAvailableFromUtc = null,
                    FromDate = requestedFrom,
                    ToDate = requestedTo,
                    ExpectedMetricsPerDate = ExpectedMetricKeys.Length
                };
            }

            var from = requestedFrom < earliest.Value ? earliest.Value : requestedFrom;
            var today = DateTime.UtcNow.Date;
            var to = requestedTo > today ? today : requestedTo;

            var counts = await context.DailyStateSnapshots
                .AsNoTracking()
                .Where(s => s.Date >= from && s.Date <= to)
                .GroupBy(s => s.Date)
                .Select(g => new { Date = g.Key, Keys = g.Select(x => x.MetricKey).Distinct().Count() })
                .ToListAsync(cancellationToken);

            var countByDate = counts.ToDictionary(c => c.Date, c => c.Keys);

            var missing = new List<DateTime>();
            var incomplete = new List<DateTime>();

            for (var day = from; day <= to; day = day.AddDays(1))
            {
                if (!countByDate.TryGetValue(day, out var keyCount))
                {
                    missing.Add(day);
                }
                else if (keyCount < ExpectedMetricKeys.Length)
                {
                    incomplete.Add(day);
                }
            }

            return new SnapshotGapReport
            {
                DataAvailableFromUtc = earliest,
                FromDate = from,
                ToDate = to,
                MissingDates = missing,
                IncompleteDates = incomplete,
                ExpectedMetricsPerDate = ExpectedMetricKeys.Length
            };
        }

        private async Task<Dictionary<string, double>> ComputeMetricsAsync(CancellationToken cancellationToken)
        {
            using var scope = _serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var pendingCount = await context.Users
                .AsNoTracking()
                .CountAsync(u => u.Status == UserStatus.Pending, cancellationToken);

            var rerecordCount = await context.Users
                .AsNoTracking()
                .CountAsync(u => u.Status == UserStatus.ReRecord, cancellationToken);

            var activeCount = await context.Users
                .AsNoTracking()
                .CountAsync(u => u.Status == UserStatus.Active, cancellationToken);

            var activeWithFcm = await context.Users
                .AsNoTracking()
                .CountAsync(u => u.Status == UserStatus.Active && !string.IsNullOrEmpty(u.FcmToken), cancellationToken);

            var openReportsCount = await context.Reports
                .AsNoTracking()
                .CountAsync(r => r.Status == "Open", cancellationToken);

            // Both sides of the coverage ratio are stored as counts. Per the read-model design
            // rule, a rate is derived at read time from summed numerator and denominator --
            // storing only the rounded ratio would make the underlying user count unrecoverable
            // as the platform grows, and this metric exists to catch an FCM delivery regression
            // of the kind fixed in dc1c933, where the absolute number is the actionable figure.
            var fcmCoverage = activeCount > 0
                ? Math.Round((double)activeWithFcm / activeCount, 4)
                : 0.0;

            return new Dictionary<string, double>
            {
                { MetricPendingQueue, pendingCount },
                { MetricRerecordQueue, rerecordCount },
                { MetricActiveUsersTotal, activeCount },
                { MetricActiveUsersWithFcm, activeWithFcm },
                { MetricFcmTokenCoverage, fcmCoverage },
                { MetricOpenReports, openReportsCount }
            };
        }

        private async Task<List<DailyStateSnapshot>> LoadSnapshotsForDateAsync(
            DateTime date,
            CancellationToken cancellationToken)
        {
            using var scope = _serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            return await context.DailyStateSnapshots
                .AsNoTracking()
                .Where(s => s.Date == date)
                .OrderBy(s => s.MetricKey)
                .ToListAsync(cancellationToken);
        }

        /// <summary>
        /// One read-modify-write pass in its own scope, so a failed attempt leaves no dirty
        /// change tracker behind for the retry.
        /// </summary>
        private async Task<List<DailyStateSnapshot>> PersistSnapshotAsync(
            DateTime date,
            Dictionary<string, double> metrics,
            DateTime computedAt,
            CancellationToken cancellationToken)
        {
            using var scope = _serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var existingSnapshots = await context.DailyStateSnapshots
                .Where(s => s.Date == date)
                .ToListAsync(cancellationToken);

            var existingMap = existingSnapshots.ToDictionary(s => s.MetricKey, s => s);
            var resultList = new List<DailyStateSnapshot>();

            foreach (var (metricKey, value) in metrics)
            {
                if (existingMap.TryGetValue(metricKey, out var existing))
                {
                    existing.Value = value;
                    existing.ComputedAtUtc = computedAt;
                    resultList.Add(existing);
                }
                else
                {
                    var newSnapshot = new DailyStateSnapshot
                    {
                        Date = date,
                        MetricKey = metricKey,
                        Value = value,
                        ComputedAtUtc = computedAt
                    };
                    context.DailyStateSnapshots.Add(newSnapshot);
                    resultList.Add(newSnapshot);
                }
            }

            await context.SaveChangesAsync(cancellationToken);

            return resultList;
        }

        private async Task LogGapsAsync(CancellationToken cancellationToken)
        {
            try
            {
                var today = DateTime.UtcNow.Date;
                var report = await GetGapReportAsync(today.AddDays(-GapScanDays), today, cancellationToken);

                if (!report.HasGaps)
                {
                    return;
                }

                // These holes are permanent: state counts cannot be reconstructed after the
                // fact, so this is logged as a warning rather than silently tolerated.
                _logger.LogWarning(
                    "DailyStateSnapshots has gaps in the last {Days} days: {MissingCount} missing date(s) [{Missing}], {IncompleteCount} incomplete date(s) [{Incomplete}]. " +
                    "State counts cannot be backfilled, so these dates are permanently unavailable and must render as gaps, not zeros.",
                    GapScanDays,
                    report.MissingDates.Count,
                    string.Join(", ", report.MissingDates.Select(d => d.ToString("yyyy-MM-dd"))),
                    report.IncompleteDates.Count,
                    string.Join(", ", report.IncompleteDates.Select(d => d.ToString("yyyy-MM-dd"))));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to evaluate DailyStateSnapshots gap report.");
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("StateSnapshotService background runner starting...");

            // Stagger initial run by 2 minutes on container startup to prevent contention
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var isStartupRun = true;

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // The startup run must not overwrite an existing reading for today: it fires
                    // at whatever hour the container happened to start, and queue depth varies
                    // through the day. Scheduled runs always write.
                    await CaptureSnapshotAsync(DateTime.UtcNow.Date, isStartupRun, stoppingToken);
                    isStartupRun = false;

                    await LogGapsAsync(stoppingToken);

                    // Next run at the configured hour (Analytics:SnapshotHourUtc), 15 past.
                    var now = DateTime.UtcNow;
                    var hour = _options.SnapshotHourUtc is >= 0 and <= 23 ? _options.SnapshotHourUtc : 0;
                    var nextRun = now.Date.AddDays(1).AddHours(hour).AddMinutes(15);
                    var delay = nextRun - now;
                    if (delay <= TimeSpan.Zero)
                    {
                        delay = TimeSpan.FromMinutes(15);
                    }

                    _logger.LogInformation("StateSnapshotService scheduled next run at {NextRun:yyyy-MM-dd HH:mm:ss} UTC (in {Hours:N1} hours).", nextRun, delay.TotalHours);
                    await Task.Delay(delay, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to capture daily state snapshot. Will retry in 15 minutes.");
                    isStartupRun = false;
                    try
                    {
                        await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }
    }
}
