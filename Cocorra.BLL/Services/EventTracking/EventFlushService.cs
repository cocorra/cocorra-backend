using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Cocorra.DAL.Data;
using Cocorra.DAL.Models;
using Cocorra.DAL.Models.Analytics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cocorra.BLL.Services.EventTracking
{
    /// <summary>
    /// AN-003: Hardened event flush service with failure classification, bounded retry,
    /// duplicate-key per-row fallback, dead-letter queueing on permanent failure, and
    /// graceful shutdown channel draining.
    ///
    /// The classification step is what makes the rest safe. EF Core wraps almost every
    /// provider failure during SaveChangesAsync in DbUpdateException — deadlocks, command
    /// timeouts and constraint violations alike — so treating DbUpdateException as
    /// "duplicate key" would silently discard a batch on any transient database fault, which
    /// is quieter than the discard-on-failure behaviour this service replaced.
    /// </summary>
    public class EventFlushService : BackgroundService
    {
        private readonly Channel<UserEvent> _queue;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<EventFlushService> _logger;
        private readonly EventTrackingOptions _options;
        private readonly EventPipelineMetrics _metrics;

        /// <summary>Bound on how long a shutdown persist attempt may take.</summary>
        private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(10);

        public EventFlushService(
            Channel<UserEvent> queue,
            IServiceScopeFactory scopeFactory,
            ILogger<EventFlushService> logger,
            IOptions<EventTrackingOptions>? options = null,
            EventPipelineMetrics? metrics = null)
        {
            _queue = queue;
            _scopeFactory = scopeFactory;
            _logger = logger;
            _options = options?.Value ?? new EventTrackingOptions();
            _metrics = metrics ?? new EventPipelineMetrics();
        }

        private int BatchSize => _options.EventFlushBatchSize > 0 ? _options.EventFlushBatchSize : 100;

        protected override async Task ExecuteAsync(CancellationToken ct)
        {
            var batchSize = BatchSize;
            var batch = new List<UserEvent>(capacity: batchSize);

            try
            {
                while (await _queue.Reader.WaitToReadAsync(ct))
                {
                    while (batch.Count < batchSize && _queue.Reader.TryRead(out var evt))
                    {
                        batch.Add(evt);
                    }

                    if (batch.Count == 0)
                    {
                        continue;
                    }

                    await ProcessBatchWithRetryAsync(batch, ct);
                    batch.Clear();
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("EventFlushService cancellation requested. Draining in-flight channel events...");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in EventFlushService execution loop.");
            }
            finally
            {
                // Anything already read out of the channel but not yet persisted would
                // otherwise disappear with the process.
                if (batch.Count > 0)
                {
                    await PersistOrDeadLetterOnShutdownAsync(batch);
                    batch.Clear();
                }

                await DrainRemainingEventsAsync();
            }
        }

        /// <summary>
        /// Persists a batch, classifying failures so each class gets the response it needs:
        /// duplicate key to the per-row fallback, transient to bounded retry, everything else
        /// (and exhausted retries) to the dead-letter store. Never discards silently.
        /// </summary>
        private async Task ProcessBatchWithRetryAsync(List<UserEvent> batch, CancellationToken ct)
        {
            var retries = 0;
            var maxRetries = _options.EventFlushMaxRetries;
            var backoffMs = _options.EventFlushInitialBackoffMs > 0 ? _options.EventFlushInitialBackoffMs : 200;

            while (true)
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                    db.UserEvents.AddRange(batch);
                    await db.SaveChangesAsync(ct);

                    _metrics.RecordPersisted(batch.Count);
                    return; // Batch persisted successfully
                }
                catch (DbUpdateException dbEx) when (IsDuplicateKeyViolation(dbEx))
                {
                    // At least one EventId already exists. AddRange + one SaveChangesAsync is
                    // all-or-nothing, so without this fallback a single duplicate would discard
                    // the whole batch — a 99-event loss path strictly worse than the defect
                    // AN-002 set out to fix.
                    _logger.LogWarning(
                        "Duplicate EventId in batch of {Count}; falling back to per-row insert so the non-colliding rows survive.",
                        batch.Count);
                    await FallbackPerRowInsertAsync(batch, ct);
                    return;
                }
                catch (Exception ex) when (IsRetryable(ex) && !ct.IsCancellationRequested && retries < maxRetries)
                {
                    retries++;
                    _metrics.RecordBatchRetried();

                    var delay = TimeSpan.FromMilliseconds(backoffMs * Math.Pow(2, retries - 1));
                    _logger.LogWarning(ex,
                        "Transient error persisting event batch of {Count}. Retry {Retry}/{MaxRetries} after {Delay}ms.",
                        batch.Count, retries, maxRetries, delay.TotalMilliseconds);

                    try
                    {
                        await Task.Delay(delay, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        // Shutdown landed mid-backoff. Returning here would drop the batch, so
                        // dead-letter it instead: the point of this service is that a failed
                        // flush always leaves a record.
                        _logger.LogWarning(
                            "Shutdown during retry backoff; dead-lettering {Count} undelivered events.", batch.Count);
                        await RouteToDeadLetterAsync(batch, "Cancelled during retry backoff");
                        return;
                    }
                }
                catch (Exception fatalEx)
                {
                    _metrics.RecordBatchFailed();
                    _logger.LogError(fatalEx,
                        "Exhausted retries or permanent error persisting batch of {Count} events. Routing to dead-letter.",
                        batch.Count);
                    await RouteToDeadLetterAsync(batch, fatalEx.Message);
                    return;
                }
            }
        }

        /// <summary>
        /// Fallback per-row insert: only a genuine duplicate-key collision is discarded.
        /// Anything else is dead-lettered — misreading a transient fault as a duplicate here
        /// is exactly how a batch would disappear without a trace.
        /// </summary>
        private async Task FallbackPerRowInsertAsync(List<UserEvent> batch, CancellationToken ct)
        {
            var persisted = 0;

            foreach (var evt in batch)
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                    db.UserEvents.Add(evt);
                    await db.SaveChangesAsync(ct);
                    persisted++;
                }
                catch (DbUpdateException dbEx) when (IsDuplicateKeyViolation(dbEx))
                {
                    // The idempotency guarantee working as designed.
                    _metrics.RecordDuplicateDiscarded();
                    _logger.LogDebug("Duplicate EventId {EventId} discarded in per-row fallback.", evt.EventId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Failed to persist event {EventId} during per-row fallback ({Reason}). Routing to dead-letter.",
                        evt.EventId, ex.GetType().Name);
                    await RouteToDeadLetterAsync(new List<UserEvent> { evt }, ex.Message);
                }
            }

            if (persisted > 0)
            {
                _metrics.RecordPersisted(persisted);
            }
        }

        /// <summary>
        /// Last-chance persist on shutdown: one attempt, then dead-letter. No retry loop,
        /// because the host is already counting down its own shutdown timeout.
        /// </summary>
        private async Task PersistOrDeadLetterOnShutdownAsync(List<UserEvent> batch)
        {
            using var cts = new CancellationTokenSource(DrainTimeout);

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                db.UserEvents.AddRange(batch);
                await db.SaveChangesAsync(cts.Token);
                _metrics.RecordPersisted(batch.Count);
            }
            catch (DbUpdateException dbEx) when (IsDuplicateKeyViolation(dbEx))
            {
                await FallbackPerRowInsertAsync(batch, cts.Token);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Shutdown flush failed for {Count} events. Routing to dead-letter.", batch.Count);
                await RouteToDeadLetterAsync(batch, $"Shutdown flush failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Writes dead letters one scope per chunk. The per-event version meant a failing
        /// 100-event batch opened 100 scopes and 100 round trips against a database that had
        /// just proven unhealthy.
        /// </summary>
        private async Task RouteToDeadLetterAsync(List<UserEvent> events, string reason)
        {
            if (events.Count == 0)
            {
                return;
            }

            var truncatedReason = reason.Length > 1000 ? reason.Substring(0, 1000) : reason;
            var chunkSize = BatchSize;

            for (var offset = 0; offset < events.Count; offset += chunkSize)
            {
                var chunk = events.Skip(offset).Take(chunkSize).ToList();

                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                    db.DeadLetterEvents.AddRange(chunk.Select(evt => new DeadLetterEvent
                    {
                        EventId = evt.EventId,
                        EventType = evt.EventType,
                        UserId = evt.UserId,
                        RoomId = evt.RoomId,
                        PropertiesJson = evt.PropertiesJson,
                        OccurredAtUtc = evt.OccurredAtUtc,
                        FailureReason = truncatedReason,
                        DeadLetteredAtUtc = DateTime.UtcNow
                    }));

                    await db.SaveChangesAsync();
                    _metrics.RecordDeadLettered(chunk.Count);
                }
                catch (Exception ex)
                {
                    _logger.LogCritical(ex,
                        "CRITICAL: failed to write {Count} events to the dead-letter store. These events are lost.",
                        chunk.Count);
                }
            }
        }

        /// <summary>
        /// Drains whatever is still buffered in the channel, in batch-sized chunks. Draining
        /// the whole channel as a single insert could mean one 10,000-row statement under a
        /// shutdown deadline, which would fail and cascade into a 10,000-row per-row fallback.
        /// </summary>
        private async Task DrainRemainingEventsAsync()
        {
            var chunk = new List<UserEvent>(capacity: BatchSize);
            var drained = 0;

            while (_queue.Reader.TryRead(out var evt))
            {
                chunk.Add(evt);

                if (chunk.Count >= BatchSize)
                {
                    await PersistOrDeadLetterOnShutdownAsync(chunk);
                    drained += chunk.Count;
                    chunk.Clear();
                }
            }

            if (chunk.Count > 0)
            {
                await PersistOrDeadLetterOnShutdownAsync(chunk);
                drained += chunk.Count;
            }

            if (drained > 0)
            {
                _logger.LogInformation("Drained {Count} leftover events on shutdown.", drained);
            }
        }

        /// <summary>
        /// True only for a unique/primary-key violation. SQL Server raises 2627 (constraint)
        /// and 2601 (unique index); SQLite raises 19 (SQLITE_CONSTRAINT) with extended codes
        /// 1555 and 2067 for the primary-key and unique cases.
        /// </summary>
        public static bool IsDuplicateKeyViolation(DbUpdateException ex)
        {
            for (Exception? inner = ex.InnerException; inner is not null; inner = inner.InnerException)
            {
                if (inner is not DbException dbException)
                {
                    continue;
                }

                if (dbException.SqlState is "23000" or "23505")
                {
                    return true;
                }

                var errorCode = TryGetIntProperty(dbException, "Number")
                                ?? TryGetIntProperty(dbException, "SqliteErrorCode");
                var extendedCode = TryGetIntProperty(dbException, "SqliteExtendedErrorCode");

                if (errorCode is 2601 or 2627 or 1555 or 2067 || extendedCode is 1555 or 2067)
                {
                    return true;
                }

                // SQLite reports the generic constraint code 19; the message distinguishes a
                // UNIQUE violation from NOT NULL or FOREIGN KEY, which are not duplicates.
                if (errorCode == 19 && dbException.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static int? TryGetIntProperty(Exception ex, string propertyName)
        {
            var property = ex.GetType().GetProperty(propertyName);
            var value = property?.GetValue(ex);
            return value is int intValue ? intValue : null;
        }

        /// <summary>
        /// Everything that is not a duplicate-key violation is retried. A genuinely permanent
        /// error exhausts the small retry budget and lands in the dead-letter store, which is
        /// the correct destination for it either way.
        /// </summary>
        private static bool IsRetryable(Exception ex) => ex is not OperationCanceledException;
    }
}
