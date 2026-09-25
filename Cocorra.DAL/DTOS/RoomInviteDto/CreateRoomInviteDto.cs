using System.ComponentModel.DataAnnotations;

namespace Cocorra.DAL.DTOS.RoomInviteDto
{
    /// <summary>The whole body is optional; omitting it uses the configured default lifetime.</summary>
    public class CreateRoomInviteDto
    {
        [Range(1, 168, ErrorMessage = "ExpiresInHours must be between 1 and 168.")]
        public int? ExpiresInHours { get; set; }
    }
}
