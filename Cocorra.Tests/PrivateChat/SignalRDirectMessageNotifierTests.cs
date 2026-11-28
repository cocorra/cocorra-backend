using System;
using System.Threading;
using System.Threading.Tasks;
using Cocorra.API.Hubs;
using Cocorra.API.Services;
using Cocorra.DAL.DTOS.ChatDto;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Cocorra.Tests.PrivateChat;

public class SignalRDirectMessageNotifierTests
{
    private readonly Mock<IHubContext<ChatHub>> _chatHubMock = new();
    private readonly Mock<IHubClients> _chatClientsMock = new();
    private readonly Mock<IClientProxy> _chatReceiverProxyMock = new();
    private readonly Mock<IClientProxy> _chatSenderProxyMock = new();

    private readonly Mock<IHubContext<RoomHub>> _roomHubMock = new();
    private readonly Mock<IHubClients> _roomClientsMock = new();
    private readonly Mock<IClientProxy> _roomReceiverProxyMock = new();
    private readonly Mock<IClientProxy> _roomSenderProxyMock = new();

    private readonly Guid _senderId = Guid.NewGuid();
    private readonly Guid _receiverId = Guid.NewGuid();
    private readonly SignalRDirectMessageNotifier _notifier;

    public SignalRDirectMessageNotifierTests()
    {
        _chatHubMock.Setup(h => h.Clients).Returns(_chatClientsMock.Object);
        _chatClientsMock.Setup(c => c.User(_receiverId.ToString())).Returns(_chatReceiverProxyMock.Object);
        _chatClientsMock.Setup(c => c.User(_senderId.ToString())).Returns(_chatSenderProxyMock.Object);

        _roomHubMock.Setup(h => h.Clients).Returns(_roomClientsMock.Object);
        _roomClientsMock.Setup(c => c.User(_receiverId.ToString())).Returns(_roomReceiverProxyMock.Object);
        _roomClientsMock.Setup(c => c.User(_senderId.ToString())).Returns(_roomSenderProxyMock.Object);

        _notifier = new SignalRDirectMessageNotifier(
            _chatHubMock.Object,
            _roomHubMock.Object,
            NullLogger<SignalRDirectMessageNotifier>.Instance);
    }

    private MessageDto CreateMessage() => new()
    {
        Id = Guid.NewGuid(),
        SenderId = _senderId,
        ReceiverId = _receiverId,
        Content = "Hello from room",
        CreatedAt = new DateTime(2026, 11, 28, 12, 0, 0, DateTimeKind.Utc),
        OriginRoomId = Guid.NewGuid()
    };

    [Fact]
    public async Task DeliverAsync_SendsReceiveAndSentEventsOnBothChatHubAndRoomHubToCorrectUsers()
    {
        var message = CreateMessage();

        await _notifier.DeliverAsync(message);

        _chatReceiverProxyMock.Verify(
            p => p.SendCoreAsync(
                SignalRDirectMessageNotifier.ChatReceive,
                It.Is<object?[]>(a => a.Length == 1 && ReferenceEquals(a[0], message)),
                It.IsAny<CancellationToken>()),
            Times.Once);
        _chatReceiverProxyMock.Verify(
            p => p.SendCoreAsync(SignalRDirectMessageNotifier.ChatSent, It.IsAny<object?[]>(), It.IsAny<CancellationToken>()),
            Times.Never);

        _chatSenderProxyMock.Verify(
            p => p.SendCoreAsync(
                SignalRDirectMessageNotifier.ChatSent,
                It.Is<object?[]>(a => a.Length == 1 && ReferenceEquals(a[0], message)),
                It.IsAny<CancellationToken>()),
            Times.Once);
        _chatSenderProxyMock.Verify(
            p => p.SendCoreAsync(SignalRDirectMessageNotifier.ChatReceive, It.IsAny<object?[]>(), It.IsAny<CancellationToken>()),
            Times.Never);

        _roomReceiverProxyMock.Verify(
            p => p.SendCoreAsync(
                SignalRDirectMessageNotifier.RoomReceive,
                It.Is<object?[]>(a => a.Length == 1 && ReferenceEquals(a[0], message)),
                It.IsAny<CancellationToken>()),
            Times.Once);
        _roomReceiverProxyMock.Verify(
            p => p.SendCoreAsync(SignalRDirectMessageNotifier.RoomSent, It.IsAny<object?[]>(), It.IsAny<CancellationToken>()),
            Times.Never);

        _roomSenderProxyMock.Verify(
            p => p.SendCoreAsync(
                SignalRDirectMessageNotifier.RoomSent,
                It.Is<object?[]>(a => a.Length == 1 && ReferenceEquals(a[0], message)),
                It.IsAny<CancellationToken>()),
            Times.Once);
        _roomSenderProxyMock.Verify(
            p => p.SendCoreAsync(SignalRDirectMessageNotifier.RoomReceive, It.IsAny<object?[]>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task DeliverAsync_WhenChatHubThrows_SwallowsFailureAndStillDeliversToRoomHub()
    {
        var message = CreateMessage();
        _chatReceiverProxyMock
            .Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("ChatHub transport failure"));
        _chatSenderProxyMock
            .Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("ChatHub transport failure"));

        var exception = await Record.ExceptionAsync(() => _notifier.DeliverAsync(message));

        Assert.Null(exception);
        _roomReceiverProxyMock.Verify(
            p => p.SendCoreAsync(
                SignalRDirectMessageNotifier.RoomReceive,
                It.Is<object?[]>(a => a.Length == 1 && ReferenceEquals(a[0], message)),
                It.IsAny<CancellationToken>()),
            Times.Once);
        _roomSenderProxyMock.Verify(
            p => p.SendCoreAsync(
                SignalRDirectMessageNotifier.RoomSent,
                It.Is<object?[]>(a => a.Length == 1 && ReferenceEquals(a[0], message)),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
