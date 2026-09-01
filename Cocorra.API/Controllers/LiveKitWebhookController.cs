using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cocorra.BLL.Services.EventTracking;
using Cocorra.BLL.Services.LiveKit;
using Cocorra.DAL.AppMetaData;
using Cocorra.DAL.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Cocorra.API.Controllers
{
    /// <summary>
    /// AN-040 — LiveKit webhook ingestion.
    ///
    /// Cocorra has zero media telemetry: a room where everyone failed to connect is
    /// indistinguishable from a room nobody attended, and a call that dropped mid-sentence is
    /// indistinguishable from one that ended naturally. Both read as low engagement, and both
    /// would be answered with product changes rather than an infrastructure fix.
    ///
    /// The correlation key already exists — LiveKit participant identity is the Cocorra user id
    /// — so this needs no new identifier scheme, only somewhere to put the payload.
    /// </summary>
    [ApiController]
    [AllowAnonymous] // Authenticated by webhook signature, not by JWT: the caller is LiveKit.
    public class LiveKitWebhookController : ControllerBase
    {
        private readonly IEventTracker _eventTracker;
        private readonly LiveKitSettings _settings;
        private readonly ILogger<LiveKitWebhookController> _logger;

        public LiveKitWebhookController(
            IEventTracker eventTracker,
            IOptions<LiveKitSettings> settings,
            ILogger<LiveKitWebhookController> logger)
        {
            _eventTracker = eventTracker;
            _settings = settings.Value;
            _logger = logger;
        }

        [HttpPost(Router.AnalyticsRouting.LiveKitWebhook)]
        public async Task<IActionResult> Receive()
        {
            using var reader = new StreamReader(Request.Body, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();

            if (string.IsNullOrWhiteSpace(body))
            {
                return BadRequest();
            }

            // The endpoint is anonymous, so the signature IS the authentication. Without this
            // check anyone could post arbitrary events and poison every media metric.
            if (!IsSignatureValid(body))
            {
                _logger.LogWarning("LiveKit webhook rejected: signature missing or invalid.");
                return Unauthorized();
            }

            if (!_eventTracker.NewEventEmissionEnabled)
            {
                // Acknowledge rather than error: LiveKit retries on a non-2xx, and there is no
                // point accumulating a retry backlog for events we are choosing not to record.
                return Ok();
            }

            try
            {
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;

                var eventName = ReadString(root, "event") ?? "unknown";
                var roomName = root.TryGetProperty("room", out var room) ? ReadString(room, "name") : null;
                var participantIdentity = root.TryGetProperty("participant", out var participant)
                    ? ReadString(participant, "identity")
                    : null;

                // LiveKit room name and participant identity are already the Cocorra room id and
                // user id, so correlation needs no new scheme — only parsing.
                Guid? roomId = Guid.TryParse(roomName, out var parsedRoom) ? parsedRoom : null;
                Guid? userId = Guid.TryParse(participantIdentity, out var parsedUser) ? parsedUser : null;

                _eventTracker.Track(EventTypes.MediaSessionEvent, userId, new
                {
                    roomId,
                    livekitEvent = eventName,
                    // Disconnect reason is the whole point of this feed: it separates "the user
                    // left" from "the connection failed", which look identical from our side.
                    disconnectReason = participant.ValueKind == JsonValueKind.Object
                        ? ReadString(participant, "disconnectReason")
                        : null,
                    trackType = root.TryGetProperty("track", out var track) ? ReadString(track, "type") : null
                });
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "LiveKit webhook payload could not be parsed.");
                // Still 200: a malformed payload will not become well-formed on retry, and
                // failing here would make LiveKit retry it indefinitely.
                return Ok();
            }

            return Ok();
        }

        /// <summary>
        /// LiveKit signs the body with an HMAC-SHA256 of the API secret in the Authorization
        /// header. Compared in fixed time so the check cannot be probed byte by byte.
        /// </summary>
        private bool IsSignatureValid(string body)
        {
            if (string.IsNullOrWhiteSpace(_settings.ApiSecret))
            {
                _logger.LogError("LiveKit webhook cannot be verified: ApiSecret is not configured.");
                return false;
            }

            if (!Request.Headers.TryGetValue("Authorization", out var provided) || provided.Count == 0)
            {
                return false;
            }

            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_settings.ApiSecret));
            var expected = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(body)));

            var providedValue = provided.ToString();

            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(expected),
                Encoding.UTF8.GetBytes(providedValue));
        }

        private static string? ReadString(JsonElement element, string propertyName) =>
            element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }
}
