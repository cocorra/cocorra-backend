using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cocorra.DAL.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cocorra.BLL.Services.EventTracking
{
    /// <summary>
    /// AN-004: Batched, configurable event retention cleanup service.
    /// Deletes raw events older than RawEventRetentionDays in bounded batches
    /// during low-traffic windows with staggered startup to avoid competing with ingestion.
    /// </summary>
    public class EventCleanupService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<EventCleanupService> _logger;
        private readonly EventTrackingOptions _options;

        public EventCleanupService(
            IServiceScopeFactory scopeFactory,
            ILogger<EventCleanupService> logger,
            IOptions<EventTrackingOptions>? options = null)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
            _options = options?.Value ?? new EventTrackingOptions();
        }

        protected override async Task ExecuteAsync(CancellationToken ct)
        {
            _logger.LogInformation("EventCleanupService started. Startup delay of 5 minutes active...");

            // Stagger startup by 5 minutes to avoid container boot contention
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(5), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await PerformBatchedPurgeAsync(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "EventCleanupService: Error occurred during user event purge cycle.");
                }

                // Schedule next daily run
                try
                {
                    var now = DateTime.UtcNow;
                    var nextRun = now.Date.AddDays(1).AddHours(2); // 02:00 UTC
                    var delay = nextRun - now;
                    if (delay <= TimeSpan.Zero)
                    {
                        delay = TimeSpan.FromHours(24);
                    }

                    _logger.LogInformation("EventCleanupService: Next purge scheduled for {NextRun:yyyy-MM-dd HH:mm:ss} UTC.", nextRun);
                    await Task.Delay(delay, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        public async Task<int> PerformBatchedPurgeAsync(CancellationToken ct = default)
        {
            var retentionDays = _options.RawEventRetentionDays > 0 ? _options.RawEventRetentionDays : 180;
            var batchSize = _options.CleanupBatchSize > 0 ? _options.CleanupBatchSize : 5000;
            var cutoff = DateTime.UtcNow.AddDays(-retentionDays);

            _logger.LogInformation("EventCleanupService: Starting batched purge for events older than {Cutoff:yyyy-MM-dd HH:mm:ss} (retention: {Days} days, batch: {BatchSize})...", cutoff, retentionDays, batchSize);

            var totalDeleted = 0;
            var batchesCount = 0;

            while (!ct.IsCancellationRequested)
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                // Select a batch of expired IDs to delete in chunks
                var expiredIds = await db.UserEvents
                    .AsNoTracking()
                    .Where(e => e.OccurredAtUtc < cutoff)
                    .OrderBy(e => e.Id)
                    .Select(e => e.Id)
                    .Take(batchSize)
                    .ToListAsync(ct);

                if (expiredIds.Count == 0)
                {
                    break;
                }

                var deleted = await db.UserEvents
                    .Where(e => expiredIds.Contains(e.Id))
                    .ExecuteDeleteAsync(ct);

                totalDeleted += deleted;
                batchesCount++;

                if (deleted < batchSize)
                {
                    break;
                }

                // Inter-batch pause to yield locks and give ingestion headroom
                await Task.Delay(TimeSpan.FromMilliseconds(50), ct);
            }

            if (totalDeleted > 0)
            {
                _logger.LogInformation("EventCleanupService: Purged {TotalDeleted} events across {Batches} batches.", totalDeleted, batchesCount);
            }
            else
            {
                _logger.LogInformation("EventCleanupService: Zero expired events found past cutoff.");
            }

            return totalDeleted;
        }
    }
}
