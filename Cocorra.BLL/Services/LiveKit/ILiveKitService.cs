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

    /// <summary>
    /// Creates the room on the media server ahead of anyone connecting to it, and returns
    /// whether that succeeded.
    ///
    /// <para>
    /// LiveKit's <c>auto_create</c> defaults to true, which means the first valid token to
    /// arrive conjures the room into existence. That quietly undoes <see cref="CloseRoomAsync"/>:
    /// a participant holding an unexpired token who reconnects after a room has ended will
    /// <i>recreate</i> it and be admitted, and if two of them do it they can hear each other in
    /// a session the database considers over. The participant_joined webhook evicts them, but
    /// only after a round-trip, and only while that feed is healthy.
    /// </para>
    ///
    /// <para>
    /// Creating rooms explicitly is what makes turning <c>auto_create</c> off survivable — with
    /// it off, a token for a room that does not exist is simply refused, with no window and no
    /// dependence on webhook delivery. Called at go-live so the room exists for the whole
    /// session; <paramref name="emptyTimeout"/> has to outlast the longest bookable room, or
    /// LiveKit reaps it during a quiet moment and nobody can get back in.
    /// </para>
    ///
    /// <para>
    /// Never throws — a failure here must not stop a room going live while
    /// <c>auto_create</c> is still on, because auto-creation is still there to cover it. Check
    /// the return value before relying on the room existing.
    /// </para>
    /// </summary>
    Task<bool> EnsureRoomExistsAsync(Guid roomId, TimeSpan emptyTimeout);
}
