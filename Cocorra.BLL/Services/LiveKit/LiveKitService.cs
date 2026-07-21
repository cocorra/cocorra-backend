using Livekit.Server.Sdk.Dotnet;
using Microsoft.Extensions.Options;

namespace Cocorra.BLL.Services.LiveKit;

public class LiveKitService : ILiveKitService
{
    private readonly LiveKitSettings _settings;

    public LiveKitService(IOptions<LiveKitSettings> settings)
    {
        _settings = settings.Value;
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
}
