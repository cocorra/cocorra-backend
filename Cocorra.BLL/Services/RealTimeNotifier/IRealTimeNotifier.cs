using System;
using System.Threading.Tasks;

namespace Cocorra.BLL.Services.RealTimeNotifier
{
    /// <summary>
    /// Abstraction for broadcasting real-time events to connected clients.
    /// Decouples the BLL from SignalR hub implementations in the API layer.
    /// </summary>
    public interface IRealTimeNotifier
    {
        /// <summary>
        /// Sends a ForceLogout event to a specific user, causing the client to
        /// disconnect from rooms and clear the session immediately.
        /// </summary>
        Task ForceLogoutAsync(Guid userId, string reason);

        /// <summary>
        /// Tells everyone still in a room that it has ended. Used by
        /// HostReconnectGraceService, which ends rooms from outside any hub invocation and so
        /// has no Clients.Group of its own to broadcast on.
        /// </summary>
        Task RoomEndedAsync(Guid roomId, string message);
    }
}
