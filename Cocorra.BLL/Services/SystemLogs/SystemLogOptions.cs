namespace Cocorra.BLL.Services.SystemLogs
{
    /// <summary>
    /// Configuration for the persisted system log sink and its retention.
    /// Bound to Configuration section "SystemLogs".
    /// </summary>
    public class SystemLogOptions
    {
        public const string SectionName = "SystemLogs";

        public bool Enabled { get; set; } = true;

        /// <summary>Lowest level persisted. Anything below Warning would flood the table.</summary>
        public string MinimumLevel { get; set; } = "Warning";

        public int RetentionDays { get; set; } = 30;

        /// <summary>Bound on entries waiting to be flushed; the oldest are dropped when full.</summary>
        public int QueueCapacity { get; set; } = 5_000;

        public int BatchSize { get; set; } = 100;
    }
}
