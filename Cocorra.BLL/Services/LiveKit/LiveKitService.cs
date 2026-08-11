using System.Text;
using System.Text.Json;
using Livekit.Server.Sdk.Dotnet;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cocorra.BLL.Services.LiveKit;

public class LiveKitService : ILiveKitService
{
    private readonly LiveKitSettings _settings;
    private readonly RoomServiceClient _roomServiceClient;
    private readonly ILogger<LiveKitService>? _logger;

    public LiveKitService(IOptions<LiveKitSettings> settings, ILogger<LiveKitService>? logger = null)
    {
        _settings = settings.Value;
        _logger = logger;
        _roomServiceClient = new RoomServiceClient(ToHttpHost(_settings.ServerUrl), _settings.ApiKey, _settings.ApiSecret);
    }

    /// <summary>
    /// LiveKit's server (Twirp) API is served over http(s) on the same host as the
    /// ws(s) media endpoint — translate the configured ServerUrl accordingly.
    /// </summary>
    private static string ToHttpHost(string serverUrl)
    {
        if (serverUrl.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
            return "https://" + serverUrl["wss://".Length..];
        if (serverUrl.StartsWith("ws://", StringComparison.OrdinalIgnoreCase))
            return "http://" + serverUrl["ws://".Length..];
        return serverUrl;
    }

    /// <inheritdoc />
    public string GenerateToken(Guid roomId, Guid userId, string participantName, bool canPublish)
    {
        var token = new AccessToken(_settings.ApiKey, _settings.ApiSecret)
            .WithIdentity(userId.ToString())
            .WithName(participantName)
            .WithGrants(new VideoGrants
            {
                RoomJoin = true,
                Room = roomId.ToString(),
                CanPublish = canPublish,
                CanSubscribe = true
            })
            .WithTtl(TimeSpan.FromHours(4)); // Covers max 3h room + 1h buffer

        var jwt = token.ToJwt();
        LogIssuedToken(jwt, roomId, userId, participantName);
        return jwt;
    }

    // ==========================================================================
    // TEMPORARY AUDIT LOGGING — remove once the stage/mic investigation is done.
    // Decodes the JWT that was actually issued and logs the real grant values,
    // so we log what the token contains rather than what we intended it to
    // contain. Never throws into the request path and never alters the token.
    // ==========================================================================
    private void LogIssuedToken(string jwt, Guid roomId, Guid userId, string participantName)
    {
        if (_logger is null) return;

        try
        {
            var parts = jwt.Split('.');
            if (parts.Length != 3) return;

            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
            var root = doc.RootElement;
            var video = root.GetProperty("video");

            bool Grant(string name) =>
                video.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

            var exp = DateTimeOffset.FromUnixTimeSeconds(root.GetProperty("exp").GetInt64()).UtcDateTime;

            _logger.LogInformation(
                "[LIVEKIT-TOKEN-AUDIT] Room={Room} Identity={Identity} ParticipantName={ParticipantName} " +
                "RoomJoin={RoomJoin} CanPublish={CanPublish} CanSubscribe={CanSubscribe} CanPublishData={CanPublishData} " +
                "TokenExpirationUtc={Expiration} TtlMinutes={TtlMinutes} JwtLength={JwtLength} " +
                "ApiKeyLoaded={ApiKeyLoaded} ApiSecretLoaded={ApiSecretLoaded}",
                video.TryGetProperty("room", out var r) ? r.GetString() : "(none)",
                root.TryGetProperty("sub", out var s) ? s.GetString() : "(none)",
                root.TryGetProperty("name", out var n) ? n.GetString() : "(none)",
                Grant("roomJoin"),
                Grant("canPublish"),
                Grant("canSubscribe"),
                Grant("canPublishData"),
                exp.ToString("o"),
                Math.Round((exp - DateTime.UtcNow).TotalMinutes),
                jwt.Length,
                !string.IsNullOrWhiteSpace(_settings.ApiKey),
                !string.IsNullOrWhiteSpace(_settings.ApiSecret));

            if (!Grant("canPublish"))
            {
                _logger.LogWarning(
                    "[LIVEKIT-TOKEN-AUDIT] Issued a LISTEN-ONLY token (canPublish=false) for Identity={Identity} " +
                    "in Room={Room}. If this user is on stage, their mic will not work until " +
                    "UpdateStagePermissionAsync succeeds.",
                    userId, roomId);
            }
        }
        catch (Exception ex)
        {
            // Audit logging must never affect token issuance.
            _logger.LogDebug(ex, "[LIVEKIT-TOKEN-AUDIT] Failed to decode issued token for audit logging.");
        }
    }

    /// <inheritdoc />
    public async Task UpdateStagePermissionAsync(Guid roomId, Guid userId, bool canPublish)
    {
        try
        {
            await _roomServiceClient.UpdateParticipant(new UpdateParticipantRequest
            {
                Room = roomId.ToString(),
                Identity = userId.ToString(),
                Permission = new ParticipantPermission
                {
                    CanPublish = canPublish,
                    CanSubscribe = true,
                    CanPublishData = true
                }
            });

            // TEMPORARY AUDIT LOGGING — see LogIssuedToken.
            _logger?.LogInformation(
                "[LIVEKIT-PERM-AUDIT] UpdateParticipant OK. Room={Room} Identity={Identity} " +
                "CanPublish={CanPublish} CanSubscribe=true CanPublishData=true",
                roomId, userId, canPublish);
        }
        catch (Exception ex)
        {
            // TEMPORARY AUDIT LOGGING — log and rethrow so callers behave exactly as before.
            _logger?.LogError(ex,
                "[LIVEKIT-PERM-AUDIT] UpdateParticipant FAILED. Room={Room} Identity={Identity} " +
                "CanPublish={CanPublish}. The participant's publish permission was NOT changed on the media server.",
                roomId, userId, canPublish);
            throw;
        }
    }
}
