using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Cocorra.DAL.DTOS.RoomDto;

public class IceServerDto
{
    [JsonPropertyName("urls")]
    public List<string> Urls { get; set; } = new();

    [JsonPropertyName("username")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Username { get; set; }

    [JsonPropertyName("credential")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Credential { get; set; }
}
