using System;
using System.Collections.Generic;

namespace Cocorra.BLL.Services.Analytics
{
    /// <summary>
    /// Ordered weakest-last, deliberately. <c>AnalyticsService.BuildMeta</c> takes
    /// <c>Max()</c> across a composite's components so the response inherits its weakest
    /// input, and that only works while the ordinal order tracks descending trust.
    /// Inserting a value in the wrong position would silently round a composite's trust up.
    /// </summary>
    public enum MetricTrustLevel
    {
        /// <summary>Formula, population and exclusions are proven; no known caveat.</summary>
        Verified = 0,

        /// <summary>Usable, but only alongside a stated condition that must be rendered inline.</summary>
        ConditionallyReliable = 1,

        /// <summary>
        /// Computed correctly but not yet validated against enough real emission to be relied
        /// on. Applies to metrics whose backing events shipped recently or are still flagged
        /// off. Promotes to VERIFIED after a stated period of stable emission, not by default.
        /// </summary>
        Experimental = 2,

        /// <summary>Known to mislead. Must not be displayed; preferably not served at all.</summary>
        Unreliable = 3
    }

    public class MetricContract
    {
        public string MetricKey { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string BusinessPurpose { get; set; } = string.Empty;
        public string TechnicalDefinition { get; set; } = string.Empty;
        public string Formula { get; set; } = string.Empty;
        public MetricTrustLevel TrustLevel { get; set; } = MetricTrustLevel.Verified;
        public List<string> Exclusions { get; set; } = new();
        public List<string> Limitations { get; set; } = new();
        public DateTime? DataAvailableFromUtc { get; set; }
        public string ValidationMethod { get; set; } = string.Empty;
    }

    public interface IMetricRegistry
    {
        MetricContract? GetContract(string metricKey);
        IReadOnlyList<MetricContract> GetAllContracts();
    }
}
