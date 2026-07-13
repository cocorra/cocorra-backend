namespace Cocorra.DAL.DTOS.AnalyticsDto
{
    /// <summary>
    /// A single time-bucket (day or month) for user growth tracking.
    /// </summary>
    public class UserGrowthDataPointDto
    {
        /// <summary>ISO-8601 period label, e.g. "2026-07-01" (daily) or "2026-07" (monthly).</summary>
        public string Period { get; set; } = string.Empty;
        public int NewUsers { get; set; }
        public int ActiveUsers { get; set; }
        public int PendingUsers { get; set; }
        public int BannedUsers { get; set; }
        public int RejectedUsers { get; set; }
        public int ReRecordUsers { get; set; }
    }

    public class UserGrowthDto
    {
        /// <summary>"daily" | "monthly"</summary>
        public string Granularity { get; set; } = string.Empty;
        public DateTime From { get; set; }
        public DateTime To { get; set; }

        public int TotalUsersInPeriod { get; set; }
        public IEnumerable<UserGrowthDataPointDto> DataPoints { get; set; } = [];

        // Aggregated totals across the whole period
        public Dictionary<string, int> StatusBreakdown { get; set; } = [];
        public Dictionary<string, int> MbtiDistribution { get; set; } = [];
        public double AverageAge { get; set; }
    }
}
