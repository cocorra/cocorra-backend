namespace Cocorra.DAL.DTOS.NotificationDto
{
    public class BroadcastNotificationResultDto
    {
        /// <summary>ReferenceId of every Notification row saved for this broadcast.</summary>
        public Guid BroadcastId { get; set; }

        /// <summary>Active users who received the in-app notification.</summary>
        public int RecipientCount { get; set; }

        /// <summary>Recipients with a registered FCM token, i.e. the ones a push is sent to.</summary>
        public int PushTargetCount { get; set; }

        /// <summary>
        /// False when no push was queued: either nobody has a token or the push queue was full.
        /// The in-app notifications are saved either way.
        /// </summary>
        public bool PushQueued { get; set; }
    }
}
