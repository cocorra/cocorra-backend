using System;

namespace Cocorra.DAL.Models
{
    /// <summary>
    /// Identity <see cref="Id"/> rather than a Guid: it is the chronological order within a room,
    /// which is what the ±N report context window is cut on.
    /// </summary>
    public class TextRoomMessage
    {
        public long Id { get; set; }

        public Guid TextRoomId { get; set; }
        public TextRoom? TextRoom { get; set; }

        public Guid SenderId { get; set; }
        public ApplicationUser? Sender { get; set; }

        /// <summary>As delivered: already masked when <see cref="IsMasked"/>.</summary>
        public string Content { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public bool IsMasked { get; set; }

        /// <summary>Exempts the message from the retention purge.</summary>
        public bool IsReported { get; set; }

        /// <summary>Soft delete: hidden from clients, content kept for report context.</summary>
        public bool IsDeleted { get; set; }

        public DateTime? DeletedAt { get; set; }

        public Guid? DeletedById { get; set; }
    }
}
