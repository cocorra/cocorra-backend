using Livekit.Server.Sdk.Dotnet;
using Microsoft.Extensions.Options;

namespace Cocorra.BLL.Services.LiveKit;

public class LiveKitService : ILiveKitService
{
    private readonly LiveKitSettings _settings;
    private readonly RoomServiceClient _roomServiceClient;

    public LiveKitService(IOptions<LiveKitSettings> settings)
    {
        _settings = settings.Value;
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

        return token.ToJwt();
    }

    /// <inheritdoc />
    public async Task UpdateStagePermissionAsync(Guid roomId, Guid userId, bool canPublish)
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
    }
}
