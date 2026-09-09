using System.Collections.Generic;
using Cocorra.DAL.DTOS.RoomDto;

namespace Cocorra.BLL.Services.LiveKit;

public class LiveKitSettings
{
    public string ServerUrl { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string ApiSecret { get; set; } = string.Empty;
    public List<IceServerDto> IceServers { get; set; } = new();

    /// <summary>
    /// Lifetime of an issued LiveKit access token.
    ///
    /// <para>
    /// This is a <b>reconnection window, not a session length limit</b>. The token is presented
    /// when a client connects; the LiveKit client SDK caches it and re-presents the same one on
    /// an automatic reconnect, so the TTL governs how long after joining a client can still
    /// re-establish a dropped connection without asking us for a new token. A participant whose
    /// token has expired stays connected — they just cannot come back if they drop.
    /// </para>
    ///
    /// <para>
    /// It is also the backstop on a credential we cannot revoke: a LiveKit token is a stateless
    /// signed JWT, so a kicked user holds a working one until it expires. The primary control is
    /// LiveKitWebhookController's participant_joined enforcement, which evicts them in a webhook
    /// round-trip; this bounds the exposure if that feed ever fails.
    /// </para>
    ///
    /// <para>
    /// Do not shorten this below the point where the mobile client can recover. Until the client
    /// re-fetches a token on an unexpected disconnect (GET /Room/{roomId}/Token), an expired
    /// token turns any network blip into a permanent ejection from the room.
    /// </para>
    /// </summary>
    public int TokenTtlMinutes { get; set; } = 240;
}
