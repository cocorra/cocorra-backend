using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Cocorra.DAL.Enums;

namespace Cocorra.DAL.Models
{
    public class Report : BaseEntity
    {
        public Guid ReporterId { get; set; }
        [ForeignKey(nameof(ReporterId))]
        public virtual ApplicationUser? Reporter { get; set; }

        public Guid? ReportedUserId { get; set; }
        [ForeignKey(nameof(ReportedUserId))]
        public virtual ApplicationUser? ReportedUser { get; set; }

        public Guid? ReportedRoomId { get; set; }
        [ForeignKey(nameof(ReportedRoomId))]
        public virtual Room? ReportedRoom { get; set; }

        public ReportCategory Category { get; set; }

        [Required]
        public string Description { get; set; } = string.Empty;

        public string? ScreenshotPath { get; set; }

        /// <summary>
        /// Free-form for backward compatibility. Prefer <see cref="StatusCode"/> for analytics:
        /// this column cannot be relied on to hold only known values.
        /// </summary>
        [Required]
        [MaxLength(50)]
        public string Status { get; set; } = "Open";

        /// <summary>
        /// AN-033: typed status, kept in step with <see cref="Status"/>. Additive so nothing
        /// that reads the string breaks, while analytics gets a value it can group on safely.
        /// </summary>
        public ReportStatus StatusCode { get; set; } = ReportStatus.Open;

        /// <summary>
        /// AN-033: when the item reached a terminal state. Resolution time was previously
        /// unmeasurable — only the current status existed, with no record of when it changed.
        /// </summary>
        public DateTime? ResolvedAt { get; set; }

        /// <summary>Null on reports filed before severity existed.</summary>
        public ModerationSeverity? Severity { get; set; }

        /// <summary>
        /// For <see cref="ReportSource.AutoModeration"/> the reporter is the message sender, because
        /// <see cref="ReporterId"/> is required and the previous release must still read these rows.
        /// </summary>
        public ReportSource Source { get; set; } = ReportSource.User;

        public Guid? ReportedTextRoomId { get; set; }
        [ForeignKey(nameof(ReportedTextRoomId))]
        public virtual TextRoom? ReportedTextRoom { get; set; }

        /// <summary>Deliberately not FK-linked: the evidence lives in <see cref="MessageContext"/>.</summary>
        public long? ReportedTextRoomMessageId { get; set; }

        public Guid? MatchedDictionaryEntryId { get; set; }

        [MaxLength(100)]
        public string? MatchedTerm { get; set; }

        public int? ContextBeforeRequested { get; set; }

        public int? ContextAfterRequested { get; set; }

        /// <summary>Null while messages after the reported one are still being collected.</summary>
        public DateTime? ContextCompletedAt { get; set; }

        public virtual ICollection<ReportMessageContextEntry> MessageContext { get; set; } = new List<ReportMessageContextEntry>();
    }
}
