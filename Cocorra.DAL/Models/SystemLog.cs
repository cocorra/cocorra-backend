using System;
using System.ComponentModel.DataAnnotations;

namespace Cocorra.DAL.Models
{
    /// <summary>
    /// A persisted Warning-or-higher application log entry, written by the database logger
    /// sink and purged after SystemLogs:RetentionDays. No FK to users: a log line must outlive
    /// whatever it describes.
    /// </summary>
    public class SystemLog
    {
        public long Id { get; set; } // bigint identity — high volume

        public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;

        [Required, MaxLength(16)]
        public string Level { get; set; } = string.Empty;

        [Required, MaxLength(256)]
        public string Category { get; set; } = string.Empty;

        public int? EventId { get; set; }

        [Required, MaxLength(4000)]
        public string Message { get; set; } = string.Empty;

        [MaxLength(8000)]
        public string? Exception { get; set; }

        [MaxLength(64)]
        public string? TraceId { get; set; }

        [MaxLength(16)]
        public string? RequestMethod { get; set; }

        [MaxLength(512)]
        public string? RequestPath { get; set; }

        public Guid? UserId { get; set; }
    }
}
