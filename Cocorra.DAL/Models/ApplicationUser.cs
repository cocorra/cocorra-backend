using Microsoft.AspNetCore.Identity;
using System.ComponentModel.DataAnnotations;

namespace Cocorra.DAL.Models
{
    public class ApplicationUser : IdentityUser<Guid>
    {
        [Required]
        [MaxLength(50)]
        public string FirstName { get; set; } = string.Empty;
        [Required]
        [MaxLength(50)]
        public string LastName { get; set; } = string.Empty;
        public int Age { get; set; }
        public string? VoiceVerificationPath { get; set; }
        public string? MBTI { get; set; }
        public UserStatus Status { get; set; } = UserStatus.Pending;
        public virtual ICollection<RoomParticipant> RoomParticipations { get; set; } = new List<RoomParticipant>();
        public string? FcmToken { get; set; }
        public virtual ICollection<Room> OwnedRooms { get; set; }= new List<Room>();
        public virtual ICollection<BlockedDevices> BlockedDevices { get; set; } = new List<BlockedDevices>();
        [System.ComponentModel.DataAnnotations.Schema.Column("CreateAt")]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public string? ProfilePicturePath { get; set; }
        public string? Bio { get; set; } 
        
        public string? RefreshToken { get; set; }
        public DateTime RefreshTokenExpiryTime { get; set; }
    }
}