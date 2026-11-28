using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using Cocorra.BLL.Services.ChatService;
using Cocorra.BLL.Services.EventTracking;
using Cocorra.BLL.Services.NotificationService;
using Cocorra.DAL.Models;
using Cocorra.DAL.Repository.MessageRepository;
using Cocorra.DAL.Repository.UserBlockRepository;
using Cocorra.Tests.Helpers;
using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Cocorra.Tests.PrivateChat;

public class ChatServiceOriginTests
{
    private readonly Mock<IUserBlockRepository> _blockRepoMock = new();
    private readonly Mock<IMessageRepository> _messageRepoMock = new();
    private readonly Mock<IPushNotificationService> _pushServiceMock = new();
    private readonly Mock<UserManager<ApplicationUser>> _userManagerMock = TestIdentityHelper.CreateMockUserManager();
    private readonly Mock<IMediator> _mediatorMock = new();
    private readonly Mock<IEventTracker> _eventTrackerMock = new();
    private readonly ChatService _service;

    public ChatServiceOriginTests()
    {
        _blockRepoMock
            .Setup(b => b.IsBlockedAsync(It.IsAny<Guid>(), It.IsAny<Guid>()))
            .ReturnsAsync(false);
        _messageRepoMock
            .Setup(m => m.AddAsync(It.IsAny<Message>()))
            .ReturnsAsync((Message m) => m);

        _service = new ChatService(
            _blockRepoMock.Object,
            _messageRepoMock.Object,
            _pushServiceMock.Object,
            _userManagerMock.Object,
            _mediatorMock.Object,
            _eventTrackerMock.Object,
            NullLogger<ChatService>.Instance);
    }

    [Fact]
    public async Task SaveMessageAsync_WithAudioRoomOrigin_PersistsAndEchoesOriginRoomId()
    {
        var senderId = Guid.NewGuid();
        var receiverId = Guid.NewGuid();
        var roomId = Guid.NewGuid();
        Message? persisted = null;
        _messageRepoMock
            .Setup(m => m.AddAsync(It.IsAny<Message>()))
            .Callback<Message>(m => persisted = m)
            .ReturnsAsync((Message m) => m);

        var result = await _service.SaveMessageAsync(senderId, receiverId, "Hi from voice room", new MessageOrigin(RoomId: roomId));

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Data);
        Assert.Equal(roomId, result.Data!.OriginRoomId);
        Assert.Null(result.Data.OriginTextRoomId);
        Assert.NotNull(persisted);
        Assert.Equal(roomId, persisted!.OriginRoomId);
        Assert.Null(persisted.OriginTextRoomId);
    }

    [Fact]
    public async Task SaveMessageAsync_WithTextRoomOrigin_PersistsAndEchoesOriginTextRoomId()
    {
        var senderId = Guid.NewGuid();
        var receiverId = Guid.NewGuid();
        var textRoomId = Guid.NewGuid();
        Message? persisted = null;
        _messageRepoMock
            .Setup(m => m.AddAsync(It.IsAny<Message>()))
            .Callback<Message>(m => persisted = m)
            .ReturnsAsync((Message m) => m);

        var result = await _service.SaveMessageAsync(senderId, receiverId, "Hi from text room", new MessageOrigin(TextRoomId: textRoomId));

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Data);
        Assert.Null(result.Data!.OriginRoomId);
        Assert.Equal(textRoomId, result.Data.OriginTextRoomId);
        Assert.NotNull(persisted);
        Assert.Null(persisted!.OriginRoomId);
        Assert.Equal(textRoomId, persisted.OriginTextRoomId);
    }

    [Fact]
    public async Task SaveMessageAsync_ThreeArgOverload_StoresNullOrigin()
    {
        var senderId = Guid.NewGuid();
        var receiverId = Guid.NewGuid();
        Message? persisted = null;
        _messageRepoMock
            .Setup(m => m.AddAsync(It.IsAny<Message>()))
            .Callback<Message>(m => persisted = m)
            .ReturnsAsync((Message m) => m);

        var result = await _service.SaveMessageAsync(senderId, receiverId, "Direct chat message");

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Data);
        Assert.Null(result.Data!.OriginRoomId);
        Assert.Null(result.Data.OriginTextRoomId);
        Assert.NotNull(persisted);
        Assert.Null(persisted!.OriginRoomId);
        Assert.Null(persisted.OriginTextRoomId);
    }

    [Fact]
    public async Task SaveMessageAsync_ExactMaxLength1000Chars_SucceedsAndPersists()
    {
        var senderId = Guid.NewGuid();
        var receiverId = Guid.NewGuid();
        var content = new string('x', 1000);
        var origin = new MessageOrigin(TextRoomId: Guid.NewGuid());

        var result = await _service.SaveMessageAsync(senderId, receiverId, $"  {content}  ", origin);

        Assert.True(result.Succeeded);
        Assert.Equal(content, result.Data!.Content);
        _messageRepoMock.Verify(m => m.AddAsync(It.Is<Message>(msg => msg.Content.Length == 1000)), Times.Once);
    }

    [Fact]
    public async Task SaveMessageAsync_Exceeding1000Chars_ReturnsBadRequestWithoutPersisting()
    {
        var senderId = Guid.NewGuid();
        var receiverId = Guid.NewGuid();
        var content = new string('x', 1001);
        var origin = new MessageOrigin(RoomId: Guid.NewGuid());

        var result = await _service.SaveMessageAsync(senderId, receiverId, content, origin);

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        _messageRepoMock.Verify(m => m.AddAsync(It.IsAny<Message>()), Times.Never);
        _eventTrackerMock.Verify(t => t.Track(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<object?>()), Times.Never);
        _pushServiceMock.Verify(
            p => p.SendPushNotificationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Dictionary<string, string>>()),
            Times.Never);
    }

    [Fact]
    public async Task SaveMessageAsync_BlockedPairWithOrigin_ReturnsBadRequestWithoutPersisting()
    {
        var senderId = Guid.NewGuid();
        var receiverId = Guid.NewGuid();
        _blockRepoMock.Setup(b => b.IsBlockedAsync(senderId, receiverId)).ReturnsAsync(true);

        var result = await _service.SaveMessageAsync(
            senderId,
            receiverId,
            "Blocked attempt",
            new MessageOrigin(RoomId: Guid.NewGuid()));

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        _messageRepoMock.Verify(m => m.AddAsync(It.IsAny<Message>()), Times.Never);
    }
}
