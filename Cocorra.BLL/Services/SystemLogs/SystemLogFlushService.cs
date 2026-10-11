using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Cocorra.DAL.Data;
using Cocorra.DAL.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cocorra.BLL.Services.SystemLogs
{
    /// <summary>
    /// Drains the system log queue into the database in batches, each through a fresh scope so
    /// a failed batch cannot leave tracked entities behind for the next one. On shutdown it
    /// flushes whatever is still queued, bounded by <see cref="DrainTimeout"/>.
    /// </summary>
    public class SystemLogFlushService : BackgroundService
    {
        /// <summary>Bound on the shutdown flush, which runs inside the host's own shutdown timeout.</summary>
        private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(10);

        private readonly ChannelReader<SystemLog> _queue;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<SystemLogFlushService> _logger;
        private readonly int _batchSize;

        public SystemLogFlushService(
            Channel<SystemLog> queue,
            IServiceScopeFactory scopeFactory,
            ILogger<SystemLogFlushService> logger,
            IOptions<SystemLogOptions> options)
        {
            _queue = queue.Reader;
            _scopeFactory = scopeFactory;
            _logger = logger;
            _batchSize = Math.Max(1, options.Value.BatchSize);
        }

        protected override async Task ExecuteAsync(CancellationToken ct)
        {
            var batch = new List<SystemLog>(capacity: _batchSize);

            try
            {
                while (await _queue.WaitToReadAsync(ct))
                {
                    FillBatch(batch);
                    await PersistAsync(batch, ct);
                    batch.Clear();
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("SystemLogFlushService stopping. Flushing queued system log entries...");
            }
            finally
            {
                await DrainAsync(batch);
            }
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            await base.StopAsync(cancellationToken);

            // ExecuteAsync never runs if the host stops before its scheduled start, so its drain is
            // not guaranteed; when it did run, this finds an already-empty queue.
            await DrainAsync(new List<SystemLog>(capacity: _batchSize));
        }

        private void FillBatch(List<SystemLog> batch)
        {
            while (batch.Count < _batchSize && _queue.TryRead(out var entry))
            {
                batch.Add(entry);
            }
        }

        private async Task DrainAsync(List<SystemLog> batch)
        {
            using var cts = new CancellationTokenSource(DrainTimeout);

            while (!cts.IsCancellationRequested)
            {
                FillBatch(batch);
                if (batch.Count == 0)
                {
                    return;
                }

                try
                {
                    await PersistAsync(batch, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning("Shutdown flush timed out; {Count} system log entries discarded.", batch.Count);
                    return;
                }

                batch.Clear();
            }
        }

        private async Task PersistAsync(List<SystemLog> batch, CancellationToken ct)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                db.SystemLogs.AddRange(batch);
                await db.SaveChangesAsync(ct);
            }
            // Cancellation is not a persistence failure: it must reach the caller with the batch
            // still populated, so the shutdown drain can retry it instead of it being cleared.
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Logged under this service's category, which the database sink excludes, so an
                // unreachable database cannot keep feeding the queue it is failing to drain.
                _logger.LogError(ex, "Failed to persist {Count} system log entries; they are discarded.", batch.Count);
            }
        }
    }
}
