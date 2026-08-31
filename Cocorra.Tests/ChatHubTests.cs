using System.Security.Claims;
using Cocorra.API.Hubs;
using Cocorra.BLL.Base;
using Cocorra.BLL.Services.ChatService;
using Cocorra.DAL.DTOS.ChatDto;
using Microsoft.AspNetCore.SignalR;
using Moq;
using Xunit;

namespace Cocorra.Tests;

public class ChatHubTests
{
    private readonly Mock<IChatService> _chatServiceMock = new();
    private readonly Mock<IHubCallerClients> _clientsMock = new();
    private readonly Mock<ISingleClientProxy> _callerMock = new();
    private readonly Mock<IClientProxy> _userProxyMock = new();
    private readonly Mock<HubCallerContext> _contextMock = new();

    public ChatHubTests()
    {
        _clientsMock.Setup(c => c.Caller).Returns(_callerMock.Object);
        _clientsMock.Setup(c => c.User(It.IsAny<string>())).Returns(_userProxyMock.Object);
    }

    private ChatHub CreateHub(Guid? senderId = null)
    {
        var hub = new ChatHub(_chatServiceMock.Object);

        var claims = new List<Claim>();
        if (senderId.HasValue)
        {
            claims.Add(new Claim(ClaimTypes.NameIdentifier, senderId.Value.ToString()));
        }

        var identity = new ClaimsIdentity(claims, "TestAuth");
        var principal = new ClaimsPrincipal(identity);

        _contextMock.Setup(c => c.User).Returns(principal);
        _contextMock.Setup(c => c.ConnectionId).Returns("test-conn-id");
        _contextMock.Setup(c => c.UserIdentifier).Returns(senderId?.ToString());

        hub.Context = _contextMock.Object;
        hub.Clients = _clientsMock.Object;

        return hub;
    }

    [Fact]
    public async Task SendMessage_InvalidSenderId_SendsErrorToCaller()
    {
        // Arrange (No valid sender Guid claim)
        var hub = CreateHub(null);

        // Act
        await hub.SendMessage(Guid.NewGuid().ToString(), "Hello");

        // Assert
        _callerMock.Verify(c => c.SendCoreAsync("SendMessageError", It.IsAny<object[]>(), default), Times.Once);
    }

    [Fact]
    public async Task SendMessage_InvalidReceiverId_SendsErrorToCaller()
    {
        var senderId = Guid.NewGuid();
        var hub = CreateHub(senderId);

        await hub.SendMessage("invalid-guid", "Hello");

        _callerMock.Verify(c => c.SendCoreAsync("SendMessageError", It.IsAny<object[]>(), default), Times.Once);
    }

    [Fact]
    public async Task SendMessage_EmptyContent_SendsErrorToCaller()
    {
        var senderId = Guid.NewGuid();
        var receiverId = Guid.NewGuid();
        var hub = CreateHub(senderId);

        await hub.SendMessage(receiverId.ToString(), "   ");

        _callerMock.Verify(c => c.SendCoreAsync("SendMessageError", It.IsAny<object[]>(), default), Times.Once);
    }

    [Fact]
    public async Task SendMessage_ServiceFailure_SendsErrorToCaller()
    {
        var senderId = Guid.NewGuid();
        var receiverId = Guid.NewGuid();
        var hub = CreateHub(senderId);

        _chatServiceMock.Setup(s => s.SaveMessageAsync(senderId, receiverId, "Hello"))
            .ReturnsAsync(new Response<MessageDto>
            {
                Succeeded = false,
                StatusCode = System.Net.HttpStatusCode.BadRequest,
                Message = "User has blocked you."
            });

        await hub.SendMessage(receiverId.ToString(), "Hello");

        _callerMock.Verify(c => c.SendCoreAsync("SendMessageError", It.IsAny<object[]>(), default), Times.Once);
    }

    [Fact]
    public async Task SendMessage_Success_SendsReceiveMessageAndMessageSent()
    {
        var senderId = Guid.NewGuid();
        var receiverId = Guid.NewGuid();
        var hub = CreateHub(senderId);

        var messageDto = new MessageDto
        {
            Id = Guid.NewGuid(),
            SenderId = senderId,
            ReceiverId = receiverId,
            Content = "Hello friend!"
        };

        _chatServiceMock.Setup(s => s.SaveMessageAsync(senderId, receiverId, "Hello friend!"))
            .ReturnsAsync(new Response<MessageDto>
            {
                Succeeded = true,
                StatusCode = System.Net.HttpStatusCode.OK,
                Data = messageDto
            });

        await hub.SendMessage(receiverId.ToString(), "Hello friend!");

        // Verify receiver gets message
        _userProxyMock.Verify(u => u.SendCoreAsync("ReceiveMessage", It.Is<object[]>(args => args.Length > 0 && args[0] == messageDto), default), Times.Once);

        // Verify caller gets MessageSent confirmation
        _callerMock.Verify(c => c.SendCoreAsync("MessageSent", It.Is<object[]>(args => args.Length > 0 && args[0] == messageDto), default), Times.Once);
    }
}
