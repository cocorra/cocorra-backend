using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using Cocorra.DAL.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Cocorra.API.Hubs
{
    [Authorize]
    public class SupportHub : Hub
    {
        public const string AdminsGroup = "Admins";
        public const string SupportAdminsGroup = "SupportAdmins";

        private static readonly ConcurrentDictionary<string, (Guid UserId, HubCallerContext Context)> _staffConnections = new();

        public override async Task OnConnectedAsync()
        {
            var userIdRaw = Context.UserIdentifier ?? Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (!string.IsNullOrEmpty(userIdRaw))
            {
                var isAdmin = Context.User?.IsInRole(AppRoles.Admin) == true;
                var isModerator = Context.User?.IsInRole(AppRoles.Moderator) == true;

                if (isAdmin || isModerator)
                {
                    if (Guid.TryParse(userIdRaw, out var userId))
                    {
                        _staffConnections[Context.ConnectionId] = (userId, Context);
                    }

                    await Groups.AddToGroupAsync(Context.ConnectionId, AdminsGroup);

                    if (isAdmin)
                    {
                        await Groups.AddToGroupAsync(Context.ConnectionId, SupportAdminsGroup);
                    }
                }
            }

            await base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            _staffConnections.TryRemove(Context.ConnectionId, out _);
            await base.OnDisconnectedAsync(exception);
        }

        internal static IReadOnlyList<(string ConnectionId, HubCallerContext Context)> TakeStaffConnections(Guid userId)
        {
            var removed = new List<(string ConnectionId, HubCallerContext Context)>();
            foreach (var entry in _staffConnections)
            {
                if (entry.Value.UserId == userId && _staffConnections.TryRemove(entry.Key, out var tracked))
                {
                    removed.Add((entry.Key, tracked.Context));
                }
            }

            return removed;
        }
    }
}
