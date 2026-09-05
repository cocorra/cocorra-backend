using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cocorra.DAL.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cocorra.BLL.Services.Analytics
{
    public class BackfillResult
    {
        public DateTime FromDate { get; set; }
        public DateTime ToDate { get; set; }
        public int DatesProcessed { get; set; }
        public int DatesSkipped { get; set; }
        public bool Completed { get; set; }

        /// <summary>Where to resume from if the run was interrupted or throttled out.</summary>
        public DateTime? ResumeFromDate { get; set; }

        public List<string> Notes { get; set; } = new();
    }

    public interface IAnalyticsBackfillService
    {
        Task<BackfillResult> BackfillAsync(
            DateTime fromDate,
            DateTime toDate,
            bool force = false,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// AN-016 — Backfill.
    ///
    /// Runs the SAME rollup code path as the live aggregation service rather than a parallel
    /// implementation. That is the whole design: if backfill had its own queries, backfilled
    /// rows and live-aggregated rows could diverge, and nobody would know which was right.
    ///
    /// Deliberately NOT a BackgroundService. Backfill is an operator action with a real cost
    /// on a database that is also serving traffic; it should be invoked explicitly, against a
    /// restored copy first, not fired automatically at container start.
    ///
    /// What can and cannot be reconstructed:
    ///   RM-1 platform  — full, within the 180-day raw event window
    ///   RM-2 rooms     — PARTIAL: joiners, speakers and reports yes; hand raises, stage
    ///                    promotions and per-room speaking seconds were never captured
    ///   RM-3 hosts     — FULL HISTORY: derives from Rooms, which is never purged
    ///   RM-4 funnel    — onboarding steps only, within the event window
    ///   RM-5 snapshots — NOTHING. Pure state cannot be reconstructed after the fact, and
    ///                    fabricating it would be inventing history.
    /// </summary>
    public class AnalyticsBackfillService : IAnalyticsBackfillService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<AnalyticsBackfillService> _logger;

        /// <summary>Pause between dates so a backfill cannot starve live ingestion.</summary>
        private const int InterDateDelayMs = 250;

        /// <summary>Upper bound on a single invocation, so one call cannot run unbounded.</summary>
        private const int MaxDatesPerRun = 400;

        public AnalyticsBackfillService(
            IServiceScopeFactory scopeFactory,
            ILogger<AnalyticsBackfillService> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        public async Task<BackfillResult> BackfillAsync(
            DateTime fromDate,
            DateTime toDate,
            bool force = false,
            CancellationToken cancellationToken = default)
        {
            var from = fromDate.Date;
            var to = toDate.Date;
            var today = DateTime.UtcNow.Date;

            if (to > today)
            {
                to = today;
            }

            var result = new BackfillResult { FromDate = from, ToDate = to };

            if (from > to)
            {
                result.Completed = true;
                result.Notes.Add("Empty range: nothing to do.");
                return result;
            }

            result.Notes.Add(
                "RM-5 DailyStateSnapshots is not backfilled. State counts cannot be reconstructed " +
                "after the fact, and inventing them would fabricate history.");
            result.Notes.Add(
                "RM-2 hand raises, stage promotions and per-room speaking seconds remain 0: those " +
                "events were never captured and are not recoverable.");

            var processed = 0;
            var skipped = 0;

            // Oldest first, one date per transaction, so an interruption leaves a clean prefix
            // of completed dates rather than a partial write somewhere in the middle.
            for (var date = from; date <= to; date = date.AddDays(1))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    result.ResumeFromDate = date;
                    result.Notes.Add("Cancelled; resume from the date above.");
                    return result;
                }

                if (processed + skipped >= MaxDatesPerRun)
                {
                    result.ResumeFromDate = date;
                    result.Notes.Add($"Run capped at {MaxDatesPerRun} dates; resume from the date above.");
                    return result;
                }

                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                if (!force && await db.DailyPlatformMetrics.AnyAsync(m => m.Date == date, cancellationToken))
                {
                    skipped++;
                    continue;
                }

                try
                {
                    // Identical calls to the live path. INV-5: recomputing a date in full is
                    // idempotent, so a re-run produces byte-identical rows.
                    await AnalyticsAggregationService.AggregateDailyPlatformMetricsAsync(db, date, cancellationToken);
                    await AnalyticsAggregationService.AggregateDailyRoomMetricsAsync(db, date, cancellationToken);
                    await AnalyticsAggregationService.AggregateDailyHostMetricsAsync(db, date, cancellationToken);

                    await db.SaveChangesAsync(cancellationToken);
                    processed++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Backfill failed on {Date:yyyy-MM-dd}. Stopping so the range stays resumable.", date);
                    result.ResumeFromDate = date;
                    result.Notes.Add($"Failed on {date:yyyy-MM-dd}: {ex.Message}");
                    result.DatesProcessed = processed;
                    result.DatesSkipped = skipped;
                    return result;
                }

                // Throttle: this competes with live ingestion on the same tables.
                try
                {
                    await Task.Delay(InterDateDelayMs, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    result.ResumeFromDate = date.AddDays(1);
                    result.DatesProcessed = processed;
                    result.DatesSkipped = skipped;
                    return result;
                }
            }

            result.DatesProcessed = processed;
            result.DatesSkipped = skipped;
            result.Completed = true;

            _logger.LogInformation(
                "Backfill complete for {From:yyyy-MM-dd}..{To:yyyy-MM-dd}: {Processed} processed, {Skipped} already present.",
                from, to, processed, skipped);

            return result;
        }
    }
}
