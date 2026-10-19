using Cocorra.BLL.Base;
using Cocorra.DAL.DTOS.NotificationDto;

namespace Cocorra.BLL.Services.NotificationService
{
    public interface IBroadcastNotificationService
    {
        /// <summary>
        /// Saves an in-app notification for every Active user and queues a push to each of them
        /// that has an FCM token. adminId is recorded in the audit log.
        /// </summary>
        Task<Response<BroadcastNotificationResultDto>> BroadcastAsync(BroadcastNotificationDto dto, Guid adminId);
    }
}
