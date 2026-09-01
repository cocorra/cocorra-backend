namespace Cocorra.DAL.DTOS.AnalyticsDto
{
    /// <summary>
    /// One step of the sequential activation funnel (AN-007 / M-507).
    /// </summary>
    public class FunnelStepDto
    {
        public string Step { get; set; } = string.Empty;

        /// <summary>Users who reached this step with every earlier step already completed.</summary>
        public int Count { get; set; }

        /// <summary>Share of the first step's population that reached this step.</summary>
        public double ConversionFromFirstStepPercent { get; set; }

        /// <summary>Share of the immediately preceding step that reached this step.</summary>
        public double ConversionFromPreviousStepPercent { get; set; }

        /// <summary>
        /// Median time from the previous step to this one. Null on the first step, and null
        /// when no user has both timestamps.
        ///
        /// A median rather than a mean, deliberately: one of Cocorra's steps is a human review
        /// queue, and if most reviews take 20 minutes while 15% take three days, the mean
        /// describes nobody and hides the users being harmed.
        /// </summary>
        public double? MedianSecondsFromPreviousStep { get; set; }

        /// <summary>90th percentile of the same gap — where the tail actually sits.</summary>
        public double? P90SecondsFromPreviousStep { get; set; }
    }

    public class ActivationFunnelDto
    {
        public DateTime From { get; set; }
        public DateTime To { get; set; }

        public IEnumerable<FunnelStepDto> Steps { get; set; } = [];

        /// <summary>
        /// Earliest event timestamp backing this funnel. Bounded by the 180-day raw retention
        /// window, so a caller can render a visible start-of-data boundary rather than
        /// implying the series begins at zero.
        /// </summary>
        public DateTime? DataAvailableFromUtc { get; set; }
    }
}
