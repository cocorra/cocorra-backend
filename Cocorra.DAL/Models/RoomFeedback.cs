using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Cocorra.DAL.Models
{
    /// <summary>
    /// A participant's rating of a room session. One row per (RoomId, UserId): submitting
    /// again updates the existing row rather than adding a second one, enforced by a unique
    /// index configured in AppDbContext.
    /// </summary>
    public class RoomFeedback : BaseEntity
    {
        public Guid RoomId { get; set; }
        [ForeignKey(nameof(RoomId))]
        public virtual Room? Room { get; set; }

        public Guid UserId { get; set; }
        [ForeignKey(nameof(UserId))]
        public virtual ApplicationUser? User { get; set; }

        /// <summary>1..5 stars. Also enforced by a database check constraint.</summary>
        public int Rating { get; set; }

        [MaxLength(1000)]
        public string? Comment { get; set; }
    }
}
