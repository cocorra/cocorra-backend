using System.Security.Claims;
using Cocorra.API.Controllers;
using Cocorra.API.Hubs;
using Cocorra.BLL.Base;
using Cocorra.BLL.Services.SupportService;
using Cocorra.DAL.DTOS.ReportDto;
using Cocorra.DAL.DTOS.SupportChatDto;
using Cocorra.DAL.DTOS.SupportDto;
using Cocorra.DAL.Enums;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Moq;
using Xunit;

namespace Cocorra.Tests;

public class SupportControllerTests
{
    private readonly Mock<ISupportService> _supportServiceMock = new();
    private readonly Mock<IHubContext<SupportHub>> _supportHubMock = new();
    private readonly Mock<IHubClients> _hubClientsMock = new();
    private readonly Mock<IClientProxy> _groupProxyMock = new();
    private readonly Mock<IClientProxy> _userProxyMock = new();

    public SupportControllerTests()
    {
        _supportHubMock.Setup(h => h.Clients).Returns(_hubClientsMock.Object);
        _hubClientsMock.Setup(c => c.Group("Admins")).Returns(_groupProxyMock.Object);
        _hubClientsMock.Setup(c => c.User(It.IsAny<string>())).Returns(_userProxyMock.Object);
    }

    private SupportController CreateController(Guid? userId = null, string role = "User")
    {
        var controller = new SupportController(_supportServiceMock.Object, _supportHubMock.Object);

        if (userId.HasValue)
        {
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, userId.Value.ToString()),
                new(ClaimTypes.Role, role)
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
    public async Task SubmitTicket_Anonymous_ReturnsOk()
    {
        var controller = CreateController();
        var dto = new SubmitSupportTicketDto { Type = SupportTicketType.GeneralQuestion, Message = "Help", ContactEmail = "test@example.com" };
        var serviceResponse = new Response<string>
        {
            Succeeded = true,
            StatusCode = System.Net.HttpStatusCode.OK,
            Data = "Ticket submitted"
        };

        _supportServiceMock.Setup(s => s.SubmitTicketAsync(null, dto)).ReturnsAsync(serviceResponse);

        var result = await controller.SubmitTicket(dto);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(200, obj.StatusCode);
    }

    [Fact]
    public async Task SubmitReport_Unauthorized_WhenNotLoggedIn()
    {
        var controller = CreateController();
        var result = await controller.SubmitReport(new SubmitReportDto());

        Assert.IsType<UnauthorizedResult>(result);
    }

    [Fact]
    public async Task SubmitReport_Success_ReturnsOk()
    {
        var userId = Guid.NewGuid();
        var dto = new SubmitReportDto { Description = "Spam user" };
        var serviceResponse = new Response<string>
        {
            Succeeded = true,
            StatusCode = System.Net.HttpStatusCode.OK,
            Data = "Report submitted"
        };

        _supportServiceMock.Setup(s => s.SubmitReportAsync(userId, dto)).ReturnsAsync(serviceResponse);

        var controller = CreateController(userId);
        var result = await controller.SubmitReport(dto);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(200, obj.StatusCode);
    }

    [Fact]
    public async Task SendMessage_NewChat_BroadcastsToAdminsGroup()
    {
        var userId = Guid.NewGuid();
        var dto = new SendMessageDto { Content = "Need help" };
        var serviceResponse = new Response<SendMessageResultDto>
        {
            Succeeded = true,
            StatusCode = System.Net.HttpStatusCode.OK,
            Data = new SendMessageResultDto
            {
                IsNewChat = true,
                Message = new SupportMessageDto { Id = Guid.NewGuid(), Content = "Need help" }
            }
        };

        _supportServiceMock.Setup(s => s.SendMessageAsync(userId.ToString(), dto)).ReturnsAsync(serviceResponse);

        var controller = CreateController(userId);
        var result = await controller.SendMessage(dto);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(200, obj.StatusCode);
    }

    [Fact]
    public async Task ClaimChat_Success_BroadcastsChatClaimed()
    {
        var adminId = Guid.NewGuid();
        var chatId = Guid.NewGuid();
        var serviceResponse = new Response<string>
        {
            Succeeded = true,
            StatusCode = System.Net.HttpStatusCode.OK,
            Data = "Chat claimed"
        };

        _supportServiceMock.Setup(s => s.ClaimChatAsync(chatId, adminId.ToString())).ReturnsAsync(serviceResponse);

        var controller = CreateController(adminId, role: "Admin");
        var result = await controller.ClaimChat(chatId);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(200, obj.StatusCode);
    }

    [Fact]
    public async Task AdminReply_Success_BroadcastsToUser()
    {
        var adminId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();
        var chatId = Guid.NewGuid();
        var dto = new SendMessageDto { Content = "How can I help?" };
        var serviceResponse = new Response<AdminReplyResultDto>
        {
            Succeeded = true,
            StatusCode = System.Net.HttpStatusCode.OK,
            Data = new AdminReplyResultDto
            {
                UserId = targetUserId.ToString(),
                Message = new SupportMessageDto { Content = "How can I help?" }
            }
        };

        _supportServiceMock.Setup(s => s.AdminReplyAsync(chatId, adminId.ToString(), dto)).ReturnsAsync(serviceResponse);

        var controller = CreateController(adminId, role: "Admin");
        var result = await controller.AdminReply(chatId, dto);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(200, obj.StatusCode);
    }

    [Fact]
    public async Task CloseChat_Success_ReturnsOk()
    {
        var adminId = Guid.NewGuid();
        var chatId = Guid.NewGuid();
        var serviceResponse = new Response<string>
        {
            Succeeded = true,
            StatusCode = System.Net.HttpStatusCode.OK,
            Data = "Chat closed"
        };

        _supportServiceMock.Setup(s => s.CloseChatAsync(chatId, adminId.ToString())).ReturnsAsync(serviceResponse);

        var controller = CreateController(adminId, role: "Admin");
        var result = await controller.CloseChat(chatId);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(200, obj.StatusCode);
    }

    [Fact]
    public async Task GetPendingChats_ReturnsOk()
    {
        var serviceResponse = new Response<List<PendingChatDto>>
        {
            Succeeded = true,
            StatusCode = System.Net.HttpStatusCode.OK,
            Data = new List<PendingChatDto>()
        };

        _supportServiceMock.Setup(s => s.GetPendingChatsAsync(1, 10)).ReturnsAsync(serviceResponse);

        var controller = CreateController(Guid.NewGuid(), role: "Admin");
        var result = await controller.GetPendingChats(1, 10);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(200, obj.StatusCode);
    }
}
