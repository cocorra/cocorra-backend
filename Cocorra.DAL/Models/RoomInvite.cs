using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Cocorra.DAL.Enums;

namespace Cocorra.DAL.Models
{
    /// <summary>
    /// A single-use link that lets someone join a room. The code is a bearer secret: whoever
    /// holds it can join, so it is never logged or put into analytics — events carry the row
    /// Id instead. The public URL is built from configuration on the way out, not stored.
    /// </summary>
    public class RoomInvite : BaseEntity
    {
        /// <summary>22-char Base64Url (128 random bits). Unique index in AppDbContext.</summary>
        [Required]
        [MaxLength(64)]
        public string InviteCode { get; set; } = string.Empty;

        public Guid RoomId { get; set; }
        [ForeignKey(nameof(RoomId))]
        public virtual Room? Room { get; set; }

        public Guid InviterUserId { get; set; }
        [ForeignKey(nameof(InviterUserId))]
        public virtual ApplicationUser? InviterUser { get; set; }

        public DateTime ExpiresAt { get; set; }

        /// <summary>
        /// Stored transitions are Active→Used and Active→Revoked only. See
        /// <see cref="RoomInviteStatus"/> for how Expired is derived.
        /// </summary>
        public RoomInviteStatus Status { get; set; } = RoomInviteStatus.Active;

        public DateTime? UsedAt { get; set; }
        public Guid? UsedByUserId { get; set; }
        [ForeignKey(nameof(UsedByUserId))]
        public virtual ApplicationUser? UsedByUser { get; set; }

        public DateTime? RevokedAt { get; set; }
        public Guid? RevokedByUserId { get; set; }
        [ForeignKey(nameof(RevokedByUserId))]
        public virtual ApplicationUser? RevokedByUser { get; set; }
    }
}
