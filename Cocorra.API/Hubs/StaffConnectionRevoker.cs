using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cocorra.API.Hubs
{
    /// <summary>
    /// Cuts a user's live SignalR privileges the moment their roles or status change, instead of
    /// waiting for the connection to drop: removes them from the SupportHub staff group and
    /// claimed-chat groups and aborts their privileged hub connections.
    /// </summary>
    public interface IStaffConnectionRevoker
    {
        Task RevokeAsync(Guid userId);
    }

    public sealed class StaffConnectionRevoker : IStaffConnectionRevoker
    {
        private readonly IHubContext<SupportHub> _supportHub;
        private readonly ILogger<StaffConnectionRevoker> _logger;

        public StaffConnectionRevoker(
            IHubContext<SupportHub> supportHub,
            ILogger<StaffConnectionRevoker>? logger = null)
        {
            _supportHub = supportHub;
            _logger = logger ?? NullLogger<StaffConnectionRevoker>.Instance;
        }

        private static readonly string[] StaffGroups =
        {
            SupportHub.AdminsGroup,
            SupportHub.SupportAdminsGroup
        };

        public async Task RevokeAsync(Guid userId)
        {
            var connections = SupportHub.TakeStaffConnections(userId);
            if (connections.Count == 0)
            {
                return;
            }

            foreach (var (connectionId, context) in connections)
            {
                foreach (var group in StaffGroups)
                {
                    try
                    {
                        await _supportHub.Groups.RemoveFromGroupAsync(connectionId, group);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "Failed to remove revoked connection {ConnectionId} for user {UserId} from SupportHub group {Group}.",
                            connectionId, userId, group);
                    }
                }

                try
                {
                    context.Abort();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Failed to abort revoked SupportHub connection {ConnectionId} for user {UserId}.",
                        connectionId, userId);
                }
            }
        }
    }
}
