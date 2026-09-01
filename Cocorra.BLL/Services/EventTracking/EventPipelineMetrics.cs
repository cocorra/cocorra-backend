using System.Threading;

namespace Cocorra.BLL.Services.EventTracking
{
    /// <summary>
    /// AN-003 step 7: in-process counters for the event pipeline.
    ///
    /// Both loss paths in this pipeline are silent by design — the channel drops on overflow
    /// and a failed flush discards — and there is no structured logging sink, no APM and no
    /// metrics export in this deployment. Without these counters the only way to observe
    /// either failure is to grep container logs that rotate at 10MB/3 files. AN-025 reads
    /// them to answer whether the numbers on the dashboard are being fed at all.
    ///
    /// Registered as a singleton; every member is safe to call from any thread.
    /// </summary>
    public class EventPipelineMetrics
    {
        private long _eventsDroppedOnEnqueue;
        private long _eventsEnqueued;
        private long _eventsPersisted;
        private long _flushBatchesFailed;
        private long _flushBatchesRetried;
        private long _eventsDeadLettered;
        private long _duplicateEventsDiscarded;

        /// <summary>Events lost because the bounded channel was full (DropWrite).</summary>
        public long EventsDroppedOnEnqueue => Interlocked.Read(ref _eventsDroppedOnEnqueue);

        public long EventsEnqueued => Interlocked.Read(ref _eventsEnqueued);

        public long EventsPersisted => Interlocked.Read(ref _eventsPersisted);

        /// <summary>Batches that exhausted retries or hit a permanent error.</summary>
        public long FlushBatchesFailed => Interlocked.Read(ref _flushBatchesFailed);

        public long FlushBatchesRetried => Interlocked.Read(ref _flushBatchesRetried);

        /// <summary>Events written to DeadLetterEvents rather than lost.</summary>
        public long EventsDeadLettered => Interlocked.Read(ref _eventsDeadLettered);

        /// <summary>
        /// Events rejected by UX_UserEvents_EventId. Expected and harmless — that is the
        /// idempotency guarantee working — but a sudden rise means something is replaying.
        /// </summary>
        public long DuplicateEventsDiscarded => Interlocked.Read(ref _duplicateEventsDiscarded);

        public void RecordDroppedOnEnqueue() => Interlocked.Increment(ref _eventsDroppedOnEnqueue);

        public void RecordEnqueued() => Interlocked.Increment(ref _eventsEnqueued);

        public void RecordPersisted(int count) => Interlocked.Add(ref _eventsPersisted, count);

        public void RecordBatchFailed() => Interlocked.Increment(ref _flushBatchesFailed);

        public void RecordBatchRetried() => Interlocked.Increment(ref _flushBatchesRetried);

        public void RecordDeadLettered(int count) => Interlocked.Add(ref _eventsDeadLettered, count);

        public void RecordDuplicateDiscarded() => Interlocked.Increment(ref _duplicateEventsDiscarded);
    }
}
