using System;

namespace Cocorra.DAL.Models
{
    /// <summary>
    /// An immutable copy of one message around a reported text-room message. Copied, not
    /// referenced, so the evidence survives history rotation, retention purge and account deletion.
    /// </summary>
    public class ReportMessageContextEntry
    {
        public long Id { get; set; }

        public Guid ReportId { get; set; }
        public Report? Report { get; set; }

        public long TextRoomMessageId { get; set; }

        /// <summary>Negative before the reported message, 0 for it, positive after.</summary>
        public int Offset { get; set; }

        public Guid SenderId { get; set; }

        /// <summary>First and last name when captured. Never an email.</summary>
        public string SenderDisplayName { get; set; } = string.Empty;

        public string Content { get; set; } = string.Empty;

        public DateTime SentAt { get; set; }

        /// <summary>Deleted when captured. A staff deletion after capture does not change the snapshot.</summary>
        public bool WasDeleted { get; set; }
    }
}
