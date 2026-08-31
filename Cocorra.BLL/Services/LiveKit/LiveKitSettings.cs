using System.Collections.Generic;
using Cocorra.DAL.DTOS.RoomDto;

namespace Cocorra.BLL.Services.LiveKit;

public class LiveKitSettings
{
    public string ServerUrl { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string ApiSecret { get; set; } = string.Empty;
    public List<IceServerDto> IceServers { get; set; } = new();
}
