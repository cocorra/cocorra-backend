using System.ComponentModel.DataAnnotations;

namespace Cocorra.DAL.DTOS.NotificationDto
{
    /// <summary>
    /// Request body for POST /Api/V1/Admin/Notifications/Broadcast. The limits match the
    /// Notification columns each recipient's in-app copy is stored in.
    /// </summary>
    public class BroadcastNotificationDto
    {
        [Required]
        [MaxLength(100)]
        public string Title { get; set; } = string.Empty;

        [Required]
        [MaxLength(500)]
        public string Message { get; set; } = string.Empty;
    }
}
