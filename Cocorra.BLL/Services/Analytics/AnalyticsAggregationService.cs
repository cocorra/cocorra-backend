using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cocorra.BLL.Services.EventTracking;
using Cocorra.DAL.Data;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;
using Cocorra.DAL.Models.Analytics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cocorra.BLL.Services.Analytics
{
    /// <summary>
    /// AN-015: AnalyticsAggregationService performs hourly idempotent rollups of raw events
    /// into Read Models (RM-1..RM-4) with transactional watermark checkpointing on UserEvent.Id.
    /// </summary>
    public partial class AnalyticsAggregationService : BackgroundService
    {
        public const string PipelineName = "daily_analytics_rollup";

        /// <summary>
        /// How far back from "now" the reader stops, to let in-flight inserts commit before
        /// their identity values are stepped over. The flush service batches, so an event can
        /// be inserted seconds after it occurred.
        /// </summary>
        public const int AggregationSafetyLagSeconds = 120;

        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<AnalyticsAggregationService> _logger;
        private readonly EventTrackingOptions _options;

        public AnalyticsAggregationService(
            IServiceScopeFactory scopeFactory,
            ILogger<AnalyticsAggregationService> logger,
            IOptions<EventTrackingOptions>? options = null)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
            _options = options?.Value ?? new EventTrackingOptions();
        }

        protected override async Task ExecuteAsync(CancellationToken ct)
        {
            _logger.LogInformation("AnalyticsAggregationService starting (stagger delay: 2 minutes)...");

            // Stagger initial run by 2 minutes
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(2), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await PerformAggregationCycleAsync(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "AnalyticsAggregationService: Error during aggregation cycle.");
                }

                // Schedule next hourly cycle
                try
                {
                    var intervalMinutes = _options.AggregationIntervalMinutes > 0 ? _options.AggregationIntervalMinutes : 60;
                    await Task.Delay(TimeSpan.FromMinutes(intervalMinutes), ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        public async Task<int> PerformAggregationCycleAsync(CancellationToken ct = default)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // 1. Get or initialize watermark checkpoint
            var checkpoint = await db.AggregationCheckpoints
                .FirstOrDefaultAsync(c => c.PipelineName == PipelineName, ct);

            if (checkpoint == null)
            {
                checkpoint = new AggregationCheckpoint
                {
                    PipelineName = PipelineName,
                    LastProcessedEventId = 0,
                    LastSuccessAtUtc = DateTime.UtcNow
                };
                db.AggregationCheckpoints.Add(checkpoint);
                await db.SaveChangesAsync(ct);
            }

            var lastId = checkpoint.LastProcessedEventId;
            var batchSize = _options.AggregationBatchSize > 0 ? _options.AggregationBatchSize : 50_000;

            // 2. Read unprocessed raw events, holding back a safety lag.
            //
            // IDENTITY values are assigned before commit, so a row with Id 500 can become
            // visible AFTER Id 501. Advancing the watermark straight to MAX(Id) would skip 500
            // permanently and silently. Excluding the most recent few seconds of inserts gives
            // any in-flight transaction time to commit before its id is passed over.
            var safetyLagCutoff = DateTime.UtcNow.AddSeconds(-AggregationSafetyLagSeconds);

            var rawEvents = await db.UserEvents
                .AsNoTracking()
                .Where(e => e.Id > lastId && e.OccurredAtUtc <= safetyLagCutoff)
                .OrderBy(e => e.Id)
                .Take(batchSize)
                .ToListAsync(ct);

            if (rawEvents.Count == 0)
            {
                _logger.LogDebug("AnalyticsAggregationService: No new events past watermark {LastId}.", lastId);
                return 0;
            }

            _logger.LogInformation("AnalyticsAggregationService: Processing {Count} events past watermark {LastId}...", rawEvents.Count, lastId);

            var affectedDates = rawEvents
                .Select(e => e.OccurredAtUtc.Date)
                .Distinct()
                .OrderBy(d => d)
                .ToList();

            // 3. Roll up affected dates into the read models. Each grain is recomputed IN FULL
            // for the affected date rather than incremented, which is what makes a re-run
            // idempotent and lets a corrupted rollup be fixed by simply running again.
            foreach (var date in affectedDates)
            {
                await AggregateDailyPlatformMetricsAsync(db, date, ct);
                await AggregateDailyRoomMetricsAsync(db, date, ct);
                await AggregateDailyHostMetricsAsync(db, date, ct);
            }

            // Funnel cohorts are recomputed over a trailing window, not just the affected
            // dates: activation_completed arrives when an admin reviews, which can be days
            // after the cohort date, so a cohort's later steps keep changing after the fact.
            await AggregateFunnelCohortsAsync(db, ct);

            // 4. Advance watermark checkpoint
            var newLastId = rawEvents.Max(e => e.Id);
            checkpoint.LastProcessedEventId = newLastId;
            checkpoint.LastSuccessAtUtc = DateTime.UtcNow;
            checkpoint.ConsecutiveFailures = 0;

            await db.SaveChangesAsync(ct);

            _logger.LogInformation("AnalyticsAggregationService: Successfully advanced watermark to {NewLastId}.", newLastId);
            return rawEvents.Count;
        }

        internal static async Task AggregateDailyPlatformMetricsAsync(AppDbContext db, DateTime date, CancellationToken ct)
        {
            var nextDate = date.AddDays(1);

            // Compute counts for this date
            var roomsCreated = await db.Rooms
                .AsNoTracking()
                .CountAsync(r => r.CreatedAt >= date && r.CreatedAt < nextDate, ct);

            var roomsLive = await db.Rooms
                .AsNoTracking()
                .CountAsync(r => r.CreatedAt >= date && r.CreatedAt < nextDate && r.Status != RoomStatus.Scheduled, ct);

            var distinctHosts = await db.Rooms
                .AsNoTracking()
                .Where(r => r.CreatedAt >= date && r.CreatedAt < nextDate)
                .Select(r => r.HostId)
                .Distinct()
                .CountAsync(ct);

            var distinctJoiners = await db.RoomParticipants
                .AsNoTracking()
                .Where(p => p.JoinedAt >= date && p.JoinedAt < nextDate && p.UserId != p.Room!.HostId)
                .Select(p => p.UserId)
                .Distinct()
                .CountAsync(ct);

            var distinctSpeakers = await db.RoomParticipants
                .AsNoTracking()
                .Where(p => p.JoinedAt >= date && p.JoinedAt < nextDate && p.UserId != p.Room!.HostId && p.TotalSpokenSeconds > 0)
                .Select(p => p.UserId)
                .Distinct()
                .CountAsync(ct);

            var totalSpokenSeconds = await db.RoomParticipants
                .AsNoTracking()
                .Where(p => p.JoinedAt >= date && p.JoinedAt < nextDate && p.UserId != p.Room!.HostId)
                .SumAsync(p => (long)p.TotalSpokenSeconds, ct);

            var registrations = await db.Users
                .AsNoTracking()
                .CountAsync(u => u.CreatedAt >= date && u.CreatedAt < nextDate, ct);

            var voiceSubmitted = await db.UserEvents
                .AsNoTracking()
                .Where(e => e.EventType == EventTypes.VoiceVerificationSubmitted && e.OccurredAtUtc >= date && e.OccurredAtUtc < nextDate)
                .Select(e => e.UserId)
                .Distinct()
                .CountAsync(ct);

            var voiceApproved = await db.UserEvents
                .AsNoTracking()
                .Where(e => e.EventType == EventTypes.ActivationCompleted && e.OccurredAtUtc >= date && e.OccurredAtUtc < nextDate)
                .Select(e => e.UserId)
                .Distinct()
                .CountAsync(ct);

            // Upsert on DailyPlatformMetrics (Grain: Date)
            var existing = await db.DailyPlatformMetrics
                .FirstOrDefaultAsync(m => m.Date == date, ct);

            if (existing != null)
            {
                existing.RoomsCreated = roomsCreated;
                existing.RoomsGoneLive = roomsLive;
                existing.DistinctActiveHosts = distinctHosts;
                existing.DistinctJoiningUsers = distinctJoiners;
                existing.DistinctSpeakingUsers = distinctSpeakers;
                existing.TotalSpokenSeconds = totalSpokenSeconds;
                existing.NewRegistrations = registrations;
                existing.VoiceVerificationsSubmitted = voiceSubmitted;
                existing.VoiceVerificationsApproved = voiceApproved;
                existing.ComputedAtUtc = DateTime.UtcNow;
            }
            else
            {
                db.DailyPlatformMetrics.Add(new DailyPlatformMetrics
                {
                    Date = date,
                    RoomsCreated = roomsCreated,
                    RoomsGoneLive = roomsLive,
                    DistinctActiveHosts = distinctHosts,
                    DistinctJoiningUsers = distinctJoiners,
                    DistinctSpeakingUsers = distinctSpeakers,
                    TotalSpokenSeconds = totalSpokenSeconds,
                    NewRegistrations = registrations,
                    VoiceVerificationsSubmitted = voiceSubmitted,
                    VoiceVerificationsApproved = voiceApproved,
                    ComputedAtUtc = DateTime.UtcNow
                });
            }
        }
    }
}
