using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using Cocorra.BLL.Services.EventTracking;
using Cocorra.BLL.Services.NotificationService;
using Cocorra.BLL.Services.RealTimeNotifier;
using Cocorra.BLL.Services.SupportService;
using Cocorra.BLL.Services.Upload;
using Cocorra.DAL.DTOS.ReportDto;
using Cocorra.DAL.DTOS.SupportChatDto;
using Cocorra.DAL.DTOS.SupportDto;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;
using Cocorra.DAL.Repository.NotificationRepository;
using Cocorra.DAL.Repository.SupportRepository;
using Cocorra.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Moq;
using Xunit;

namespace Cocorra.Tests;

public class SupportServiceTests
{
    private readonly Mock<ISupportRepository> _supportRepoMock = new();
    private readonly Mock<IUploadImage> _uploadImageMock = new();
    private readonly Mock<UserManager<ApplicationUser>> _userManagerMock = TestIdentityHelper.CreateMockUserManager();
    private readonly Mock<INotificationRepository> _notificationRepoMock = new();
    private readonly Mock<IRealTimeNotifier> _realTimeNotifierMock = new();
    private readonly Mock<IPushNotificationService> _pushServiceMock = new();
    private readonly Mock<IEventTracker> _eventTrackerMock = new();
    private readonly SupportService _service;

    public SupportServiceTests()
    {
        _eventTrackerMock.Setup(e => e.NewEventEmissionEnabled).Returns(true);
        _service = new SupportService(
            _supportRepoMock.Object,
            _uploadImageMock.Object,
            _userManagerMock.Object,
            _notificationRepoMock.Object,
            _realTimeNotifierMock.Object,
            _pushServiceMock.Object,
            _eventTrackerMock.Object
        );
    }

    [Fact]
    public async Task SubmitTicketAsync_WithoutScreenshot_SavesTicketAndReturnsSuccess()
    {
        var userId = Guid.NewGuid();
        var dto = new SubmitSupportTicketDto
        {
            Type = SupportTicketType.GeneralQuestion,
            Message = "How does audio verification work?",
            ContactEmail = "user@test.com"
        };

        var result = await _service.SubmitTicketAsync(userId, dto);

        Assert.True(result.Succeeded);
        Assert.Equal("Support ticket submitted successfully.", result.Data);
        _supportRepoMock.Verify(r => r.AddTicketAsync(It.Is<SupportTicket>(t =>
            t.UserId == userId &&
            t.Message == dto.Message &&
            t.Status == "Open" &&
            t.ScreenshotPath == null)), Times.Once);
    }

    [Fact]
    public async Task SubmitTicketAsync_WithScreenshot_UploadsImageAndSavesTicket()
    {
        var fileMock = new Mock<IFormFile>();
        fileMock.Setup(f => f.Length).Returns(100);
        _uploadImageMock.Setup(u => u.SaveImageAsync(fileMock.Object, "Profiles"))
            .ReturnsAsync("https://cdn.cocorra.com/screenshot.jpg");

        var dto = new SubmitSupportTicketDto
        {
            Type = SupportTicketType.TechnicalProblem,
            Message = "Button not working",
            Screenshot = fileMock.Object
        };

        var result = await _service.SubmitTicketAsync(null, dto);

        Assert.True(result.Succeeded);
        _supportRepoMock.Verify(r => r.AddTicketAsync(It.Is<SupportTicket>(t =>
            t.ScreenshotPath == "https://cdn.cocorra.com/screenshot.jpg")), Times.Once);
    }

    [Fact]
    public async Task SubmitReportAsync_MissingBothUserAndRoom_ReturnsBadRequest()
    {
        var dto = new SubmitReportDto
        {
            ReportedUserId = null,
            ReportedRoomId = null,
            Category = ReportCategory.Harassment,
            Description = "Bad behavior"
        };

        var result = await _service.SubmitReportAsync(Guid.NewGuid(), dto);

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        Assert.Contains("must specify a user or a room", result.Message);
    }

    [Fact]
    public async Task SubmitReportAsync_ValidUserReport_SavesReportAndTracksEvent()
    {
        var reporterId = Guid.NewGuid();
        var reportedUserId = Guid.NewGuid();
        var dto = new SubmitReportDto
        {
            ReportedUserId = reportedUserId,
            Category = ReportCategory.Spam,
            Description = "Spamming voice room"
        };

        var result = await _service.SubmitReportAsync(reporterId, dto);

        Assert.True(result.Succeeded);
        Assert.Equal("Report submitted successfully.", result.Data);

        _supportRepoMock.Verify(r => r.AddReportAsync(It.Is<Report>(rep =>
            rep.ReporterId == reporterId &&
            rep.ReportedUserId == reportedUserId &&
            rep.Category == ReportCategory.Spam &&
            rep.Status == "Open")), Times.Once);

        _eventTrackerMock.Verify(t => t.Track(
            EventTypes.UserReported,
            reporterId,
            It.IsAny<object>()), Times.Once);
    }

