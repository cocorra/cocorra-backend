using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using System;
using System.Threading.Tasks;

namespace Cocorra.API.Hubs
{
    [Authorize]
    public class SupportHub : Hub
    {
        public override async Task OnConnectedAsync()
        {
            var userId = Context.UserIdentifier;
            if (!string.IsNullOrEmpty(userId))
            {
                // Assign admins and moderators to an "Admins" group for broadcasting
                if (Context.User?.IsInRole("Admin") == true || Context.User?.IsInRole("Moderator") == true)
                {
                    await Groups.AddToGroupAsync(Context.ConnectionId, "Admins");
                }
            }
            await base.OnConnectedAsync();
        }
    }
}
