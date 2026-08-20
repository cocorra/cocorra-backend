using System;
using System.Threading.Tasks;

using FirebaseAdmin.Messaging;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;

namespace Cocorra.BLL.Services.NotificationService
{
    public class PushNotificationService : IPushNotificationService
    {
        private readonly ILogger<PushNotificationService> _logger;

        public PushNotificationService(ILogger<PushNotificationService> logger)
        {
            _logger = logger;
        }

        public async Task SendPushNotificationAsync(string fcmToken, string title, string body, Dictionary<string, string> data)
        {
            var type = data?.GetValueOrDefault("type", "unknown") ?? "unknown";

            if (string.IsNullOrWhiteSpace(fcmToken))
            {
                _logger.LogWarning("FCM push skipped: token is null or empty. Type: {Type}", type);
                return;
            }

            // FirebaseMessaging.DefaultInstance returns null (it does not throw) when
            // FirebaseApp.Create was never called — e.g. firebase-config.json missing at
            // startup (Program.cs). Guard here so we log the real cause instead of letting
            // a NullReferenceException escape into a caller's catch block.
            if (FirebaseMessaging.DefaultInstance == null)
            {
                _logger.LogError(
                    "FCM push FAILED: FirebaseMessaging.DefaultInstance is null. " +
                    "Ensure firebase-config.json exists and FirebaseApp.Create() succeeded at startup. Type: {Type}",
                    type);
                return;
            }

            // An alert payload changes how both platforms must be addressed, so decide once.
            var hasAlert = !string.IsNullOrWhiteSpace(title) || !string.IsNullOrWhiteSpace(body);

            var message = new Message()
            {
                Token = fcmToken,
                Data = data,

                // High priority so the message is not deferred while the device is in Doze.
                Android = new AndroidConfig()
                {
                    Priority = Priority.High
                },

                // apns-push-type is required from iOS 13 on. Apple rejects a background
                // push sent at priority 10 (BadPriority), so alert and data-only pushes
                // must not share the same headers.
                Apns = new ApnsConfig()
                {
                    Headers = new Dictionary<string, string>
                    {
                        { "apns-push-type", hasAlert ? "alert" : "background" },
                        { "apns-priority", hasAlert ? "10" : "5" }
                    },
                    Aps = new Aps()
                    {
                        // Only meaningful for data-only pushes; on an alert push it would
                        // just make the payload's intent ambiguous.
                        ContentAvailable = !hasAlert
                    }
                }
            };

            // CRITICAL: Only attach Notification when title/body are non-empty.
            // Firebase treats ANY Notification object (even with empty strings) as a
            // "display" notification, which can cause blank pop-ups on Android 13+
            // and prevents silent background handling on iOS.
            if (hasAlert)
            {
                message.Notification = new Notification()
                {
                    Title = title,
                    Body = body
                };
            }

            try
            {
                var messageId = await FirebaseMessaging.DefaultInstance.SendAsync(message);
                _logger.LogInformation(
                    "FCM push sent successfully. MessageId: {MessageId}, Type: {Type}", messageId, type);
            }
            catch (FirebaseMessagingException ex)
            {
                // Log the FCM error code so token, quota and payload problems are
                // diagnosable from server logs. Only the token suffix is logged.
                _logger.LogError(ex,
                    "FCM push FAILED. MessagingErrorCode: {ErrorCode}, FcmToken (last 8): ...{TokenSuffix}, Type: {Type}",
                    ex.MessagingErrorCode,
                    fcmToken.Length > 8 ? fcmToken[^8..] : fcmToken,
                    type);
            }
            catch (Exception ex)
            {
                // Catch-all so callers can await this without their own try/catch.
                _logger.LogError(ex, "FCM push FAILED with unexpected exception. Type: {Type}", type);
            }
        }
    }
}
