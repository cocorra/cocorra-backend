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

    /// <summary>
    /// Tears the room down on the media server, disconnecting everyone still publishing or
    /// subscribed to it.
    ///
    /// <para>
    /// Ending a room in the database and broadcasting RoomEnded only tells clients to leave.
    /// A client that ignores the message — or never receives it — stays on the audio bridge,
    /// so a "closed" session could still be carrying live voice between participants. This is
    /// what actually stops it.
    /// </para>
    ///
    /// <para>
    /// Throws on failure. The caller decides whether that should surface, and every current
    /// caller has already committed the end to the database by this point.
    /// </para>
    /// </summary>
    Task CloseRoomAsync(Guid roomId);

    /// <summary>
    /// Disconnects one participant from the media server, leaving the room running for
    /// everyone else. The counterpart to <see cref="CloseRoomAsync"/> for a kick, which
    /// otherwise removes someone from the database and the hub while leaving their audio
    /// connection intact.
    /// </summary>
    Task RemoveParticipantAsync(Guid roomId, Guid userId);
}
