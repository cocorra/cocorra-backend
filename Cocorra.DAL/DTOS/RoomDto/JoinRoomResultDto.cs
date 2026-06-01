namespace Cocorra.DAL.DTOS.RoomDto;

public class JoinRoomResultDto
{
    /// <summary>
    /// LiveKit JWT token for the client to connect to the media server.
    /// Null when the user is pending approval in a private room.
    /// </summary>
    public string? LiveKitToken { get; set; }

    /// <summary>
    /// The LiveKit server WebSocket URL (e.g., wss://live.cocorraapp.com).
    /// Null when the user is pending approval in a private room.
    /// </summary>
    public string? LiveKitServerUrl { get; set; }
}
