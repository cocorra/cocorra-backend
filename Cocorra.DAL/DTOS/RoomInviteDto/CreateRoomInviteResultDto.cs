using System;

namespace Cocorra.DAL.DTOS.RoomInviteDto
{
    public class CreateRoomInviteResultDto
    {
        public string InviteCode { get; set; } = string.Empty;
        public string InviteUrl { get; set; } = string.Empty;
        public DateTime ExpiresAt { get; set; }
    }
}
