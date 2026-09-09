using System.Text;
using Cocorra.BLL.Services.EventTracking;
using Cocorra.BLL.Services.LiveKit;
using Cocorra.DAL.AppMetaData;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;
using Cocorra.DAL.Repository.RoomRepository;
using Livekit.Server.Sdk.Dotnet;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Cocorra.API.Controllers
{
    /// <summary>
    /// LiveKit webhook ingestion. Two jobs, in priority order.
    ///
    /// <para>
    /// <b>Enforcement.</b> A LiveKit access token is a stateless signed JWT: nothing on the media
    /// server records that it was issued, so nothing can revoke it. A kicked user therefore holds
    /// a working credential until it expires, and can reconnect straight to the media server
    /// without touching this API. participant_joined is the one moment we learn about that
    /// connection, so it is where the database — the source of truth — gets to overrule the
    /// token. Eviction latency is a webhook round-trip rather than the token's remaining life.
    /// </para>
    ///
    /// <para>
    /// <b>AN-040 telemetry.</b> Cocorra otherwise has no media telemetry: a room where everyone
    /// failed to connect is indistinguishable from a room nobody attended. LiveKit participant
    /// identity is already the Cocorra user id, so correlation needs no new identifier scheme.
    /// </para>
    /// </summary>
    [ApiController]
    [AllowAnonymous] // Authenticated by webhook signature, not by JWT: the caller is LiveKit.
    public class LiveKitWebhookController : ControllerBase
    {
        private const string ParticipantJoinedEvent = "participant_joined";

        private readonly IEventTracker _eventTracker;
        private readonly IRoomRepository _roomRepo;
        private readonly ILiveKitService _liveKitService;
        private readonly WebhookReceiver _receiver;
        private readonly ILogger<LiveKitWebhookController> _logger;

        public LiveKitWebhookController(
            IEventTracker eventTracker,
            IRoomRepository roomRepo,
            ILiveKitService liveKitService,
            IOptions<LiveKitSettings> settings,
            ILogger<LiveKitWebhookController> logger)
        {
            _eventTracker = eventTracker;
            _roomRepo = roomRepo;
            _liveKitService = liveKitService;
            _logger = logger;

            var value = settings.Value;
            _receiver = new WebhookReceiver(value.ApiKey, value.ApiSecret);
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
            // check anyone could post arbitrary events, poison every media metric, and — now
            // that this endpoint can evict people — disconnect participants at will.
            //
            // Delegated to the SDK rather than hand-rolled. LiveKit does not send a bare HMAC:
            // the Authorization header is a JWT signed with the API secret carrying a `sha256`
            // claim over the body, and both the signature and that checksum have to be
            // verified. Receive throws if either fails.
            WebhookEvent webhookEvent;
            try
            {
                webhookEvent = _receiver.Receive(body, Request.Headers.Authorization.ToString(),
                    skipAuth: false, ignoreUnknownFields: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "LiveKit webhook rejected: signature or checksum invalid.");
                return Unauthorized();
            }

            var eventName = webhookEvent.Event ?? "unknown";

            // LiveKit room name and participant identity are already the Cocorra room id and
            // user id, so correlation needs no new scheme — only parsing.
            Guid? roomId = Guid.TryParse(webhookEvent.Room?.Name, out var parsedRoom) ? parsedRoom : null;
            Guid? userId = Guid.TryParse(webhookEvent.Participant?.Identity, out var parsedUser) ? parsedUser : null;

            // Enforcement runs BEFORE, and independently of, the analytics flag. Whether we are
            // recording media events is a reporting decision; whether a kicked user stays
            // connected is not, and gating this behind Analytics:EnableNewEventEmission would
            // silently disable a security control.
            if (eventName == ParticipantJoinedEvent && roomId.HasValue && userId.HasValue)
            {
                await EnforceParticipantIsStillAllowedAsync(roomId.Value, userId.Value);
            }

            if (!_eventTracker.NewEventEmissionEnabled)
            {
                // Acknowledge rather than error: LiveKit retries on a non-2xx, and there is no
                // point accumulating a retry backlog for events we are choosing not to record.
                return Ok();
            }

            _eventTracker.Track(EventTypes.MediaSessionEvent, userId, new
            {
                roomId,
                livekitEvent = eventName,
                // Disconnect reason is the whole point of this feed: it separates "the user
                // left" from "the connection failed", which look identical from our side.
                disconnectReason = webhookEvent.Participant?.DisconnectReason.ToString(),
                trackType = webhookEvent.Track?.Type.ToString()
            });

            return Ok();
        }

        /// <summary>
        /// Re-checks a joining participant against the database and disconnects them if the
        /// token they used no longer reflects reality — they were kicked, they were never an
        /// active participant, or the room has ended.
        ///
        /// Failures are logged and swallowed: LiveKit retries on a non-2xx, and a retry storm
        /// against a database that is already struggling would make an outage worse. A missed
        /// eviction degrades to the pre-webhook behaviour, which is the token's own expiry.
        /// </summary>
        private async Task EnforceParticipantIsStillAllowedAsync(Guid roomId, Guid userId)
        {
            try
            {
                var room = await _roomRepo.GetByIdAsync(roomId);
                var participant = await _roomRepo.GetParticipantAsync(roomId, userId);

                var reason =
                    room is null ? "room_not_found"
                    : room.Status != RoomStatus.Live ? $"room_{room.Status}".ToLowerInvariant()
                    : participant is null ? "not_a_participant"
                    : participant.Status != ParticipantStatus.Active ? $"participant_{participant.Status}".ToLowerInvariant()
                    : null;

                if (reason is null)
                {
                    return;
                }

                _logger.LogWarning(
                    "[LIVEKIT-ENFORCE] Evicting {UserId} from room {RoomId}: {Reason}. " +
                    "They connected with a token the database no longer honours.",
                    userId, roomId, reason);

                await _liveKitService.RemoveParticipantAsync(roomId, userId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "[LIVEKIT-ENFORCE] Could not verify or evict {UserId} in room {RoomId}. " +
                    "They remain connected until their token expires.",
                    userId, roomId);
            }
        }
    }
}
