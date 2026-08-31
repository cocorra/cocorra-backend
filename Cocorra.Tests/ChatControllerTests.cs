using System.Security.Claims;
using Cocorra.API.Controllers;
using Cocorra.BLL.Base;
using Cocorra.BLL.Services.ChatService;
using Cocorra.DAL.DTOS.ChatDto;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Cocorra.Tests;

public class ChatControllerTests
{
    private readonly Mock<IChatService> _chatServiceMock = new();

    private ChatController CreateController(Guid? userId = null)
    {
        var controller = new ChatController(_chatServiceMock.Object);

        if (userId.HasValue)
        {
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, userId.Value.ToString())
            };
            var identity = new ClaimsIdentity(claims, "TestAuth");
            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
            };
        }
        else
        {
            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal() }
            };
        }

        return controller;
    }

    [Fact]
    public async Task GetFriendsList_Unauthorized_WhenNoUser()
    {
        var controller = CreateController();
        var result = await controller.GetFriendsList();

        Assert.IsType<UnauthorizedResult>(result);
    }

    [Fact]
    public async Task GetFriendsList_Success_ReturnsStatusCodeResult()
    {
        var userId = Guid.NewGuid();
        var friends = new List<ChatFriendDto>
        {
            new() { FriendId = Guid.NewGuid(), FullName = "Friend One" }
        };
        var serviceResponse = new Response<IEnumerable<ChatFriendDto>>
        {
            Succeeded = true,
            StatusCode = System.Net.HttpStatusCode.OK,
            Data = friends
        };

        _chatServiceMock.Setup(s => s.GetChatFriendsListAsync(userId, 1, 20)).ReturnsAsync(serviceResponse);

        var controller = CreateController(userId);
        var result = await controller.GetFriendsList(1, 20);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(200, obj.StatusCode);
        Assert.Equal(serviceResponse, obj.Value);
    }

    [Fact]
    public async Task GetChatHistory_ClampsPageSize_AndReturnsOk()
    {
        var userId = Guid.NewGuid();
        var friendId = Guid.NewGuid();
        var messages = new List<MessageDto>
        {
            new() { Id = Guid.NewGuid(), Content = "Hello", SenderId = userId }
        };
        var serviceResponse = new Response<IEnumerable<MessageDto>>
        {
            Succeeded = true,
            StatusCode = System.Net.HttpStatusCode.OK,
            Data = messages
        };

        // If pageSize passed is 200, it clamps to 100
        _chatServiceMock.Setup(s => s.GetChatHistoryAsync(userId, friendId, 1, 100)).ReturnsAsync(serviceResponse);

        var controller = CreateController(userId);
        var result = await controller.GetChatHistory(friendId, 0, 200);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(200, obj.StatusCode);
        Assert.Equal(serviceResponse, obj.Value);
    }

    [Fact]
    public async Task MarkMessagesAsRead_Success_ReturnsOk()
    {
        var userId = Guid.NewGuid();
        var friendId = Guid.NewGuid();
        var serviceResponse = new Response<string>
        {
            Succeeded = true,
            StatusCode = System.Net.HttpStatusCode.OK,
            Data = "Marked as read"
        };

        _chatServiceMock.Setup(s => s.MarkMessagesAsReadAsync(userId, friendId)).ReturnsAsync(serviceResponse);

        var controller = CreateController(userId);
        var result = await controller.MarkMessagesAsRead(friendId);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(200, obj.StatusCode);
        Assert.Equal(serviceResponse, obj.Value);
    }
}
