using System;
using System.Collections.Generic;

namespace Cocorra.BLL.Services.Analytics
{
    public enum MetricTrustLevel
    {
        Verified,
        ConditionallyReliable,
        Unreliable
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
