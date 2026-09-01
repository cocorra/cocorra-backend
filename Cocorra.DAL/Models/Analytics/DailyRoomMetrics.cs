using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Cocorra.DAL.Models.Analytics
{
    /// <summary>
    /// RM-2: Daily room-level aggregations retained indefinitely.
    /// Grain: One row per (Date, RoomId).
    /// </summary>
    public class DailyRoomMetrics
    {
        public long Id { get; set; }

        [Column(TypeName = "date")]
        public DateTime Date { get; set; }

        public Guid RoomId { get; set; }

        public Guid HostId { get; set; }

        [MaxLength(64)]
        [Column(TypeName = "varchar(64)")]
        public string Category { get; set; } = string.Empty;

        [MaxLength(64)]
        [Column(TypeName = "varchar(64)")]
        public string SelectionMode { get; set; } = string.Empty;

        public int StageCapacity { get; set; }

        public int DistinctJoiners { get; set; }
        public int DistinctSpeakers { get; set; }
        public int HandRaises { get; set; }
        public int StagePromotions { get; set; }
        public int TotalSpokenSeconds { get; set; }
        public int ReportsCount { get; set; }

        [Column(TypeName = "datetime2")]
        public DateTime ComputedAtUtc { get; set; } = DateTime.UtcNow;
    }
}
