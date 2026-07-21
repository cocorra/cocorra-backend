using System.Text.Json.Serialization;

namespace Cocorra.DAL.DTOS.RoomDto;

public class JoinRoomResultDto
{
    /// <summary>
    /// The LiveKit server WebSocket URL (e.g., wss://live.cocorraapp.com).
    /// Null when the user is pending approval in a private room.
    /// </summary>
    [JsonPropertyName("livekitUrl")]
    public string? LiveKitServerUrl { get; set; }

    /// <summary>
    /// LiveKit JWT token for the client to connect to the media server.
    /// Null when the user is pending approval in a private room.
    /// </summary>
    [JsonPropertyName("livekitToken")]
    public string? LiveKitToken { get; set; }

    /// <summary>
    /// The LiveKit room name the client must connect to (the room's GUID).
    /// Null when the user is pending approval in a private room.
    /// </summary>
    [JsonPropertyName("roomName")]
    public string? RoomName { get; set; }
}
