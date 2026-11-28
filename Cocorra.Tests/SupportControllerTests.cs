using System.Net;
using System.Reflection;
using System.Security.Claims;
using Cocorra.API.Authorization;
using Cocorra.API.Controllers;
using Cocorra.API.Hubs;
using Cocorra.BLL.Base;
using Cocorra.BLL.Services.SupportService;
using Cocorra.DAL.DTOS.ReportDto;
using Cocorra.DAL.DTOS.SupportChatDto;
using Cocorra.DAL.DTOS.SupportDto;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
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

        _supportServiceMock.Setup(s => s.AdminReplyAsync(chatId, adminId.ToString(), dto, true)).ReturnsAsync(serviceResponse);

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

        _supportServiceMock.Setup(s => s.CloseChatAsync(chatId, adminId.ToString(), true)).ReturnsAsync(serviceResponse);

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

    [Theory]
    [InlineData(AppRoles.Admin, true)]
    [InlineData(AppRoles.Moderator, false)]
    public async Task GetReports_ClampsPagingAndPassesFiltersAndAdminFlag(string role, bool expectedIsAdmin)
    {
        var controller = CreateController(Guid.NewGuid(), role);
        _supportServiceMock
            .Setup(s => s.GetFilteredReportsAsync(new ReportListQuery(ReportCategory.Harassment, "Open", null, false, 1, 100), expectedIsAdmin))
            .ReturnsAsync(new PagedResponse<ReportDetailsDto>(new List<ReportDetailsDto>(), 0, 1, 100));

        var result = await controller.GetReports(ReportCategory.Harassment, "Open", pageNumber: 0, pageSize: 250);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(200, obj.StatusCode);
        _supportServiceMock.Verify(s => s.GetFilteredReportsAsync(new ReportListQuery(ReportCategory.Harassment, "Open", null, false, 1, 100), expectedIsAdmin), Times.Once);
    }

    [Theory]
    [InlineData(AppRoles.Admin, true)]
    [InlineData(AppRoles.Moderator, false)]
    public async Task GetReportById_PassesCallerIdAndAdminFlagToService(string role, bool expectedIsAdmin)
    {
        var callerId = Guid.NewGuid();
        var reportId = Guid.NewGuid();
        var controller = CreateController(callerId, role);
        _supportServiceMock
            .Setup(s => s.GetReportByIdAsync(reportId, callerId, expectedIsAdmin))
            .ReturnsAsync(new Response<ReportDetailsDto> { Succeeded = true, StatusCode = HttpStatusCode.OK, Data = new ReportDetailsDto { Id = reportId } });

        var result = await controller.GetReportById(reportId);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(200, obj.StatusCode);
        _supportServiceMock.Verify(s => s.GetReportByIdAsync(reportId, callerId, expectedIsAdmin), Times.Once);
    }

    [Fact]
    public async Task GetReportById_MissingCallerClaim_ReturnsUnauthorized()
    {
        var controller = CreateController();

        var result = await controller.GetReportById(Guid.NewGuid());

        Assert.IsType<UnauthorizedResult>(result);
        _supportServiceMock.Verify(s => s.GetReportByIdAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<bool>()), Times.Never);
    }

    [Theory]
    [InlineData(AppRoles.Admin, true)]
    [InlineData(AppRoles.Moderator, false)]
    public async Task ScreenshotUrls_PassCallerAdminFlagToService(string role, bool expectedIsAdmin)
    {
        var reportId = Guid.NewGuid();
        var ticketId = Guid.NewGuid();
        var controller = CreateController(Guid.NewGuid(), role);
        var urlResponse = new Response<Cocorra.DAL.DTOS.StorageDto.PrivateFileUrlDto>
        {
            Succeeded = true,
            StatusCode = HttpStatusCode.OK,
            Data = new Cocorra.DAL.DTOS.StorageDto.PrivateFileUrlDto { Url = "https://signed/url" }
        };
        _supportServiceMock.Setup(s => s.GetReportScreenshotUrlAsync(reportId, expectedIsAdmin)).ReturnsAsync(urlResponse);
        _supportServiceMock.Setup(s => s.GetTicketScreenshotUrlAsync(ticketId, expectedIsAdmin)).ReturnsAsync(urlResponse);

        Assert.Equal(200, Assert.IsType<ObjectResult>(await controller.GetReportScreenshotUrl(reportId)).StatusCode);
        Assert.Equal(200, Assert.IsType<ObjectResult>(await controller.GetTicketScreenshotUrl(ticketId)).StatusCode);
        _supportServiceMock.Verify(s => s.GetReportScreenshotUrlAsync(reportId, expectedIsAdmin), Times.Once);
        _supportServiceMock.Verify(s => s.GetTicketScreenshotUrlAsync(ticketId, expectedIsAdmin), Times.Once);
    }

    [Fact]
    public async Task GetTickets_ClampsPagingAndPassesFiltersAndAdminFlag()
    {
        var controller = CreateController(Guid.NewGuid(), AppRoles.Admin);
        _supportServiceMock
            .Setup(s => s.GetAdminTicketsAsync(SupportTicketType.TechnicalProblem, SupportTicketStatus.Open, 1, 50, true))
            .ReturnsAsync(new PagedResponse<SupportTicketDetailsDto>(new List<SupportTicketDetailsDto>(), 0, 1, 50));

        var result = await controller.GetTickets(SupportTicketType.TechnicalProblem, SupportTicketStatus.Open, pageNumber: 0, pageSize: 500);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(200, obj.StatusCode);
        _supportServiceMock.Verify(s => s.GetAdminTicketsAsync(SupportTicketType.TechnicalProblem, SupportTicketStatus.Open, 1, 50, true), Times.Once);
    }

    [Fact]
    public async Task GetTickets_ModeratorCaller_PassesCallerIsAdminFalse()
    {
        var controller = CreateController(Guid.NewGuid(), AppRoles.Moderator);
        _supportServiceMock
            .Setup(s => s.GetAdminTicketsAsync(null, null, 1, 10, false))
            .ReturnsAsync(new PagedResponse<SupportTicketDetailsDto>(new List<SupportTicketDetailsDto>(), 0, 1, 10));

        var result = await controller.GetTickets(null, null, pageNumber: 1, pageSize: 10);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(200, obj.StatusCode);
        _supportServiceMock.Verify(s => s.GetAdminTicketsAsync(null, null, 1, 10, false), Times.Once);
    }

    [Theory]
    [InlineData(AppRoles.Admin, true)]
    [InlineData(AppRoles.Moderator, false)]
    public async Task GetTicketById_PassesCallerIdAndAdminFlag(string role, bool expectedIsAdmin)
    {
        var callerId = Guid.NewGuid();
        var ticketId = Guid.NewGuid();
        var controller = CreateController(callerId, role);
        _supportServiceMock
            .Setup(s => s.GetTicketByIdAsync(ticketId, callerId, expectedIsAdmin))
            .ReturnsAsync(new Response<SupportTicketDetailsDto> { Succeeded = true, StatusCode = HttpStatusCode.OK, Data = new SupportTicketDetailsDto { Id = ticketId } });

        var result = await controller.GetTicketById(ticketId);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(200, obj.StatusCode);
        _supportServiceMock.Verify(s => s.GetTicketByIdAsync(ticketId, callerId, expectedIsAdmin), Times.Once);
    }

    [Theory]
    [InlineData(AppRoles.Admin, true)]
    [InlineData(AppRoles.Moderator, false)]
    public async Task UpdateTicketStatus_PassesStatusReasonCallerIdAndAdminFlagToService(string role, bool expectedIsAdmin)
    {
        var id = Guid.NewGuid();
        var callerId = Guid.NewGuid();
        var controller = CreateController(callerId, role);
        _supportServiceMock.Setup(s => s.UpdateTicketStatusAsync(id, SupportTicketStatus.Resolved, "Resolved after triage", callerId, expectedIsAdmin))
            .ReturnsAsync(new Response<string> { Succeeded = true, StatusCode = HttpStatusCode.OK });

        var result = await controller.UpdateTicketStatus(id, new UpdateTicketStatusDto
        {
            Status = SupportTicketStatus.Resolved,
            Reason = "Resolved after triage"
        });

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(200, obj.StatusCode);
        _supportServiceMock.Verify(s => s.UpdateTicketStatusAsync(id, SupportTicketStatus.Resolved, "Resolved after triage", callerId, expectedIsAdmin), Times.Once);
    }

    [Theory]
    [InlineData(AppRoles.Admin, true)]
    [InlineData(AppRoles.Moderator, false)]
    public async Task UpdateReportStatus_PassesStatusReasonCallerIdAndAdminFlagToService(string role, bool expectedIsAdmin)
    {
        var id = Guid.NewGuid();
        var callerId = Guid.NewGuid();
        var controller = CreateController(callerId, role);
        _supportServiceMock.Setup(s => s.UpdateReportStatusAsync(id, "Resolved", "Assigned for investigation", callerId, expectedIsAdmin))
            .ReturnsAsync(new Response<string> { Succeeded = true, StatusCode = HttpStatusCode.OK });

        var result = await controller.UpdateReportStatus(id, new UpdateReportStatusDto
        {
            Status = "Resolved",
            Reason = "Assigned for investigation"
        });

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(200, obj.StatusCode);
        _supportServiceMock.Verify(s => s.UpdateReportStatusAsync(id, "Resolved", "Assigned for investigation", callerId, expectedIsAdmin), Times.Once);
    }

    [Theory]
    [InlineData(AppRoles.Admin, true)]
    [InlineData(AppRoles.Moderator, false)]
    public async Task ChatReplyAndClose_PassCallerAdminFlagToService(string role, bool expectedIsAdmin)
    {
        var staffId = Guid.NewGuid();
        var chatId = Guid.NewGuid();
        var dto = new SendMessageDto { Content = "Following up" };
        var controller = CreateController(staffId, role);

        _supportServiceMock.Setup(s => s.AdminReplyAsync(chatId, staffId.ToString(), dto, expectedIsAdmin))
            .ReturnsAsync(new Response<AdminReplyResultDto>
            {
                Succeeded = true,
                StatusCode = HttpStatusCode.OK,
                Data = new AdminReplyResultDto { UserId = Guid.NewGuid().ToString(), Message = new SupportMessageDto { Content = dto.Content } }
            });
        _supportServiceMock.Setup(s => s.CloseChatAsync(chatId, staffId.ToString(), expectedIsAdmin))
            .ReturnsAsync(new Response<string> { Succeeded = true, StatusCode = HttpStatusCode.OK });

        Assert.Equal(200, Assert.IsType<ObjectResult>(await controller.AdminReply(chatId, dto)).StatusCode);
        Assert.Equal(200, Assert.IsType<ObjectResult>(await controller.CloseChat(chatId)).StatusCode);
        _supportServiceMock.Verify(s => s.AdminReplyAsync(chatId, staffId.ToString(), dto, expectedIsAdmin), Times.Once);
        _supportServiceMock.Verify(s => s.CloseChatAsync(chatId, staffId.ToString(), expectedIsAdmin), Times.Once);
    }

    [Theory]
    [InlineData(AppRoles.Admin, true)]
    [InlineData(AppRoles.Moderator, false)]
    public async Task TakeReportAction_PassesCallerIdAndAdminFlag(string role, bool expectedIsAdmin)
    {
        var id = Guid.NewGuid();
        var callerId = Guid.NewGuid();
        var dto = new TakeReportActionDto { Action = AdminReportAction.WarnUser, Reason = "Guideline reminder" };
        var controller = CreateController(callerId, role);
        _supportServiceMock.Setup(s => s.TakeActionOnReportAsync(id, dto, callerId, expectedIsAdmin))
            .ReturnsAsync(new Response<string> { Succeeded = true, StatusCode = HttpStatusCode.OK });

        var result = await controller.TakeReportAction(id, dto);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(200, obj.StatusCode);
        _supportServiceMock.Verify(s => s.TakeActionOnReportAsync(id, dto, callerId, expectedIsAdmin), Times.Once);
    }

    [Fact]
    public void StaffSupportEndpoints_UseAdminOrModeratorPolicy_AndNeverRawRoleAttributes()
    {
        var staffMethods = new[]
        {
            nameof(SupportController.GetReports),
            nameof(SupportController.GetReportById),
            nameof(SupportController.GetReportScreenshotUrl),
            nameof(SupportController.GetTickets),
            nameof(SupportController.GetTicketById),
            nameof(SupportController.UpdateTicketStatus),
            nameof(SupportController.GetTicketScreenshotUrl),
            nameof(SupportController.UpdateReportStatus),
            nameof(SupportController.TakeReportAction),
            nameof(SupportController.ClaimChat),
            nameof(SupportController.AdminReply),
            nameof(SupportController.CloseChat),
            nameof(SupportController.GetPendingChats),
            nameof(SupportController.GetAdminActiveChats)
        };

        foreach (var name in staffMethods)
        {
            var attr = typeof(SupportController).GetMethod(name)!
                .GetCustomAttributes<AuthorizeAttribute>(false)
                .Single();
            Assert.Equal(AuthPolicies.AdminOrModerator, attr.Policy);
            Assert.Null(attr.Roles);
        }

        var allAuthorizeAttributes = typeof(SupportController)
            .GetCustomAttributes<AuthorizeAttribute>(true)
            .Concat(typeof(SupportController)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .SelectMany(m => m.GetCustomAttributes<AuthorizeAttribute>(false)));

        Assert.All(allAuthorizeAttributes, attr => Assert.Null(attr.Roles));
    }

    [Theory]
    [InlineData(AppRoles.Admin, AuthPolicies.ActiveStatus, true)]
    [InlineData(AppRoles.Moderator, AuthPolicies.ActiveStatus, true)]
    [InlineData(AppRoles.RecordReviewer, AuthPolicies.ActiveStatus, false)]
    [InlineData(AppRoles.Coach, AuthPolicies.ActiveStatus, false)]
    [InlineData(AppRoles.User, AuthPolicies.ActiveStatus, false)]
    [InlineData(AppRoles.Admin, "Pending", false)]
    [InlineData(AppRoles.Moderator, "ReRecord", false)]
    [InlineData(AppRoles.Moderator, "Banned", false)]
    [InlineData(AppRoles.Admin, null, false)]
    public async Task AdminOrModeratorPolicy_EnforcesRoleAndActiveVerificationStatus(
        string role, string? verificationStatus, bool expectedAllowed)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization(AuthPolicies.Register);
        using var provider = services.BuildServiceProvider();
        var authorizationService = provider.GetRequiredService<IAuthorizationService>();

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Role, role)
        };
        if (verificationStatus is not null)
        {
            claims.Add(new Claim(AuthPolicies.VerificationStatusClaim, verificationStatus));
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
        var authResult = await authorizationService.AuthorizeAsync(principal, null, AuthPolicies.AdminOrModerator);

        Assert.Equal(expectedAllowed, authResult.Succeeded);
    }
}
