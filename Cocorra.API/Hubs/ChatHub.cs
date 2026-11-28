using Cocorra.BLL.Services.ChatService;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using System.Security.Claims;

namespace Cocorra.API.Hubs
{
    [Authorize]
    public class ChatHub : Hub
    {
        private readonly IChatService _chatService;
        private readonly IDirectMessageNotifier _dmNotifier;

        public ChatHub(IChatService chatService, IDirectMessageNotifier dmNotifier)
        {
            _chatService = chatService;
            _dmNotifier = dmNotifier;
        }

        public async Task SendMessage(string receiverIdString, string content)
        {
            try
            {
                var senderIdString = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;

                if (!Guid.TryParse(senderIdString, out Guid senderId) ||
                    !Guid.TryParse(receiverIdString, out Guid receiverId))
                {
                    await Clients.Caller.SendAsync("SendMessageError", new { Error = "Invalid User IDs." });
                    return;
                }

                if (string.IsNullOrWhiteSpace(content))
                {
                    await Clients.Caller.SendAsync("SendMessageError", new { Error = "Message cannot be empty." });
                    return;
                }

                var result = await _chatService.SaveMessageAsync(senderId, receiverId, content);

                if (!result.Succeeded)
                {
                    await Clients.Caller.SendAsync("SendMessageError", new { Error = result.Message });
                    return;
                }

                await _dmNotifier.DeliverAsync(result.Data!);
            }
            catch (Exception)
            {
                await Clients.Caller.SendAsync("SendMessageError", new { Error = "An unexpected error occurred. Please try again." });
            }
        }
    }
}