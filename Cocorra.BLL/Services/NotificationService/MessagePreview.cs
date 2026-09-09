using System.Text.Json;

namespace Cocorra.BLL.Services.NotificationService
{
    /// <summary>
    /// Turns a stored message into something readable in the notification tray.
    ///
    /// Message content is an opaque string chosen by the client, and a rich message
    /// (attachment, voice note, reply) arrives as a JSON envelope. A closed app never runs
    /// Flutter's handlers — Android and iOS print notification.body verbatim — so forwarding
    /// that envelope shows the user raw JSON. Both message paths, private chat and support
    /// chat, take content from the same kind of client field and so share this.
    ///
    /// The envelope's field names are the client's to define, so this reads the usual
    /// text-bearing keys and otherwise says only that a message arrived, which is correct
    /// whatever the shape turns out to be. Plain text, the common case, is untouched.
    /// </summary>
    internal static class MessagePreview
    {
        internal const string Generic = "Sent you a message.";

        private static readonly string[] TextKeys = { "text", "message", "body", "content", "caption" };

        internal static string ForNotificationBody(string? content)
        {
            if (string.IsNullOrWhiteSpace(content))
                return Generic;

            var trimmed = content.Trim();

            // Cheap shape check first: parsing every plain-text message would be wasteful,
            // and JsonDocument accepts bare scalars ("42", "true") that are not envelopes.
            if (trimmed[0] != '{')
                return trimmed;

            try
            {
                using var document = JsonDocument.Parse(trimmed);

                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    return Generic;

                foreach (var key in TextKeys)
                {
                    if (document.RootElement.TryGetProperty(key, out var value)
                        && value.ValueKind == JsonValueKind.String)
                    {
                        var text = value.GetString();
                        if (!string.IsNullOrWhiteSpace(text))
                            return text.Trim();
                    }
                }

                return Generic;
            }
            catch (JsonException)
            {
                // Starts with '{' but is not valid JSON — ordinary text, show it as typed.
                return trimmed;
            }
        }
    }
}
