using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Cocorra.DAL.Models.Analytics
{
    /// <summary>
    /// RM-5: Captures point-in-time state counts (queues, active totals, report backlog)
    /// that cannot be derived or backfilled from events after the fact.
    /// Grain: One row per (Date, MetricKey).
    /// </summary>
    public class DailyStateSnapshot
    {
        public long Id { get; set; }

        /// <summary>The date this snapshot represents (stored as date).</summary>
        [Column(TypeName = "date")]
        public DateTime Date { get; set; }

        /// <summary>
        /// Key identifying the metric: 'pending_verification_queue', 'rerecord_queue',
        /// 'active_users_total', 'fcm_token_coverage', 'open_reports', etc.
        /// </summary>
        [Required]
        [MaxLength(64)]
        [Column(TypeName = "varchar(64)")]
        public string MetricKey { get; set; } = string.Empty;

        /// <summary>The calculated scalar value or ratio.</summary>
        public double Value { get; set; }

        /// <summary>UTC timestamp when this snapshot was computed.</summary>
        [Column(TypeName = "datetime2")]
        public DateTime ComputedAtUtc { get; set; } = DateTime.UtcNow;
    }
}
