using System;

namespace Cocorra.DAL.Models
{
    /// <summary>
    /// One row per user who ever joined a room. Online presence is in memory
    /// (<c>ITextRoomPresenceTracker</c>); this row carries the totals and the sanctions.
    /// </summary>
    public class TextRoomParticipant
    {
        public Guid TextRoomId { get; set; }
        public TextRoom? TextRoom { get; set; }

        public Guid UserId { get; set; }
        public ApplicationUser? User { get; set; }

        public DateTime FirstJoinedAt { get; set; } = DateTime.UtcNow;

        public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;

        public DateTime? MutedUntil { get; set; }

        public Guid? MutedById { get; set; }

        /// <summary>A kicked user cannot rejoin this room instance.</summary>
        public bool IsKicked { get; set; }

        public DateTime? KickedAt { get; set; }

        public Guid? KickedById { get; set; }
    }
}
