namespace Cocorra.DAL.DTOS.AnalyticsDto
{
    /// <summary>
    /// A single time-bucket (day or month) for user growth tracking.
    ///
    /// AN-008: this carries registrations only. The old shape also carried per-bucket
    /// ActiveUsers/PendingUsers/BannedUsers counted from each user's CURRENT status, which
    /// rewrote history: a user who registered in month 1 and was banned in month 3 appeared
    /// as banned in month 1, and the distortion grew with bucket age. Status-at-time now
    /// lives in <see cref="UserGrowthDto.StatusAtTime"/>, reconstructed from events.
    /// </summary>
    public class UserGrowthDataPointDto
    {
        /// <summary>ISO-8601 period label, e.g. "2026-07-01" (daily) or "2026-07" (monthly).</summary>
        public string Period { get; set; } = string.Empty;

        /// <summary>Users whose CreatedAt falls in this bucket. VERIFIED (M-501).</summary>
        public int NewUsers { get; set; }
    }

    /// <summary>
    /// Verification status as it stood at the END of a bucket, reconstructed from
    /// voice_verification_result events (M-502). Absent buckets are periods with no event
    /// history, not periods where every user was Pending.
    /// </summary>
    public class StatusAtTimeDataPointDto
    {
        public string Period { get; set; } = string.Empty;
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

        /// <summary>Registrations per bucket (M-501, VERIFIED).</summary>
        public IEnumerable<UserGrowthDataPointDto> DataPoints { get; set; } = [];

        /// <summary>
        /// Reconstructed status-at-time per bucket (M-502, CONDITIONALLY RELIABLE). Empty when
        /// the requested window predates the available event history.
        /// </summary>
        public IEnumerable<StatusAtTimeDataPointDto> StatusAtTime { get; set; } = [];

        /// <summary>
        /// Earliest date the status reconstruction can speak to, bounded by the 180-day raw
        /// event retention window. Null when no status events exist at all. A caller must
        /// render everything before this as a gap, never as zero.
        /// </summary>
        public DateTime? StatusHistoryAvailableFromUtc { get; set; }

        /// <summary>
        /// MBTI distribution among users REGISTERED IN THIS WINDOW — not the platform-wide
        /// distribution. Relabelled per AN-008 step 5 because the previous name invited the
        /// wrong reading.
        /// </summary>
        public Dictionary<string, int> MbtiDistributionOfUsersRegisteredInWindow { get; set; } = [];

        /// <summary>Average age of users REGISTERED IN THIS WINDOW.</summary>
        public double AverageAgeOfUsersRegisteredInWindow { get; set; }
    }
}
