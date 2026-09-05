namespace Cocorra.DAL.DTOS.AnalyticsDto
{
    /// <summary>
    /// AN-025 — pipeline health.
    ///
    /// This matters more here than it would elsewhere: Cocorra has no structured logging sink,
    /// no APM and no metrics export, so errors reach Docker stdout with 10MB/3-file rotation.
    /// A failing invariant written only to container logs is one nobody sees. Routing it
    /// through the analytics API uses the one observability surface that already exists and
    /// that people already look at.
    ///
    /// It also closes the loop the trust framework opens: a metric's trust level is meaningless
    /// if the pipeline feeding it stopped three days ago.
    /// </summary>
    public class PipelineHealthDto
    {
        public bool PipelineHealthy { get; set; }
        public IEnumerable<string> Warnings { get; set; } = [];

        // ── Ingestion counters (process-local, reset on restart) ────────────
        public long EventsEnqueued { get; set; }
        public long EventsPersisted { get; set; }

        /// <summary>Events lost to a full channel. Any sustained non-zero value is a problem.</summary>
        public long EventsDroppedOnEnqueue { get; set; }

        public long FlushBatchesRetried { get; set; }
        public long FlushBatchesFailed { get; set; }
        public long EventsDeadLettered { get; set; }

        /// <summary>Expected and harmless — the idempotency guarantee working.</summary>
        public long DuplicateEventsDiscarded { get; set; }

        /// <summary>
        /// Counters live in process memory and reset when the container restarts, so they
        /// describe this instance since it started, not all time.
        /// </summary>
        public DateTime CountersSinceUtc { get; set; }

        // ── Durable state ───────────────────────────────────────────────────
        /// <summary>Rows sitting in the dead-letter store. Should be zero.</summary>
        public int DeadLetterBacklog { get; set; }

        public DateTime? LastAggregationSuccessUtc { get; set; }
        public double? AggregationLagHours { get; set; }
        public int AggregationConsecutiveFailures { get; set; }
        public long AggregationWatermarkEventId { get; set; }

        /// <summary>Raw events past the watermark and still waiting to be rolled up.</summary>
        public long UnaggregatedEventCount { get; set; }

        // ── Snapshot coverage ───────────────────────────────────────────────
        public DateTime? LastSnapshotDate { get; set; }

        /// <summary>
        /// Dates in the last 30 days with no state snapshot. These are permanent holes: state
        /// counts cannot be reconstructed after the fact.
        /// </summary>
        public IEnumerable<string> SnapshotGapDates { get; set; } = [];
    }
}
