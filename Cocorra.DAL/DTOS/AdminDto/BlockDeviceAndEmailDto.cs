using System.ComponentModel.DataAnnotations;

namespace Cocorra.DAL.DTOS.AdminDto
{
    public class BlockDeviceAndEmailDto
    {
        [Required]
        [EmailAddress]
        public string Email { get; set; } = null!;
        
        [Required]
        public string DeviceId { get; set; } = null!;
        
        public string DeviceName { get; set; } = "Unknown";
        public string DeviceModel { get; set; } = "Unknown";
        public string DeviceType { get; set; } = "Unknown";
        public string DeviceOs { get; set; } = "Unknown";
    }
}
