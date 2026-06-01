using System;
using System.Collections.Generic;
using System.Text;
using Cocorra.DAL.Enums;

namespace Cocorra.DAL.DTOS.RoomDto;

public class RoomStateDto
{
    public Guid RoomId { get; set; }
    public string RoomTitle { get; set; }=string.Empty;
    public Guid HostId { get; set; }
    public int TotalCapacity { get; set; }
    public int StageCapacity { get; set; }
    public RoomCategory Category { get; set; }
    public string CategoryName { get; set; } = string.Empty;
    public List<ParticipantStateDto> Participants { get; set; } = new List<ParticipantStateDto>();

    /// <summary>
    /// LiveKit JWT token for the requesting user to connect to the media server.
    /// </summary>
    public string? LiveKitToken { get; set; }

    /// <summary>
    /// The LiveKit server WebSocket URL.
    /// </summary>
    public string? LiveKitServerUrl { get; set; }
}
