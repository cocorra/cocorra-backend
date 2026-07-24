namespace Cocorra.BLL.Services.LiveKit;

public interface ILiveKitService
{
    /// <summary>
    /// Generates a LiveKit JWT for a participant to join a specific room.
    /// </summary>
    /// <param name="roomId">The room's unique identifier (used as LiveKit room name).</param>
    /// <param name="userId">The participant's unique identifier (used as LiveKit identity).</param>
    /// <param name="participantName">Display name for the participant.</param>
    /// <param name="canPublish">Whether the participant may publish audio/video (true for host/stage, false for audience).</param>
    /// <returns>A signed JWT string the client uses to connect to the LiveKit server.</returns>
    string GenerateToken(Guid roomId, Guid userId, string participantName, bool canPublish);

    /// <summary>
    /// Pushes an updated publish permission to an already-connected LiveKit participant
    /// via the server API, so mic access changes (stage promotion/demotion) take effect
    /// immediately without requiring the client to reconnect.
    /// </summary>
    /// <param name="roomId">The room's unique identifier (the LiveKit room name).</param>
    /// <param name="userId">The participant's unique identifier (the LiveKit identity).</param>
    /// <param name="canPublish">Whether the participant may publish audio/video.</param>
    Task UpdateStagePermissionAsync(Guid roomId, Guid userId, bool canPublish);
}