    [Fact]
    public async Task UpdateReportStatusAsync_ReportNotFound_ReturnsNotFound()
    {
        var reportId = Guid.NewGuid();
        _supportRepoMock.Setup(r => r.GetReportByIdAsync(reportId)).ReturnsAsync((Report?)null);

        var result = await _service.UpdateReportStatusAsync(reportId, "UnderReview");

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.NotFound, result.StatusCode);
    }

    [Fact]
    public async Task UpdateReportStatusAsync_ValidReport_UpdatesStatus()
    {
        var reportId = Guid.NewGuid();
        var report = new Report { Id = reportId, Status = "Open" };
        _supportRepoMock.Setup(r => r.GetReportByIdAsync(reportId)).ReturnsAsync(report);

        var result = await _service.UpdateReportStatusAsync(reportId, "Resolved");

        Assert.True(result.Succeeded);
        Assert.Equal("Resolved", report.Status);
        _supportRepoMock.Verify(r => r.UpdateReportAsync(report), Times.Once);
    }

    [Fact]
    public async Task TakeActionOnReportAsync_WarnUser_CreatesWarningNotificationAndPushes()
    {
        var reportId = Guid.NewGuid();
        var reportedUserId = Guid.NewGuid();
        var report = new Report { Id = reportId, ReportedUserId = reportedUserId, Status = "Open" };
        var reportedUser = new ApplicationUser { Id = reportedUserId, FcmToken = "fcm_warn" };

        _supportRepoMock.Setup(r => r.GetReportByIdAsync(reportId)).ReturnsAsync(report);
        _userManagerMock.Setup(m => m.FindByIdAsync(reportedUserId.ToString())).ReturnsAsync(reportedUser);

        var dto = new TakeReportActionDto
        {
            Action = AdminReportAction.WarnUser,
            AdminNote = "Stop spamming rooms"
        };

        var result = await _service.TakeActionOnReportAsync(reportId, dto);

        Assert.True(result.Succeeded);
        Assert.Equal("Resolved", report.Status);

        _notificationRepoMock.Verify(n => n.AddAsync(It.Is<Notification>(notif =>
            notif.UserId == reportedUserId &&
            notif.Type == NotificationType.AdminWarning &&
            notif.Message == "Stop spamming rooms")), Times.Once);

        _pushServiceMock.Verify(p => p.SendPushNotificationAsync(
            "fcm_warn",
            "Admin Warning",
            "Stop spamming rooms",
            It.IsAny<Dictionary<string, string>>()), Times.Once);
    }

    [Fact]
    public async Task TakeActionOnReportAsync_BanUser_BansUserCallsForceLogoutAndClearsTokens()
    {
        var reportId = Guid.NewGuid();
        var reportedUserId = Guid.NewGuid();
        var report = new Report { Id = reportId, ReportedUserId = reportedUserId, Status = "Open" };
        var reportedUser = new ApplicationUser
        {
            Id = reportedUserId,
            Status = UserStatus.Active,
            RefreshToken = "token",
            FcmToken = "fcm_ban"
        };

        _supportRepoMock.Setup(r => r.GetReportByIdAsync(reportId)).ReturnsAsync(report);
        _userManagerMock.Setup(m => m.FindByIdAsync(reportedUserId.ToString())).ReturnsAsync(reportedUser);
        _userManagerMock.Setup(m => m.UpdateAsync(reportedUser)).ReturnsAsync(IdentityResult.Success);

        var dto = new TakeReportActionDto { Action = AdminReportAction.BanUser };

        var result = await _service.TakeActionOnReportAsync(reportId, dto);

        Assert.True(result.Succeeded);
        Assert.Equal(UserStatus.Banned, reportedUser.Status);
        Assert.Null(reportedUser.RefreshToken);
        Assert.Null(reportedUser.FcmToken);

        _realTimeNotifierMock.Verify(n => n.ForceLogoutAsync(reportedUserId, It.IsAny<string>()), Times.Once);
        _notificationRepoMock.Verify(n => n.AddAsync(It.Is<Notification>(notif =>
            notif.UserId == reportedUserId &&
            notif.Type == NotificationType.AdminWarning)), Times.Once);
    }

    [Fact]
    public async Task SendMessageAsync_PendingMessageLimitReached_ReturnsBadRequest()
    {
        var userId = Guid.NewGuid().ToString();
        var chatId = Guid.NewGuid();
        var chat = new SupportChat { Id = chatId, UserId = userId, Status = SupportChatStatus.Pending };

        _supportRepoMock.Setup(r => r.GetUserOpenChatAsync(userId)).ReturnsAsync(chat);
        _supportRepoMock.Setup(r => r.GetPendingUserMessageCountAsync(chatId)).ReturnsAsync(3);

        var result = await _service.SendMessageAsync(userId, new SendMessageDto { Content = "Fourth message" });

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        Assert.Contains("maximum messages", result.Message);
        _supportRepoMock.Verify(r => r.AddMessageAsync(It.IsAny<SupportMessage>()), Times.Never);
    }

    [Fact]
    public async Task SendMessageAsync_ValidMessage_PersistsMessageWithServerDeterminedIsFromAdminFalse()
    {
        var userId = Guid.NewGuid().ToString();
        var chatId = Guid.NewGuid();
        var chat = new SupportChat { Id = chatId, UserId = userId, Status = SupportChatStatus.Pending };

        _supportRepoMock.Setup(r => r.GetUserOpenChatAsync(userId)).ReturnsAsync(chat);
        _supportRepoMock.Setup(r => r.GetPendingUserMessageCountAsync(chatId)).ReturnsAsync(1);

        var result = await _service.SendMessageAsync(userId, new SendMessageDto { Content = "I need help with login" });

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Data);
        Assert.Equal("I need help with login", result.Data.Message.Content);
        Assert.False(result.Data.Message.IsFromAdmin);

        _supportRepoMock.Verify(r => r.AddMessageAsync(It.Is<SupportMessage>(m =>
            m.SupportChatId == chatId &&
            m.SenderId == userId &&
            m.Content == "I need help with login" &&
            m.IsFromAdmin == false)), Times.Once);
    }

    [Fact]
    public async Task ClaimChatAsync_ChatNotFound_ReturnsNotFound()
    {
        var chatId = Guid.NewGuid();
        _supportRepoMock.Setup(r => r.GetChatByIdAsync(chatId)).ReturnsAsync((SupportChat?)null);

        var result = await _service.ClaimChatAsync(chatId, "admin-1");

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.NotFound, result.StatusCode);
    }

    [Fact]
    public async Task ClaimChatAsync_AlreadyClaimedByAnotherAdmin_ReturnsBadRequest()
    {
        var chatId = Guid.NewGuid();
        var chat = new SupportChat { Id = chatId, AdminId = "admin-2", Status = SupportChatStatus.Active };
        _supportRepoMock.Setup(r => r.GetChatByIdAsync(chatId)).ReturnsAsync(chat);

        var result = await _service.ClaimChatAsync(chatId, "admin-1");

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        Assert.Contains("not pending", result.Message);
    }

    [Fact]
    public async Task ClaimChatAsync_ValidChat_AssignsAdminAndActivates()
    {
        var chatId = Guid.NewGuid();
        var chat = new SupportChat { Id = chatId, Status = SupportChatStatus.Pending, AdminId = null };
        _supportRepoMock.Setup(r => r.GetChatByIdAsync(chatId)).ReturnsAsync(chat);

        var result = await _service.ClaimChatAsync(chatId, "admin-1");

        Assert.True(result.Succeeded);
        Assert.Equal("admin-1", chat.AdminId);
        Assert.Equal(SupportChatStatus.Active, chat.Status);
        _supportRepoMock.Verify(r => r.UpdateChatAsync(chat), Times.Once);
    }

    [Fact]
    public async Task CloseChatAsync_ValidChat_MarksClosed()
    {
        var chatId = Guid.NewGuid();
        var chat = new SupportChat { Id = chatId, Status = SupportChatStatus.Active, AdminId = "admin-1" };
        _supportRepoMock.Setup(r => r.GetChatByIdAsync(chatId)).ReturnsAsync(chat);

        var result = await _service.CloseChatAsync(chatId, "admin-1");

        Assert.True(result.Succeeded);
        Assert.Equal(SupportChatStatus.Closed, chat.Status);
        _supportRepoMock.Verify(r => r.UpdateChatAsync(chat), Times.Once);
    }
}
