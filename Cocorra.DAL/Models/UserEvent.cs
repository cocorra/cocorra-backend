using System;
using System.ComponentModel.DataAnnotations;

namespace Cocorra.DAL.Models
{
    public class UserEvent
    {
        public long Id { get; set; } // bigint identity — high volume

        /// <summary>Nullable: some events (e.g. registration failure) have no user yet.</summary>
        public Guid? UserId { get; set; }
        public virtual ApplicationUser? User { get; set; }

        [Required, MaxLength(64)]
        public string EventType { get; set; } = string.Empty; // use EventTypes constants

        /// <summary>Free-form JSON for event-specific fields (roomId, category, source…).
        /// NEVER store message bodies, emails, or other PII here.</summary>
        public string? PropertiesJson { get; set; }

        /// <summary>Groups events into a single app session for funnel analysis.</summary>
        public Guid? SessionId { get; set; }

        /// <summary>
        /// Promoted from PropertiesJson for room-scoped events (room_created, room_joined, …)
        /// so analytics can filter/group by room with an indexed, SQL-translatable column
        /// instead of parsing JSON. Null for non-room events. Populated by EventTracker.
        /// </summary>
        public Guid? RoomId { get; set; }

        public DateTime OccurredAtUtc { get; set; } = DateTime.UtcNow;

        /// <summary>Store a HASH of the IP, never the raw IP (see Privacy §4).</summary>
        [MaxLength(64)]
        public string? IpHash { get; set; }

        [MaxLength(256)]
        public string? UserAgent { get; set; }
    }
}
