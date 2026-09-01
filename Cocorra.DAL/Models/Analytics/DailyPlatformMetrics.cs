using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace Cocorra.DAL.Models.Analytics
{
    /// <summary>
    /// RM-1: Daily platform-level aggregations retained indefinitely.
    /// Grain: One row per Date.
    /// </summary>
    public class DailyPlatformMetrics
    {
        public long Id { get; set; }

        [Column(TypeName = "date")]
        public DateTime Date { get; set; }

        public int RoomsCreated { get; set; }
        public int RoomsGoneLive { get; set; }
        public int DistinctActiveHosts { get; set; }
        public int DistinctJoiningUsers { get; set; }
        public int DistinctSpeakingUsers { get; set; }
        public long TotalSpokenSeconds { get; set; }
        public int NewRegistrations { get; set; }
        public int VoiceVerificationsSubmitted { get; set; }
        public int VoiceVerificationsApproved { get; set; }

        [Column(TypeName = "datetime2")]
        public DateTime ComputedAtUtc { get; set; } = DateTime.UtcNow;
    }
}
