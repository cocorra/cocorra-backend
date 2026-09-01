using System;
using System.Diagnostics;
using System.Threading.Tasks;

using Cocorra.BLL.Services.EventTracking;
// Alias rather than a plain using: Cocorra.DAL.Models also defines Message and Notification,
// which would collide with the FirebaseAdmin.Messaging types used throughout this file.
using EventTypes = Cocorra.DAL.Models.EventTypes;
using FirebaseAdmin.Messaging;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;

namespace Cocorra.BLL.Services.NotificationService
{
    public class PushNotificationService : IPushNotificationService
    {
        private readonly ILogger<PushNotificationService> _logger;
        private readonly IEventTracker? _eventTracker;

        public PushNotificationService(
            ILogger<PushNotificationService> logger,
            IEventTracker? eventTracker = null)
        {
            _logger = logger;
            _eventTracker = eventTracker;
        }

        /// <summary>
        /// AN-024. Records the FCM outcome rather than only logging it.
        ///
        /// The reversed-delivery defect fixed in dc1c933 was invisible from the data: the FCM
        /// response went to stdout and was discarded, so nothing could be queried, counted or
        /// alerted on. correlationId links each attempt to its result, so a silent hang shows up
        /// as attempts without results — something a success-only counter would never reveal.
        /// </summary>
        public async Task SendPushNotificationAsync(string fcmToken, string title, string body, Dictionary<string, string> data)
        {
            var type = data?.GetValueOrDefault("type", "unknown") ?? "unknown";
            var correlationId = Guid.NewGuid();

            Guid? targetUserId = data is not null
                                 && data.TryGetValue("userId", out var rawUserId)
                                 && Guid.TryParse(rawUserId, out var parsedUserId)
                ? parsedUserId
                : null;

            if (string.IsNullOrWhiteSpace(fcmToken))
            {
                // A missing token is a delivery failure with a cause, not a no-op — it is
                // precisely the shape of the token-clearing regression this guard exists for.
                TrackAttempt(targetUserId, correlationId, type);
                TrackResult(targetUserId, correlationId, type, success: false,
                    errorCode: "missing_token", tokenInvalidated: false, latencyMs: 0);

                _logger.LogWarning("FCM push skipped: token is null or empty. Type: {Type}", type);
                return;
            }

            TrackAttempt(targetUserId, correlationId, type);

            // FirebaseMessaging.DefaultInstance returns null (it does not throw) when
            // FirebaseApp.Create was never called — e.g. firebase-config.json missing at
            // startup (Program.cs). Guard here so we log the real cause instead of letting
            // a NullReferenceException escape into a caller's catch block.
            if (FirebaseMessaging.DefaultInstance == null)
            {
                TrackResult(targetUserId, correlationId, type, success: false,
                    errorCode: "firebase_not_initialised", tokenInvalidated: false, latencyMs: 0);

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

            var stopwatch = Stopwatch.StartNew();

            try
            {
                var messageId = await FirebaseMessaging.DefaultInstance.SendAsync(message);
                stopwatch.Stop();

                TrackResult(targetUserId, correlationId, type, success: true,
                    errorCode: null, tokenInvalidated: false, latencyMs: stopwatch.ElapsedMilliseconds);

                _logger.LogInformation(
                    "FCM push sent successfully. MessageId: {MessageId}, Type: {Type}", messageId, type);
            }
            catch (FirebaseMessagingException ex)
            {
                stopwatch.Stop();

                // Unregistered and InvalidArgument mean the token is dead, which is what should
                // drive token cleanup. Separating that from a transient FCM outage matters,
                // because the two call for opposite responses.
                var tokenInvalidated = ex.MessagingErrorCode is MessagingErrorCode.Unregistered
                                                             or MessagingErrorCode.InvalidArgument;

                TrackResult(targetUserId, correlationId, type, success: false,
                    errorCode: ex.MessagingErrorCode?.ToString() ?? "unknown",
                    tokenInvalidated: tokenInvalidated,
                    latencyMs: stopwatch.ElapsedMilliseconds);

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
                stopwatch.Stop();

                TrackResult(targetUserId, correlationId, type, success: false,
                    errorCode: ex.GetType().Name, tokenInvalidated: false,
                    latencyMs: stopwatch.ElapsedMilliseconds);

                // Catch-all so callers can await this without their own try/catch.
                _logger.LogError(ex, "FCM push FAILED with unexpected exception. Type: {Type}", type);
            }
        }

        private void TrackAttempt(Guid? userId, Guid correlationId, string type)
        {
            if (_eventTracker?.NewEventEmissionEnabled != true)
            {
                return;
            }

            _eventTracker.Track(
                EventTypes.PushSendAttempted,
                userId,
                new { notificationType = type },
                correlationId: correlationId);
        }

        private void TrackResult(
            Guid? userId,
            Guid correlationId,
            string type,
            bool success,
            string? errorCode,
            bool tokenInvalidated,
            long latencyMs)
        {
            if (_eventTracker?.NewEventEmissionEnabled != true)
            {
                return;
            }

            _eventTracker.Track(
                EventTypes.PushSendResult,
                userId,
                new { notificationType = type, success, errorCode, tokenInvalidated, latencyMs },
                correlationId: correlationId);
        }
    }
}
