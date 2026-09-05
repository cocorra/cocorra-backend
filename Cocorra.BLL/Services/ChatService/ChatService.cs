using Cocorra.BLL.Services.NotificationService;
using Cocorra.DAL.DTOS.ChatDto;
using Cocorra.DAL.Models;
using Cocorra.DAL.Repository.MessageRepository;
using Cocorra.DAL.Repository.UserBlockRepository;
using Cocorra.BLL.Base;
using MediatR;
using Microsoft.AspNetCore.Identity;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using static Cocorra.BLL.Services.Events.ChatEvents;
using Cocorra.BLL.Services.EventTracking;

using Microsoft.Extensions.Logging;

namespace Cocorra.BLL.Services.ChatService
{
    public class ChatService : ResponseHandler, IChatService
    {
        private readonly IUserBlockRepository _blockRepo;
        private readonly IMessageRepository _messageRepo;
        private readonly IPushNotificationService _pushService;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IMediator _mediator; 
        private readonly IEventTracker _eventTracker;
        private readonly ILogger<ChatService> _logger;

        public ChatService(
            IUserBlockRepository blockRepo,
            IMessageRepository messageRepo,
            IPushNotificationService pushService,
            UserManager<ApplicationUser> userManager,
            IMediator mediator,
            IEventTracker eventTracker,
            ILogger<ChatService> logger)
        {
            _blockRepo = blockRepo;
            _messageRepo = messageRepo;
            _pushService = pushService;
            _userManager = userManager;
            _mediator = mediator;
            _eventTracker = eventTracker;
            _logger = logger;
        }

        public async Task<Response<IEnumerable<MessageDto>>> GetChatHistoryAsync(Guid currentUserId, Guid friendId, int pageNumber, int pageSize)
        {
            if (await _blockRepo.IsBlockedAsync(currentUserId, friendId))
                return BadRequest<IEnumerable<MessageDto>>("You cannot view chat history due to a block.");

            var messages = await _messageRepo.GetChatHistoryAsync(currentUserId, friendId, pageNumber, pageSize);

            var dtoList = messages.Select(m => new MessageDto
            {
                Id = m.Id,
                SenderId = m.SenderId,
                ReceiverId = m.ReceiverId,
                Content = m.Content,
                IsRead = m.IsRead,
                CreatedAt = m.CreatedAt
            }).ToList();

            return Success<IEnumerable<MessageDto>>(dtoList);
        }

        public async Task<Response<MessageDto>> SaveMessageAsync(Guid senderId, Guid receiverId, string content)
        {
            if (string.IsNullOrWhiteSpace(content))
                return BadRequest<MessageDto>("Message cannot be empty.");

            content = content.Trim();

            if (senderId == receiverId)
                return BadRequest<MessageDto>("You cannot send messages to yourself.");

            if (await _blockRepo.IsBlockedAsync(senderId, receiverId))
                return BadRequest<MessageDto>("You cannot send a message due to a block.");

            // AN-030: checked BEFORE the insert, using the repository method that already
            // exists, so no new query shape is introduced for analytics alone.
            var isFirstMessageToRecipient =
                await _messageRepo.GetLastMessageAsync(senderId, receiverId) is null;

            var message = new Message
            {
                SenderId = senderId,
                ReceiverId = receiverId,
                Content = content,
                CreatedAt = DateTime.UtcNow,
                IsRead = false
            };

            await _messageRepo.AddAsync(message);

            // AN-030: isFirstMessageToRecipient distinguishes a conversation starting from an
            // existing one continuing. Without it, one very active pair and many new connections
            // produce the same message count, and the social loop cannot be told apart from
            // background chatter between two people who already know each other.
            _eventTracker.Track(EventTypes.MessageSent, senderId, new
            {
                receiverId,
                isFirstMessageToRecipient,
                contentLength = content?.Length ?? 0
            });

            var dto = new MessageDto
            {
                Id = message.Id,
                SenderId = message.SenderId,
                ReceiverId = message.ReceiverId,
                Content = message.Content,
                IsRead = message.IsRead,
                CreatedAt = message.CreatedAt
            };

            try
            {
                var sender = await _userManager.FindByIdAsync(senderId.ToString());
                string senderName = sender != null ? $"{sender.FirstName} {sender.LastName}" : "New Message";
                
                var receiver = await _userManager.FindByIdAsync(receiverId.ToString());
                if (!string.IsNullOrEmpty(receiver?.FcmToken))
                {
                    var data = new Dictionary<string, string> { { "type", "chat" }, { "senderId", senderId.ToString() } };
                    await _pushService.SendPushNotificationAsync(receiver.FcmToken, senderName, content, data);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "FCM push notification failed for chat message. SenderId: {SenderId}, ReceiverId: {ReceiverId}",
                    senderId, receiverId);
            }

            return Success(dto);
        }

        public async Task<Response<IEnumerable<ChatFriendDto>>> GetChatFriendsListAsync(Guid currentUserId, int pageNumber = 1, int pageSize = 20)
        {
            var dtoList = await _messageRepo.GetRecentChatSummariesAsync(currentUserId);
            
            var paginatedList = dtoList
                .Skip((pageNumber - 1) * pageSize)
                .Take(pageSize)
                .ToList();

            return Success<IEnumerable<ChatFriendDto>>(paginatedList);
        }

        public async Task<Response<string>> MarkMessagesAsReadAsync(Guid currentUserId, Guid friendId)
        {
            await _messageRepo.MarkMessagesAsReadAsync(senderId: friendId, receiverId: currentUserId);

            await _mediator.Publish(new MessagesReadEvent(currentUserId, friendId));

            return Success("Messages marked as read.");
        }
    }
}