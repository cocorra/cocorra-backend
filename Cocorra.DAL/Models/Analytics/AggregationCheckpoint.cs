using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Cocorra.DAL.Models.Analytics
{
    /// <summary>
    /// Tracks watermark progress of background analytics aggregation jobs.
    /// </summary>
    public class AggregationCheckpoint
    {
        public int Id { get; set; }

        [Required, MaxLength(64)]
        [Column(TypeName = "varchar(64)")]
        public string PipelineName { get; set; } = string.Empty;

        public long LastProcessedEventId { get; set; }

        [Column(TypeName = "datetime2")]
        public DateTime LastSuccessAtUtc { get; set; } = DateTime.UtcNow;

        public int ConsecutiveFailures { get; set; }
    }
}
