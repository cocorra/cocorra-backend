using System;
using System.ComponentModel.DataAnnotations;

namespace Cocorra.DAL.Models
{
    /// <summary>
    /// Who did what to whom, for privileged user and role actions. Kept indefinitely and
    /// deliberately not FK-linked to users, so the trail survives account deletion; the
    /// actor email and target display are snapshots taken when the action happened.
    /// </summary>
    public class AuditLog
    {
        public long Id { get; set; }

        public DateTime OccurredAtUtc { get; set; } = DateTime.UtcNow;

        [Required, MaxLength(64)]
        public string Action { get; set; } = string.Empty; // use AuditActions constants

        public Guid? ActorUserId { get; set; }

        [MaxLength(256)]
        public string? ActorEmail { get; set; }

        [MaxLength(64)]
        public string? ActorRole { get; set; }

        public Guid? TargetUserId { get; set; }

        [MaxLength(256)]
        public string? TargetDisplay { get; set; }

        public string? DetailsJson { get; set; }

        // Staff activity timeline fields. All nullable: rows written before they existed, and
        // actions that have no entity or state change, leave them empty.
        [MaxLength(32)]
        public string? Category { get; set; } // use AuditCategories constants

        [MaxLength(32)]
        public string? EntityType { get; set; } // use AuditEntityTypes constants

        [MaxLength(64)]
        public string? EntityId { get; set; }

        [MaxLength(64)]
        public string? FromState { get; set; }

        [MaxLength(64)]
        public string? ToState { get; set; }

        [MaxLength(1000)]
        public string? Reason { get; set; }

        [MaxLength(64)]
        public string? IpAddress { get; set; }

        [MaxLength(512)]
        public string? UserAgent { get; set; }
    }
}
