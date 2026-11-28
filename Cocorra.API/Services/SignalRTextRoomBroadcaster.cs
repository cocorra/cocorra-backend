using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Cocorra.API.Hubs;
using Cocorra.BLL.Services.TextRooms;
using Cocorra.DAL.DTOS.TextRoomDto;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace Cocorra.API.Services
{
    public sealed class SignalRTextRoomBroadcaster : ITextRoomBroadcaster
    {
        public const string MessageReceived = "TextRoomMessageReceived";
        public const string MessageDeleted = "TextRoomMessageDeleted";
        public const string ConfigUpdated = "TextRoomConfigUpdated";
        public const string PresenceUpdated = "TextRoomPresenceUpdated";
        public const string RoomClosed = "TextRoomClosed";
        public const string HistoryCleared = "TextRoomHistoryCleared";
        public const string UserMuted = "TextRoomUserMuted";
        public const string UserKicked = "TextRoomKicked";
        public const string RoomListChanged = "TextRoomListChanged";

        private readonly IHubContext<TextRoomHub> _hub;
        private readonly ILogger<SignalRTextRoomBroadcaster> _logger;

        public SignalRTextRoomBroadcaster(
            IHubContext<TextRoomHub> hub,
            ILogger<SignalRTextRoomBroadcaster> logger)
        {
            _hub = hub;
            _logger = logger;
        }

        public Task MessageReceivedAsync(TextRoomMessageDto message) =>
            SendSafeAsync(_hub.Clients.Group(TextRoomHub.RoomGroup(message.RoomId)), MessageReceived, message);

        public Task MessageDeletedAsync(Guid roomId, long messageId) =>
            SendSafeAsync(_hub.Clients.Group(TextRoomHub.RoomGroup(roomId)), MessageDeleted, new { roomId, messageId });

        public Task ConfigUpdatedAsync(TextRoomClientConfigDto config) =>
            SendSafeAsync(_hub.Clients.Group(TextRoomHub.RoomGroup(config.RoomId)), ConfigUpdated, config);

        public async Task PresenceChangedAsync(Guid roomId, int onlineCount, int totalParticipants, bool visibleToUsers)
        {
            var payload = new { roomId, onlineCount, totalParticipants };
            await SendSafeAsync(_hub.Clients.Group(TextRoomHub.StaffMonitorGroup), PresenceUpdated, payload);

            if (visibleToUsers)
                await SendSafeAsync(_hub.Clients.Group(TextRoomHub.RoomGroup(roomId)), PresenceUpdated, payload);
        }

        public async Task RoomClosedAsync(Guid roomId, string reason)
        {
            var payload = new { roomId, reason };
            await SendSafeAsync(_hub.Clients.Group(TextRoomHub.RoomGroup(roomId)), RoomClosed, payload);
            await SendSafeAsync(_hub.Clients.Group(TextRoomHub.StaffMonitorGroup), RoomClosed, payload);
        }

        public Task HistoryClearedAsync(Guid roomId) =>
            SendSafeAsync(_hub.Clients.Group(TextRoomHub.RoomGroup(roomId)), HistoryCleared, new { roomId });

        public Task UserMutedAsync(Guid roomId, Guid userId, DateTime mutedUntil, string reason) =>
            SendSafeAsync(_hub.Clients.User(userId.ToString()), UserMuted, new { roomId, mutedUntil, reason });

        public async Task UserKickedAsync(
            Guid roomId, Guid userId, string reason, IReadOnlyList<string> connectionIds)
        {
            await SendSafeAsync(_hub.Clients.User(userId.ToString()), UserKicked, new { roomId, reason });

            var groupName = TextRoomHub.RoomGroup(roomId);
            foreach (var connectionId in connectionIds)
            {
                try
                {
                    await _hub.Groups.RemoveFromGroupAsync(connectionId, groupName);
                }
                catch (Exception ex)
                {
                    // Transport errors on group cleanup must not fail the already-persisted kick.
                    _logger.LogWarning(
                        ex,
                        "Failed to remove kicked connection {ConnectionId} from group {GroupName}.",
                        connectionId,
                        groupName);
                }
            }
        }

        public Task RoomListChangedAsync() =>
            SendSafeAsync(_hub.Clients.All, RoomListChanged, new { });

        private async Task SendSafeAsync(IClientProxy target, string eventName, object payload)
        {
            try
            {
                await target.SendAsync(eventName, payload);
            }
            catch (Exception ex)
            {
                // Real-time fan-out is best-effort after persistence; transport failure must not abort callers.
                _logger.LogWarning(ex, "Text room SignalR broadcast {EventName} failed.", eventName);
            }
        }
    }
}
