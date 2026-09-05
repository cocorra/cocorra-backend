namespace Cocorra.DAL.DTOS.AnalyticsDto
{
    public enum SignalDirection
    {
        Improving,
        Worsening,
        Stable,

        /// <summary>Not enough history to say. Distinct from Stable, which is a finding.</summary>
        Unknown
    }

    /// <summary>
    /// AN-039 / M-900. One watched signal in the Decision Center.
    ///
    /// Every field exists to stop the same failure: a dashboard that announces a change it
    /// cannot actually distinguish from ordinary week-to-week noise.
    /// </summary>
    public class DecisionSignalDto
    {
        public string SignalKey { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;

        /// <summary>The metric contract this signal reads, so its trust level is inspectable.</summary>
        public string MetricKey { get; set; } = string.Empty;

        public double? CurrentValue { get; set; }
        public double? PreviousValue { get; set; }
        public double? ChangePercent { get; set; }

        public SignalDirection Direction { get; set; } = SignalDirection.Unknown;

        /// <summary>
        /// True only when the change exceeds normal variation for this signal, measured as
        /// two standard deviations of its own weekly history. A fixed percentage threshold
        /// would fire constantly on a low-volume signal and never on a high-volume one.
        /// </summary>
        public bool IsSignificant { get; set; }

        /// <summary>Weeks of history behind the baseline. Below the gate, nothing is flagged.</summary>
        public int BaselineWeeks { get; set; }

        /// <summary>Plain statement of what the number is doing, or why it cannot be judged.</summary>
        public string Interpretation { get; set; } = string.Empty;

        /// <summary>
        /// The decision this signal informs. A signal nobody would act on does not belong on
        /// the page, whatever its statistical properties.
        /// </summary>
        public string DecisionSupported { get; set; } = string.Empty;
    }

    public class DecisionCenterDto
    {
        public DateTime GeneratedAtUtc { get; set; }

        public IEnumerable<DecisionSignalDto> Signals { get; set; } = [];

        /// <summary>Full weeks of read-model history available.</summary>
        public int WeeksOfHistory { get; set; }

        /// <summary>
        /// False until the baseline gate is met. While false, every signal reports Unknown and
        /// nothing is flagged as significant.
        /// </summary>
        public bool HasBaseline { get; set; }

        public int RequiredBaselineWeeks { get; set; }

        /// <summary>
        /// Why the page may be withholding judgement. Shipping change detection without a
        /// baseline produces alerts on ordinary variance, and a dashboard that cries wolf in
        /// its first month is ignored permanently — which is harder to undo than a late launch.
        /// </summary>
        public string? BaselineCaveat { get; set; }
    }
}
