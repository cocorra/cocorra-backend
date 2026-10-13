using System;
using System.Collections.Generic;
using System.Text;

namespace Cocorra.BLL.Services.NotificationService
{
    public record MulticastPushResult(int SuccessCount, int FailureCount, IReadOnlyList<string> FailedTokens);

    public interface IPushNotificationService
    {
        Task SendPushNotificationAsync(string fcmToken, string title, string body, Dictionary<string, string> data);
        Task<MulticastPushResult> SendMulticastPushNotificationAsync(
            IReadOnlyList<string> fcmTokens,
            string title,
            string body,
            Dictionary<string, string>? data = null);
    }
}
