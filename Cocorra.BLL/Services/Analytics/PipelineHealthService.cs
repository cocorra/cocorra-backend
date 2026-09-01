using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cocorra.BLL.Services.EventTracking;
using Cocorra.DAL.Data;
using Cocorra.DAL.DTOS.AnalyticsDto;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cocorra.BLL.Services.Analytics
{
    public interface IPipelineHealthService
    {
        Task<PipelineHealthDto> GetHealthAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// AN-025: assembles the pipeline health report from the in-process counters plus the
    /// durable checkpoint, dead-letter and snapshot state.
    /// </summary>
    public class PipelineHealthService : IPipelineHealthService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly EventPipelineMetrics _metrics;
        private readonly IStateSnapshotService _snapshots;
        private static readonly DateTime ProcessStartedUtc = DateTime.UtcNow;

        /// <summary>Aggregation runs hourly; more than this behind means it has stopped.</summary>
        private const double StaleAggregationHours = 3;

        public PipelineHealthService(
            IServiceProvider serviceProvider,
            EventPipelineMetrics metrics,
            IStateSnapshotService snapshots)
        {
            _serviceProvider = serviceProvider;
            _metrics = metrics;
            _snapshots = snapshots;
        }

        public async Task<PipelineHealthDto> GetHealthAsync(CancellationToken cancellationToken = default)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var checkpoint = await db.AggregationCheckpoints
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.PipelineName == AnalyticsAggregationService.PipelineName, cancellationToken);

            var deadLetterBacklog = await db.DeadLetterEvents.CountAsync(cancellationToken);

            var watermark = checkpoint?.LastProcessedEventId ?? 0;
            var unaggregated = await db.UserEvents.LongCountAsync(e => e.Id > watermark, cancellationToken);

            var today = DateTime.UtcNow.Date;
            var gapReport = await _snapshots.GetGapReportAsync(today.AddDays(-30), today, cancellationToken);

            var lastSnapshot = await db.DailyStateSnapshots
                .AsNoTracking()
                .OrderByDescending(s => s.Date)
                .Select(s => (DateTime?)s.Date)
                .FirstOrDefaultAsync(cancellationToken);

            var lagHours = checkpoint is null
                ? (double?)null
                : Math.Round((DateTime.UtcNow - checkpoint.LastSuccessAtUtc).TotalHours, 2);

            var warnings = new List<string>();

            if (checkpoint is null)
            {
                warnings.Add("Aggregation has never run: no checkpoint exists.");
            }
            else if (lagHours > StaleAggregationHours)
            {
                warnings.Add($"Aggregation last succeeded {lagHours:N1}h ago (expected hourly). Read models are stale.");
            }

            if (checkpoint?.ConsecutiveFailures > 0)
            {
                warnings.Add($"Aggregation has failed {checkpoint.ConsecutiveFailures} time(s) in a row.");
            }

            if (deadLetterBacklog > 0)
            {
                warnings.Add($"{deadLetterBacklog} event(s) in the dead-letter store awaiting investigation.");
            }

            if (_metrics.EventsDroppedOnEnqueue > 0)
            {
                warnings.Add(
                    $"{_metrics.EventsDroppedOnEnqueue} event(s) dropped on enqueue since this instance started: " +
                    "the channel is saturating. Raise Analytics:EventChannelCapacity or investigate flush throughput.");
            }

            if (gapReport.HasGaps)
            {
                warnings.Add(
                    $"DailyStateSnapshots has {gapReport.MissingDates.Count} missing and " +
                    $"{gapReport.IncompleteDates.Count} incomplete date(s) in the last 30 days. " +
                    "State counts cannot be backfilled, so these are permanent.");
            }

            return new PipelineHealthDto
            {
                // Healthy means nothing is wrong, not merely that the API answered.
                PipelineHealthy = warnings.Count == 0,
                Warnings = warnings,

                EventsEnqueued = _metrics.EventsEnqueued,
                EventsPersisted = _metrics.EventsPersisted,
                EventsDroppedOnEnqueue = _metrics.EventsDroppedOnEnqueue,
                FlushBatchesRetried = _metrics.FlushBatchesRetried,
                FlushBatchesFailed = _metrics.FlushBatchesFailed,
                EventsDeadLettered = _metrics.EventsDeadLettered,
                DuplicateEventsDiscarded = _metrics.DuplicateEventsDiscarded,
                CountersSinceUtc = ProcessStartedUtc,

                DeadLetterBacklog = deadLetterBacklog,
                LastAggregationSuccessUtc = checkpoint?.LastSuccessAtUtc,
                AggregationLagHours = lagHours,
                AggregationConsecutiveFailures = checkpoint?.ConsecutiveFailures ?? 0,
                AggregationWatermarkEventId = watermark,
                UnaggregatedEventCount = unaggregated,

                LastSnapshotDate = lastSnapshot,
                SnapshotGapDates = gapReport.MissingDates.Select(d => d.ToString("yyyy-MM-dd")).ToList()
            };
        }
    }
}
