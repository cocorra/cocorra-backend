using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Cocorra.BLL.Services.ChatService;
using Cocorra.BLL.Services.EventTracking;
using Cocorra.BLL.Services.NotificationService;
using Cocorra.DAL.DTOS.ChatDto;
using Cocorra.DAL.Models;
using Cocorra.DAL.Repository.MessageRepository;
using Cocorra.DAL.Repository.UserBlockRepository;
using Cocorra.Tests.Helpers;
using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using static Cocorra.BLL.Services.Events.ChatEvents;

namespace Cocorra.Tests;

public class ChatServiceTests
{
    private readonly Mock<IUserBlockRepository> _blockRepoMock = new();
    private readonly Mock<IMessageRepository> _messageRepoMock = new();
    private readonly Mock<IPushNotificationService> _pushServiceMock = new();
    private readonly Mock<UserManager<ApplicationUser>> _userManagerMock = TestIdentityHelper.CreateMockUserManager();
    private readonly Mock<IMediator> _mediatorMock = new();
    private readonly Mock<IEventTracker> _eventTrackerMock = new();
    private readonly ChatService _service;

    public ChatServiceTests()
    {
        _service = new ChatService(
            _blockRepoMock.Object,
            _messageRepoMock.Object,
            _pushServiceMock.Object,
            _userManagerMock.Object,
            _mediatorMock.Object,
            _eventTrackerMock.Object,
            NullLogger<ChatService>.Instance
        );
    }

    [Fact]
    public async Task GetChatHistoryAsync_UsersBlocked_ReturnsBadRequest()
    {
        var currentUserId = Guid.NewGuid();
        var friendId = Guid.NewGuid();
        _blockRepoMock.Setup(b => b.IsBlockedAsync(currentUserId, friendId)).ReturnsAsync(true);

        var result = await _service.GetChatHistoryAsync(currentUserId, friendId, pageNumber: 1, pageSize: 20);

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        Assert.Contains("block", result.Message);
    }

    [Fact]
    public async Task GetChatHistoryAsync_NotBlocked_ReturnsMessages()
    {
        var currentUserId = Guid.NewGuid();
        var friendId = Guid.NewGuid();
        _blockRepoMock.Setup(b => b.IsBlockedAsync(currentUserId, friendId)).ReturnsAsync(false);

        var messages = new List<Message>
        {
            new()
            {
                Id = Guid.NewGuid(),
                SenderId = currentUserId,
                ReceiverId = friendId,
                Content = "Hey!",
                CreatedAt = DateTime.UtcNow,
                IsRead = true
            }
        };
        _messageRepoMock.Setup(m => m.GetChatHistoryAsync(currentUserId, friendId, 1, 20))
            .ReturnsAsync(messages);

        var result = await _service.GetChatHistoryAsync(currentUserId, friendId, pageNumber: 1, pageSize: 20);

        Assert.True(result.Succeeded);
        var list = result.Data!.ToList();
        Assert.Single(list);
        Assert.Equal("Hey!", list[0].Content);
    }

    [Fact]
    public async Task SaveMessageAsync_BlockedUser_ReturnsBadRequest()
    {
        var senderId = Guid.NewGuid();
        var receiverId = Guid.NewGuid();
        _blockRepoMock.Setup(b => b.IsBlockedAsync(senderId, receiverId)).ReturnsAsync(true);

        var result = await _service.SaveMessageAsync(senderId, receiverId, "Hello");

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        Assert.Contains("block", result.Message);
        _messageRepoMock.Verify(m => m.AddAsync(It.IsAny<Message>()), Times.Never);
    }

    [Fact]
    public async Task SaveMessageAsync_ValidMessage_PersistsTracksAndDispatchesPush()
    {
        var senderId = Guid.NewGuid();
        var receiverId = Guid.NewGuid();
        var content = "Can you hear me?";
        var sender = new ApplicationUser { Id = senderId, FirstName = "Bob", LastName = "Dylan" };
        var receiver = new ApplicationUser { Id = receiverId, FcmToken = "receiver_fcm" };

        _blockRepoMock.Setup(b => b.IsBlockedAsync(senderId, receiverId)).ReturnsAsync(false);
        _userManagerMock.Setup(m => m.FindByIdAsync(senderId.ToString())).ReturnsAsync(sender);
        _userManagerMock.Setup(m => m.FindByIdAsync(receiverId.ToString())).ReturnsAsync(receiver);
        _messageRepoMock.Setup(m => m.AddAsync(It.IsAny<Message>()))
            .ReturnsAsync((Message m) => m);

        var result = await _service.SaveMessageAsync(senderId, receiverId, content);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Data);
        Assert.Equal(content, result.Data.Content);

        _messageRepoMock.Verify(m => m.AddAsync(It.Is<Message>(msg =>
            msg.SenderId == senderId &&
            msg.ReceiverId == receiverId &&
            msg.Content == content)), Times.Once);

        _eventTrackerMock.Verify(t => t.Track(
            EventTypes.MessageSent,
            senderId,
            It.IsAny<object>()), Times.Once);

        _pushServiceMock.Verify(p => p.SendPushNotificationAsync(
            "receiver_fcm",
            "Bob Dylan",
            content,
            It.Is<Dictionary<string, string>>(d => d["type"] == "chat" && d["senderId"] == senderId.ToString())
        ), Times.Once);
    }

    [Fact]
    public async Task MarkMessagesAsReadAsync_CallsRepoAndReturnsSuccess()
    {
        var currentUserId = Guid.NewGuid();
        var friendId = Guid.NewGuid();

        var result = await _service.MarkMessagesAsReadAsync(currentUserId, friendId);

        Assert.True(result.Succeeded);
        Assert.Equal("Messages marked as read.", result.Data);
        _messageRepoMock.Verify(m => m.MarkMessagesAsReadAsync(friendId, currentUserId), Times.Once);
        _mediatorMock.Verify(m => m.Publish(It.IsAny<MessagesReadEvent>(), default), Times.Once);
    }

    [Fact]
    public async Task GetChatFriendsListAsync_CallsRepoAndReturnsList()
    {
        var currentUserId = Guid.NewGuid();
        var expectedFriends = new List<ChatFriendDto>
        {
            new()
            {
                FriendId = Guid.NewGuid(),
                FullName = "Charlie",
                LastMessage = "See you tomorrow",
                UnreadCount = 3
            }
        };
        _messageRepoMock.Setup(m => m.GetRecentChatSummariesAsync(currentUserId))
            .ReturnsAsync(expectedFriends);

        var result = await _service.GetChatFriendsListAsync(currentUserId, 1, 20);

        Assert.True(result.Succeeded);
        Assert.Equal(expectedFriends, result.Data);
    }
}
