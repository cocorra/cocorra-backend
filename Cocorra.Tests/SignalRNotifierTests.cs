using System;
using System.Text.Json;
using System.Threading.Tasks;
using Cocorra.API.Hubs;
using Cocorra.API.Services;
using Microsoft.AspNetCore.SignalR;
using Moq;
using Xunit;

namespace Cocorra.Tests;

public class SignalRNotifierTests
{
    private readonly Mock<IHubContext<RoomHub>> _roomHubContextMock = new();
    private readonly Mock<IHubClients> _clientsMock = new();
    private readonly Mock<IClientProxy> _userProxyMock = new();
    private readonly Mock<IClientProxy> _groupProxyMock = new();
    private readonly Mock<IStaffConnectionRevoker> _revokerMock = new();
    private readonly SignalRNotifier _notifier;

    public SignalRNotifierTests()
    {
        _roomHubContextMock.Setup(h => h.Clients).Returns(_clientsMock.Object);
        _clientsMock.Setup(c => c.User(It.IsAny<string>())).Returns(_userProxyMock.Object);
        _clientsMock.Setup(c => c.Group(It.IsAny<string>())).Returns(_groupProxyMock.Object);

        _notifier = new SignalRNotifier(_roomHubContextMock.Object, _revokerMock.Object);
    }

    [Fact]
    public async Task ForceLogoutAsync_SendsForceLogoutToRoomHubUserAndRevokesStaffConnections()
    {
        var userId = Guid.NewGuid();
        object? capturedPayload = null;

        _userProxyMock
            .Setup(p => p.SendCoreAsync("ForceLogout", It.IsAny<object?[]>(), default))
            .Callback<string, object?[], System.Threading.CancellationToken>((_, args, _) => capturedPayload = args[0])
            .Returns(Task.CompletedTask);

        await _notifier.ForceLogoutAsync(userId, "Account banned");

        _clientsMock.Verify(c => c.User(userId.ToString()), Times.Once);
        _userProxyMock.Verify(p => p.SendCoreAsync("ForceLogout", It.Is<object?[]>(a => a.Length == 1), default), Times.Once);
        _revokerMock.Verify(r => r.RevokeAsync(userId), Times.Once);

        Assert.NotNull(capturedPayload);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(capturedPayload));
        Assert.Equal("Account banned", doc.RootElement.GetProperty("Reason").GetString());
        Assert.True(doc.RootElement.TryGetProperty("Timestamp", out _));
    }

    [Fact]
    public async Task ForceLogoutAsync_WhenRoomHubSendThrows_StillRevokesStaffConnections()
    {
        var userId = Guid.NewGuid();
        _userProxyMock
            .Setup(p => p.SendCoreAsync("ForceLogout", It.IsAny<object?[]>(), default))
            .ThrowsAsync(new InvalidOperationException("SignalR backplane unavailable"));

        var exception = await Record.ExceptionAsync(() => _notifier.ForceLogoutAsync(userId, "Account suspended"));

        Assert.Null(exception);
        _revokerMock.Verify(r => r.RevokeAsync(userId), Times.Once);
    }

    [Fact]
    public async Task RoomEndedAsync_BroadcastsToRoomGroupWithoutRevoking_AndSwallowsSendFailure()
    {
        var roomId = Guid.NewGuid();
        _groupProxyMock
            .Setup(p => p.SendCoreAsync("RoomEnded", It.IsAny<object?[]>(), default))
            .ThrowsAsync(new InvalidOperationException("Broadcast failed"));

        var exception = await Record.ExceptionAsync(() => _notifier.RoomEndedAsync(roomId, "Ended"));

        Assert.Null(exception);
        _clientsMock.Verify(c => c.Group(roomId.ToString()), Times.Once);
        _groupProxyMock.Verify(p => p.SendCoreAsync("RoomEnded", It.Is<object?[]>(a => a.Length == 1), default), Times.Once);
        _revokerMock.Verify(r => r.RevokeAsync(It.IsAny<Guid>()), Times.Never);
    }
}
