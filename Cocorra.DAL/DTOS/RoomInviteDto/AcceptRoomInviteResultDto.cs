using System;
using Cocorra.DAL.DTOS.RoomDto;

namespace Cocorra.DAL.DTOS.RoomInviteDto
{
    public class AcceptRoomInviteResultDto
    {
        public Guid RoomId { get; set; }

        /// <summary>False when the caller was already in the room, so the invite stays usable.</summary>
        public bool InviteConsumed { get; set; }

        public bool AlreadyParticipant { get; set; }

        /// <summary>The same payload POST /Room/{id}/Join returns.</summary>
        public JoinRoomResultDto Join { get; set; } = new();
    }
}
