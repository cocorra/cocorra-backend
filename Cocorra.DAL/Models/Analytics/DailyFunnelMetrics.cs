using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Cocorra.DAL.Models.Analytics
{
    /// <summary>
    /// RM-4: Daily funnel conversion steps retained indefinitely.
    /// Grain: One row per (CohortDate, FunnelName, StepIndex).
    /// </summary>
    public class DailyFunnelMetrics
    {
        public long Id { get; set; }

        [Column(TypeName = "date")]
        public DateTime CohortDate { get; set; }

        [Required, MaxLength(64)]
        [Column(TypeName = "varchar(64)")]
        public string FunnelName { get; set; } = string.Empty;

        public byte StepIndex { get; set; }

        [Required, MaxLength(64)]
        [Column(TypeName = "varchar(64)")]
        public string StepName { get; set; } = string.Empty;

        public int UsersReached { get; set; }

        public int MedianSecondsFromPrevious { get; set; }

        [Column(TypeName = "datetime2")]
        public DateTime ComputedAtUtc { get; set; } = DateTime.UtcNow;
    }
}
