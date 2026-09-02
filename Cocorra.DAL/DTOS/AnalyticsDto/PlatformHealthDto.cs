namespace Cocorra.DAL.DTOS.AnalyticsDto
{
    /// <summary>
    /// One headline figure on the Platform Health landing view (A-1).
    ///
    /// Every field that could be absent is nullable rather than defaulted. A KPI card that
    /// renders <c>0</c> for a period with no read-model coverage states, confidently, that
    /// nothing happened — which is the single failure mode this whole programme exists to
    /// remove. <see cref="IsMeasured"/> is what the client branches on.
    /// </summary>
    public class PlatformHealthMetricDto
    {
        /// <summary>Registry key, e.g. "M-100". Resolve its contract in <c>Meta.metrics</c>.</summary>
        public string MetricKey { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        /// <summary>Unit hint for the client: "count", "percent", or "seconds".</summary>
        public string Unit { get; set; } = "count";

        /// <summary>Null when unmeasured for this window. Never coerce to 0.</summary>
        public double? Value { get; set; }

        /// <summary>The same figure over the immediately preceding window of equal length.</summary>
        public double? PreviousValue { get; set; }

        public double? DeltaAbsolute { get; set; }

        /// <summary>
        /// Null when the previous value is 0 or absent. A change from 0 is not "+100%" and not
        /// "+∞"; it is a change from nothing, and only the absolute delta describes it.
        /// </summary>
        public double? DeltaPercent { get; set; }

        /// <summary>
        /// False when no read-model row covers the requested window. Renders as a labelled gap.
        /// </summary>
        public bool IsMeasured { get; set; }

        public string? NotMeasuredReason { get; set; }

        /// <summary>Earliest date with read-model coverage for this metric.</summary>
        public DateTime? DataAvailableFromUtc { get; set; }

        /// <summary>
        /// Route that diagnoses this figure. Returned by the server rather than hardcoded in the
        /// client so a drill-down cannot point at a route that no longer exists.
        /// </summary>
        public string DrillDownEndpoint { get; set; } = string.Empty;
    }

    /// <summary>
    /// A-1 — <c>GET /Analytics/Platform/Health</c>. The north star and its supporting inputs in
    /// one call: the dashboard's landing view.
    ///
    /// <para>
    /// Replaces <c>/Analytics/Summary</c>, which bundles four sub-metrics of differing trust
    /// into one response and reports a single aggregate verdict over them. A composite is only
    /// as trustworthy as its weakest component, so bundling obscures which parts to trust. This
    /// endpoint returns per-metric trust instead, and the legacy route stays live until cutover.
    /// </para>
    ///
    /// <para>
    /// Sourced from read models (RM-1, RM-3), not from raw events. Until
    /// <c>AnalyticsAggregationService</c> has run in production the read models are empty and
    /// every metric here returns <c>isMeasured: false</c> — correctly, because the figure is
    /// unknown rather than zero.
    /// </para>
    /// </summary>
    public class PlatformHealthDto
    {
        public DateTime From { get; set; }
        public DateTime To { get; set; }

        /// <summary>Start of the comparison window. Null when no comparison was requested.</summary>
        public DateTime? PreviousFrom { get; set; }
        public DateTime? PreviousTo { get; set; }

        /// <summary>
        /// Whether a comparison window was resolved. The headline figure is close to meaningless
        /// without a prior period — "1,247 participating users" answers nothing on its own — so a
        /// client should surface the absence rather than quietly showing a bare number.
        /// </summary>
        public bool HasComparison { get; set; }

        /// <summary>
        /// True when <c>To</c> falls inside the current UTC day, so the window is still filling.
        /// Must be labelled ("week to date") and must never be plotted as a completed period:
        /// without this the current period always looks like a decline, and a dashboard that
        /// shows a drop every Monday trains its readers to ignore drops.
        /// </summary>
        public bool IsPartialPeriod { get; set; }

        public IEnumerable<PlatformHealthMetricDto> Metrics { get; set; } = [];

        /// <summary>
        /// False when no read-model rows exist at all — aggregation has not yet run. Distinct
        /// from "this window has no data": one is a pipeline state, the other is a finding.
        /// </summary>
        public bool ReadModelsPopulated { get; set; }

        /// <summary>Earliest date present in the platform read model.</summary>
        public DateTime? DataAvailableFromUtc { get; set; }
    }
}
