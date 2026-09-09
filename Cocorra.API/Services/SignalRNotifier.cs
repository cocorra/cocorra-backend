using System;
using System.Threading.Tasks;
using Cocorra.API.Hubs;
using Cocorra.BLL.Services.RealTimeNotifier;
using Microsoft.AspNetCore.SignalR;

namespace Cocorra.API.Services
{
    /// <summary>
    /// Concrete implementation of IRealTimeNotifier using SignalR's IHubContext.
    /// Registered in DI at the API layer, injected into BLL services via the interface.
    /// </summary>
    public class SignalRNotifier : IRealTimeNotifier
    {
        private readonly IHubContext<RoomHub> _roomHubContext;

        public SignalRNotifier(IHubContext<RoomHub> roomHubContext)
        {
            _roomHubContext = roomHubContext;
        }

        public async Task ForceLogoutAsync(Guid userId, string reason)
        {
            try
            {
                await _roomHubContext.Clients.User(userId.ToString())
                    .SendAsync("ForceLogout", new
                    {
                        Reason = reason,
                        Timestamp = DateTime.UtcNow
                    });
            }
            catch { /* SignalR broadcast failure must not block admin actions */ }
        }

        public async Task RoomEndedAsync(Guid roomId, string message)
        {
            try
            {
                await _roomHubContext.Clients.Group(roomId.ToString())
                    .SendAsync("RoomEnded", new
                    {
                        RoomId = roomId,
                        Message = message
                    });
            }
            catch { /* The room is already ended in the database; a failed broadcast
                       must not stop the sweep from moving on to the next room. */ }
        }
    }
}
