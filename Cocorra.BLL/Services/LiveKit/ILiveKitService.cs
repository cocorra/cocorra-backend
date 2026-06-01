namespace Cocorra.BLL.Services.LiveKit;

public interface ILiveKitService
{
    /// <summary>
    /// Generates a LiveKit JWT for a participant to join a specific room.
    /// </summary>
    /// <param name="roomId">The room's unique identifier (used as LiveKit room name).</param>
    /// <param name="userId">The participant's unique identifier (used as LiveKit identity).</param>
    /// <param name="participantName">Display name for the participant.</param>
    /// <returns>A signed JWT string the client uses to connect to the LiveKit server.</returns>
    string GenerateToken(Guid roomId, Guid userId, string participantName);
}
