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
        /// <summary>
        /// notification.body is rendered verbatim by the Android and iOS system trays when the
        /// app is closed — Flutter's handlers never run, so nothing downstream can reformat it.
        /// A body that is really a serialized payload therefore reaches the user as raw JSON.
        /// Structured data belongs in Data, which Flutter parses; this is the last guard
        /// before the wire, for call sites that pass through content they do not control.
        /// </summary>
        internal const string OpaqueBodyFallback = "You have a new notification.";

        /// <summary>
        /// FCM caps the whole payload at 4 KB and neither tray collapses gracefully past a
        /// couple of lines, so a long chat message is truncated rather than sent whole.
        /// </summary>
        internal const int MaxBodyLength = 240;

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

            // Never let a serialized payload reach the tray. See OpaqueBodyFallback.
            var displayBody = BuildDisplayBody(body, type);

            // An alert payload changes how both platforms must be addressed, so decide once.
            var hasAlert = !string.IsNullOrWhiteSpace(title) || !string.IsNullOrWhiteSpace(displayBody);

            // Mirror the alert text into Data so the app can render its own notification from
            // the data payload alone — in the foreground, and if these pushes are ever moved
            // to data-only. Copied first: callers reuse a single dictionary across recipients
            // (RoomService's reminder loop), so mutating the argument would leak between sends.
            var payloadData = data is null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string>(data);

            if (hasAlert)
            {
                payloadData["title"] = title ?? string.Empty;
                payloadData["body"] = displayBody ?? string.Empty;
            }

            var message = new Message()
            {
                Token = fcmToken,
                Data = payloadData,

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
                    Body = displayBody
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

        /// <summary>
        /// Returns a body safe to hand to the OS tray: prose, never a serialized payload,
        /// never longer than <see cref="MaxBodyLength"/>.
        ///
        /// A caller that forwards content it does not control — chat is the one that does —
        /// can hand us a JSON envelope. When the app is closed the tray prints
        /// notification.body as-is, so that envelope becomes what the user reads. Callers are
        /// expected to build their own preview (ChatService does); this only catches the ones
        /// that do not, and is deliberately conservative: it looks at the shape of the string
        /// rather than parsing, so a message that merely mentions braces is left alone.
        /// </summary>
        internal string? BuildDisplayBody(string? body, string type)
        {
            if (string.IsNullOrWhiteSpace(body))
            {
                return body;
            }

            var trimmed = body.Trim();

            if (LooksLikeSerializedPayload(trimmed))
            {
                // Warn rather than fail: the notification is still worth delivering, but a
                // call site reaching this point is a bug at that call site, not here.
                _logger.LogWarning(
                    "FCM body looked like a serialized payload and was replaced with a generic " +
                    "message. The structured value is still available in Data. Type: {Type}", type);

                return OpaqueBodyFallback;
            }

            return trimmed.Length > MaxBodyLength
                ? string.Concat(trimmed.AsSpan(0, MaxBodyLength - 1).TrimEnd(), "…")
                : trimmed;
        }

        private static bool LooksLikeSerializedPayload(string trimmedBody)
        {
            return trimmedBody.Length > 1
                   && ((trimmedBody[0] == '{' && trimmedBody[^1] == '}')
                       || (trimmedBody[0] == '[' && trimmedBody[^1] == ']'));
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
