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
    }
}
