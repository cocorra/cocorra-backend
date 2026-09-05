using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Cocorra.BLL.Services.EventTracking;
using Cocorra.DAL.Data;
using Cocorra.DAL.Models;
using Cocorra.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Cocorra.Tests
{
    /// <summary>
    /// AN-003 failure classification.
    ///
    /// EF Core wraps nearly every provider failure during SaveChangesAsync in
    /// DbUpdateException, so a flush service that treats DbUpdateException as "duplicate key"
    /// routes deadlocks and timeouts into the per-row fallback and then discards them as
    /// expected duplicates — a silent loss path quieter than the one it replaced. These tests
    /// pin the distinction.
    /// </summary>
    public class PipelineClassificationTests : IDisposable
    {
        private readonly SqliteTestHost _host = new();

        public void Dispose() => _host.Dispose();

        private static UserEvent NewEvent(Guid? eventId = null) => new()
        {
            EventId = eventId ?? Guid.NewGuid(),
            EventType = EventTypes.RoomJoined,
            OccurredAtUtc = DateTime.UtcNow
        };

        [Fact]
        public async Task Classifier_RecognisesRealUniqueViolation()
        {
            var duplicate = Guid.NewGuid();

            using var scope = _host.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            db.UserEvents.Add(NewEvent(duplicate));
            await db.SaveChangesAsync();

            db.UserEvents.Add(NewEvent(duplicate));
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());

            Assert.True(EventFlushService.IsDuplicateKeyViolation(ex));
        }

        [Fact]
        public async Task Classifier_DoesNotMistakeAMissingTableForADuplicate()
        {
            // Stands in for the class of failure that matters: a real database fault that is
            // NOT a duplicate. Misclassifying it is what silently discards a batch.
            using var scope = _host.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.ExecuteSqlRawAsync("DROP TABLE UserEvents;");

            db.UserEvents.Add(NewEvent());
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());

            Assert.False(EventFlushService.IsDuplicateKeyViolation(ex));
        }

        [Fact]
        public void Classifier_DoesNotMistakeANonDbInnerExceptionForADuplicate()
        {
            var ex = new DbUpdateException("wrapped", new TimeoutException("command timeout"));
            Assert.False(EventFlushService.IsDuplicateKeyViolation(ex));
        }

        [Fact]
        public async Task PermanentFailure_IsDeadLettered_NotSilentlyDiscarded()
        {
            // Drop UserEvents so every insert fails permanently. The events must end up in the
            // dead-letter store: "no silent loss" is the whole point of AN-003.
            using (var scope = _host.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await db.Database.ExecuteSqlRawAsync("DROP TABLE UserEvents;");
            }

            var queue = Channel.CreateUnbounded<UserEvent>();
            var events = Enumerable.Range(0, 5).Select(_ => NewEvent()).ToList();
            foreach (var evt in events)
            {
                queue.Writer.TryWrite(evt);
            }
            queue.Writer.Complete();

            var metrics = new EventPipelineMetrics();
            var options = Options.Create(new EventTrackingOptions
            {
                EventFlushBatchSize = 100,
                EventFlushMaxRetries = 1,
                EventFlushInitialBackoffMs = 1
            });

            var service = new EventFlushService(
                queue,
                _host.Services.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<EventFlushService>.Instance,
                options,
                metrics);

            await service.StartAsync(CancellationToken.None);
            await Task.Delay(600);
            await service.StopAsync(CancellationToken.None);

            using (var scope = _host.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var deadLettered = await db.DeadLetterEvents.CountAsync();

                Assert.Equal(events.Count, deadLettered);
                Assert.Equal(events.Count, metrics.EventsDeadLettered);
                Assert.True(metrics.FlushBatchesFailed >= 1);
                Assert.True(metrics.FlushBatchesRetried >= 1, "a non-duplicate failure must be retried, not routed to the duplicate fallback");
            }
        }

        [Fact]
        public void DroppedEventsAreCounted_NotOnlyLogged()
        {
            // R-1. Two things are asserted here. First, that drops are counted rather than
            // only logged. Second, and more importantly, that the channel uses FullMode.Wait:
            // under DropWrite the channel discards the item but TryWrite still returns TRUE, so
            // the drop cannot be detected at all. This test fails against a DropWrite channel,
            // which is how that bug was found.
            var queue = Channel.CreateBounded<UserEvent>(
                new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.Wait });

            var metrics = new EventPipelineMetrics();
            var tracker = new EventTracker(
                queue,
                NullLogger<EventTracker>.Instance,
                new Microsoft.AspNetCore.Http.HttpContextAccessor(),
                new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
                metrics);

            for (var i = 0; i < 5; i++)
            {
                tracker.Track(EventTypes.RoomJoined, Guid.NewGuid());
            }

            Assert.Equal(1, metrics.EventsEnqueued);
            Assert.Equal(4, metrics.EventsDroppedOnEnqueue);
        }

        [Fact]
        public void DeterministicEventId_IsStableAndWellFormed()
        {
            var queue = Channel.CreateUnbounded<UserEvent>();
            var tracker = new EventTracker(
                queue,
                NullLogger<EventTracker>.Instance,
                new Microsoft.AspNetCore.Http.HttpContextAccessor(),
                new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

            var userId = Guid.NewGuid();
            var key = $"{EventTypes.ActivationCompleted}:{userId}";

            tracker.Track(EventTypes.ActivationCompleted, userId, null, eventKey: key);
            tracker.Track(EventTypes.ActivationCompleted, userId, null, eventKey: key);

            Assert.True(queue.Reader.TryRead(out var first));
            Assert.True(queue.Reader.TryRead(out var second));
            Assert.Equal(first!.EventId, second!.EventId);

            // RFC-4122: version nibble 5, variant bits 10xx. Not cosmetic — it keeps the value
            // a well-formed UUID for any consumer that inspects it.
            var bytes = first.EventId.ToByteArray();
            Assert.Equal(0x50, bytes[7] & 0xF0);
            Assert.Equal(0x80, bytes[8] & 0xC0);
        }
    }
}
