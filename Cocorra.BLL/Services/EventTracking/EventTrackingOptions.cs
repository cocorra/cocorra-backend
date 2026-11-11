namespace Cocorra.BLL.Services.EventTracking
{
    /// <summary>
    /// Configuration options for the analytics pipeline, event flushing, cleanup, and aggregation.
    /// Bound to Configuration section "Analytics".
    /// </summary>
    public class EventTrackingOptions
    {
        public const string SectionName = "Analytics";

        public string? IpHashSalt { get; set; }

        public int EventChannelCapacity { get; set; } = 10_000;

        public int EventFlushBatchSize { get; set; } = 100;

        public int EventFlushMaxRetries { get; set; } = 3;

        public int EventFlushInitialBackoffMs { get; set; } = 200;

        public int RawEventRetentionDays { get; set; } = 180;

        public int CleanupBatchSize { get; set; } = 5_000;

        public int AggregationIntervalMinutes { get; set; } = 60;

        public int AggregationBatchSize { get; set; } = 50_000;

        public int AggregationTrailingDays { get; set; } = 45;

        public int SnapshotHourUtc { get; set; } = 0;

        /// <summary>
        /// AN-017/AN-018 gate. These are the only changes that add load to a shared bounded
        /// resource (the event channel), which is why they are the one thing worth flagging —
        /// most of this programme is additive and needs no flag at all.
        /// Off by default: turn it on only after AN-001 has established the current drop rate.
        /// </summary>
        public bool EnableNewEventEmission { get; set; } = false;

        /// <summary>
        /// AN-018 gate, separate from the above. High-frequency events scale with engagement
        /// and land hardest on the busiest rooms, so they must be revertible without touching
        /// the low-frequency increment.
        /// </summary>
        public bool EnableHighFrequencyEvents { get; set; } = false;

        /// <summary>
        /// AN-042. When set, structured logs are also written as newline-delimited JSON to this
        /// path so they survive a container restart and can be grepped or shipped.
        ///
        /// Today errors reach Docker stdout with 10MB/3-file rotation and nothing else — no
        /// sink, no APM, no metrics export. A failing invariant written only there is one
        /// nobody sees. Leave unset to keep the current behaviour exactly as it is.
        /// </summary>
        public string? StructuredLogPath { get; set; }

        /// <summary>Minimum level written to the structured sink.</summary>
        public string StructuredLogMinimumLevel { get; set; } = "Warning";
    }
}
