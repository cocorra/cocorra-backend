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

namespace Cocorra.BLL.Services.SystemLogs
{
    /// <summary>
    /// Deletes system log rows older than SystemLogs:RetentionDays once a day, in bounded batches
    /// so a large backlog never becomes one long-running, lock-heavy delete.
    /// </summary>
    public class SystemLogCleanupService : BackgroundService
    {
        // Staggered after EventCleanupService (02:00 UTC) so the two purges never compete.
        private const int DailyRunHourUtc = 3;
        private const int PurgeBatchSize = 5_000;
        private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan InterBatchPause = TimeSpan.FromMilliseconds(50);

        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<SystemLogCleanupService> _logger;
        private readonly int _retentionDays;

        public SystemLogCleanupService(
            IServiceScopeFactory scopeFactory,
            ILogger<SystemLogCleanupService> logger,
            IOptions<SystemLogOptions> options)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
            _retentionDays = Math.Max(1, options.Value.RetentionDays);
        }

        protected override async Task ExecuteAsync(CancellationToken ct)
        {
            try
            {
                // Stagger startup to avoid container boot contention.
                await Task.Delay(StartupDelay, ct);

                while (!ct.IsCancellationRequested)
                {
                    await RunPurgeCycleAsync(ct);
                    await Task.Delay(DelayUntilNextRun(), ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                _logger.LogInformation("SystemLogCleanupService stopped.");
            }
        }

        private async Task RunPurgeCycleAsync(CancellationToken ct)
        {
            try
            {
                await PerformBatchedPurgeAsync(ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogError(ex, "SystemLogCleanupService: Error occurred during system log purge cycle.");
            }
        }

        private static TimeSpan DelayUntilNextRun()
        {
            var now = DateTime.UtcNow;
            return now.Date.AddDays(1).AddHours(DailyRunHourUtc) - now;
        }

        public async Task<int> PerformBatchedPurgeAsync(CancellationToken ct = default)
        {
            var cutoff = DateTime.UtcNow.AddDays(-_retentionDays);
            var totalDeleted = 0;

            while (!ct.IsCancellationRequested)
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                var expiredIds = await db.SystemLogs
                    .AsNoTracking()
                    .Where(l => l.TimestampUtc < cutoff)
                    .OrderBy(l => l.Id)
                    .Select(l => l.Id)
                    .Take(PurgeBatchSize)
                    .ToListAsync(ct);

                if (expiredIds.Count == 0)
                {
                    break;
                }

                var deleted = await db.SystemLogs
                    .Where(l => expiredIds.Contains(l.Id))
                    .ExecuteDeleteAsync(ct);

                totalDeleted += deleted;

                if (deleted < PurgeBatchSize)
                {
                    break;
                }

                // Yield locks between batches so log ingestion keeps flowing.
                await Task.Delay(InterBatchPause, ct);
            }

            _logger.LogInformation(
                "SystemLogCleanupService: Purged {TotalDeleted} system log entries older than {Cutoff:yyyy-MM-dd HH:mm:ss} UTC.",
                totalDeleted, cutoff);

            return totalDeleted;
        }
    }
}
