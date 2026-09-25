using System;
using Cocorra.DAL.Enums;

namespace Cocorra.DAL.DTOS.RoomInviteDto
{
    /// <summary>
    /// What an unauthenticated holder of the link may learn. Deliberately carries no inviter,
    /// room title or participants, and RoomId only while the invite is still usable.
    /// </summary>
    public class ResolveRoomInviteDto
    {
        public bool Valid { get; set; }
        public string InviteCode { get; set; } = string.Empty;

        /// <summary>Null unless <see cref="Valid"/>.</summary>
        public Guid? RoomId { get; set; }

        public DateTime ExpiresAt { get; set; }
        public RoomInviteStatus Status { get; set; }
    }
}
