using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Cocorra.DAL.Models.Analytics
{
    /// <summary>
    /// Dead letter store for events that failed to persist after max retry attempts
    /// or due to permanent data errors. Ensures zero silent data loss.
    /// </summary>
    public class DeadLetterEvent
    {
        public long Id { get; set; }

        public Guid EventId { get; set; }

        [Required, MaxLength(64)]
        [Column(TypeName = "varchar(64)")]
        public string EventType { get; set; } = string.Empty;

        public Guid? UserId { get; set; }

        public Guid? RoomId { get; set; }

        public string? PropertiesJson { get; set; }

        public DateTime OccurredAtUtc { get; set; }

        [Required]
        public string FailureReason { get; set; } = string.Empty;

        [Column(TypeName = "datetime2")]
        public DateTime DeadLetteredAtUtc { get; set; } = DateTime.UtcNow;
    }
}
