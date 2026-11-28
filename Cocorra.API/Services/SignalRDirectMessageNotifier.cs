using System.Threading.Tasks;
using Cocorra.API.Hubs;
using Cocorra.BLL.Services.ChatService;
using Cocorra.DAL.DTOS.ChatDto;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace Cocorra.API.Services
{
    /// <summary>
    /// Sends each saved message under both hubs' event names, to every connection of both users,
    /// so the conversation stays live whether a user is on the chat screen, still in the room, or
    /// on another device. Clients receiving both events should de-duplicate on MessageDto.Id.
    /// </summary>
    public class SignalRDirectMessageNotifier : IDirectMessageNotifier
    {
        public const string ChatReceive = "ReceiveMessage";
        public const string ChatSent = "MessageSent";
        public const string RoomReceive = "ReceivePrivateMessage";
        public const string RoomSent = "PrivateMessageSent";

        private readonly IHubContext<ChatHub> _chatHub;
        private readonly IHubContext<RoomHub> _roomHub;
        private readonly ILogger<SignalRDirectMessageNotifier> _logger;

        public SignalRDirectMessageNotifier(
            IHubContext<ChatHub> chatHub,
            IHubContext<RoomHub> roomHub,
            ILogger<SignalRDirectMessageNotifier> logger)
        {
            _chatHub = chatHub;
            _roomHub = roomHub;
            _logger = logger;
        }

        public async Task DeliverAsync(MessageDto message)
        {
            var receiver = message.ReceiverId.ToString();
            var sender = message.SenderId.ToString();

            await SendSafeAsync(_chatHub.Clients.User(receiver), ChatReceive, message);
            await SendSafeAsync(_chatHub.Clients.User(sender), ChatSent, message);
            await SendSafeAsync(_roomHub.Clients.User(receiver), RoomReceive, message);
            await SendSafeAsync(_roomHub.Clients.User(sender), RoomSent, message);
        }

        // The message is already persisted: a failed push must not surface as a send error,
        // and one hub failing must not stop delivery on the other.
        private async Task SendSafeAsync(IClientProxy target, string method, MessageDto message)
        {
            try
            {
                await target.SendAsync(method, message);
            }
            catch (System.Exception ex)
            {
                _logger.LogWarning(ex, "Direct message {MessageId} not delivered as {Method}", message.Id, method);
            }
        }
    }
}
