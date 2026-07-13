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

        public EventTracker(
            Channel<UserEvent> queue, 
            ILogger<EventTracker> logger, 
            IHttpContextAccessor httpContextAccessor, 
            IConfiguration configuration)
        {
            _queue = queue;
            _logger = logger;
            _httpContextAccessor = httpContextAccessor;
            _configuration = configuration;
        }

        public void Track(string eventType, Guid? userId = null, object? properties = null)
        {
            try
            {
                var httpContext = _httpContextAccessor.HttpContext;
                Guid? sessionId = null;
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

                    // Get SessionId from HttpContext Items (set by middleware)
                    if (httpContext.Items.TryGetValue("SessionId", out var cachedSessionId) && cachedSessionId is Guid guidSessionId)
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

                var evt = new UserEvent
                {
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
                    _logger.LogWarning("Event queue full; dropped {EventType}", eventType);
                }
            }
            catch (Exception ex)
            {
                // Tracking must NEVER throw back to the user
                _logger.LogError(ex, "Failed to enqueue event {EventType}", eventType);
            }
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
