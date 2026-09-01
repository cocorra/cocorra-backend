using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Cocorra.DAL.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Cocorra.BLL.Services.EventTracking
{
    public class EventTracker : IEventTracker
    {
        private readonly Channel<UserEvent> _queue;
        private readonly ILogger<EventTracker> _logger;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly IConfiguration _configuration;
        private readonly EventPipelineMetrics _metrics;

        public EventTracker(
            Channel<UserEvent> queue, 
            ILogger<EventTracker> logger, 
            IHttpContextAccessor httpContextAccessor, 
            IConfiguration configuration,
            EventPipelineMetrics? metrics = null)
        {
            _queue = queue;
            _logger = logger;
            _httpContextAccessor = httpContextAccessor;
            _configuration = configuration;
            _metrics = metrics ?? new EventPipelineMetrics();
            // Indexer rather than GetValue<T>: consistent with how the IP-hash salt is read,
            // and tolerant of a stubbed IConfiguration that has no section support.
            NewEventEmissionEnabled =
                bool.TryParse(configuration["Analytics:EnableNewEventEmission"], out var newEventsOn) && newEventsOn;

            // Deliberately conjunctive: the high-frequency increment cannot be enabled on its
            // own. Deploying it before the low-frequency one has proven stable would remove any
            // ability to attribute a drop-rate spike to one increment or the other.
            HighFrequencyEventsEnabled =
                NewEventEmissionEnabled
                && bool.TryParse(configuration["Analytics:EnableHighFrequencyEvents"], out var highFreqOn)
                && highFreqOn;
        }

        /// <inheritdoc />
        public bool NewEventEmissionEnabled { get; }

        /// <inheritdoc />
        public bool HighFrequencyEventsEnabled { get; }

        public void Track(string eventType, Guid? userId = null, object? properties = null)
        {
            Track(eventType, userId, properties, eventKey: null, sessionId: null, correlationId: null);
        }

        public void Track(
            string eventType,
            Guid? userId,
            object? properties,
            string? eventKey = null,
            Guid? sessionId = null,
            Guid? correlationId = null,
            byte schemaVersion = 1)
        {
            try
            {
                var httpContext = _httpContextAccessor.HttpContext;
                string? ipHash = null;
                string? userAgent = null;

                if (httpContext != null)
                {
                    // Fallback to authenticated user if userId is null
                    if (userId == null && httpContext.User.Identity?.IsAuthenticated == true)
                    {
                        var userIdString = httpContext.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
                        if (Guid.TryParse(userIdString, out var resolvedId))
                        {
                            userId = resolvedId;
                        }
                    }

                    // Get SessionId from HttpContext Items if not explicitly provided
                    if (sessionId == null && httpContext.Items.TryGetValue("SessionId", out var cachedSessionId) && cachedSessionId is Guid guidSessionId)
                    {
                        sessionId = guidSessionId;
                    }

                    // Resolve User-Agent
                    userAgent = httpContext.Request.Headers["User-Agent"].ToString();
                    if (!string.IsNullOrEmpty(userAgent) && userAgent.Length > 256)
                    {
                        userAgent = userAgent.Substring(0, 256);
                    }

                    // Resolve IP Hash
                    var remoteIp = httpContext.Connection.RemoteIpAddress?.ToString();
                    var salt = _configuration["Analytics:IpHashSalt"];
                    // No hardcoded fallback: a public salt would make hashes reversible.
                    // Startup guards on this key (Program.cs), so it is expected to be present.
                    if (!string.IsNullOrEmpty(remoteIp) && !string.IsNullOrEmpty(salt))
                    {
                        ipHash = HashIpAddress(remoteIp, salt);
                    }
                }

                var propertiesJson = properties is null ? null : JsonSerializer.Serialize(properties);

                // Deterministic EventId derivation if caller provided natural eventKey
                var eventId = !string.IsNullOrWhiteSpace(eventKey)
                    ? DeriveDeterministicGuid(eventKey)
                    : Guid.NewGuid();

                var evt = new UserEvent
                {
                    EventId = eventId,
                    SchemaVersion = schemaVersion,
                    CorrelationId = correlationId,
                    EventType = eventType,
                    UserId = userId,
                    PropertiesJson = propertiesJson,
                    RoomId = ExtractRoomId(propertiesJson),
                    SessionId = sessionId,
                    IpHash = ipHash,
                    UserAgent = userAgent,
                    OccurredAtUtc = DateTime.UtcNow
                };

                // Non-blocking write to channel
                if (!_queue.Writer.TryWrite(evt))
                {
                    // R-1: this is the pipeline's original silent loss path. The counter makes
                    // the rate observable without grepping a rotating container log.
                    _metrics.RecordDroppedOnEnqueue();
                    _logger.LogWarning("Event queue full; dropped {EventType}", eventType);
                }
                else
                {
                    _metrics.RecordEnqueued();
                }
            }
            catch (Exception ex)
            {
                // Tracking must NEVER throw back to the user
                _logger.LogError(ex, "Failed to enqueue event {EventType}", eventType);
            }
        }

        /// <summary>
        /// Derives a stable EventId from a natural key so the same logical event produces the
        /// same id in any process. SHA-256 rather than MD5: MD5 throws when Windows FIPS policy
        /// is enforced and trips security scanners, and nothing here needs MD5's speed.
        /// The version and variant bits are set so the result is a well-formed RFC-4122 UUID
        /// rather than 16 arbitrary bytes.
        /// </summary>
        internal static Guid DeriveDeterministicGuid(string eventKey)
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(eventKey));

            var guidBytes = new byte[16];
            Array.Copy(hash, guidBytes, 16);

            guidBytes[7] = (byte)((guidBytes[7] & 0x0F) | 0x50); // version 5 (name-based, SHA-1 family)
            guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80); // RFC-4122 variant

            return new Guid(guidBytes);
        }

        /// <summary>
        /// Promotes a "roomId" property (if present and a valid GUID) to the indexed RoomId
        /// column. Emit sites pass it naturally via `new { roomId = ... }`. Never throws.
        /// </summary>
        private static Guid? ExtractRoomId(string? propertiesJson)
        {
            if (string.IsNullOrEmpty(propertiesJson))
            {
                return null;
            }

            try
            {
                using var doc = JsonDocument.Parse(propertiesJson);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    // Case-insensitive so a "RoomId" / "roomId" payload both promote correctly.
                    if (string.Equals(prop.Name, "roomId", StringComparison.OrdinalIgnoreCase)
                        && prop.Value.ValueKind == JsonValueKind.String
                        && Guid.TryParse(prop.Value.GetString(), out var roomId))
                    {
                        return roomId;
                    }
                }
            }
            catch (JsonException)
            {
                // Non-object or malformed payload — no room id to promote.
            }

            return null;
        }

        private static string HashIpAddress(string ipAddress, string salt)
        {
            var input = salt + ipAddress;
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
            return Convert.ToHexString(bytes);
        }
    }
}
