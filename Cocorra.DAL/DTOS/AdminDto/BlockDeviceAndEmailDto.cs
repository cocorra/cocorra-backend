using System.ComponentModel.DataAnnotations;

namespace Cocorra.DAL.DTOS.AdminDto
{
    /// <summary>
    /// Email is the only input. The device fields this used to carry could only ever be filled
    /// in by the admin's own client, which meant the admin blocked their own device and got
    /// locked out by DeviceBlockingMiddleware. The devices to block are now resolved
    /// server-side from the registry written at the offender's login.
    /// </summary>
    public class BlockDeviceAndEmailDto
    {
        [Required]
        [EmailAddress]
        public string Email { get; set; } = null!;
    }
}
