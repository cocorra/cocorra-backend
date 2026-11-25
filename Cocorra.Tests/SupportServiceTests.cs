using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Cocorra.BLL.Services.EventTracking;
using Cocorra.BLL.Services.NotificationService;
using Cocorra.BLL.Services.RealTimeNotifier;
using Cocorra.BLL.Services.Storage;
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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Cocorra.Tests;

public class SupportServiceTests
{
    private readonly Mock<ISupportRepository> _supportRepoMock = new();
    private readonly FakePrivateFileUrlIssuer FileUrls = new();
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
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AppSettings:BaseUrl"] = "https://api.example.com/"
            })
            .Build();
        _service = new SupportService(
            _supportRepoMock.Object,
            _uploadImageMock.Object,
            _userManagerMock.Object,
            _notificationRepoMock.Object,
            _realTimeNotifierMock.Object,
            _pushServiceMock.Object,
            _eventTrackerMock.Object,
            NullLogger<SupportService>.Instance,
            FileUrls,
            configuration
        );
    }

    [Fact]
    public async Task GetFilteredReportsAsync_ReturnsHasScreenshotFlags_AndNeverAPathOrUrl()
    {
        var reporterId = Guid.NewGuid();
        var withShot = new Report { Id = Guid.NewGuid(), ReporterId = reporterId, ScreenshotPath = "Uploads/img/Support/s.png", Status = "Open" };
        var withoutShot = new Report { Id = Guid.NewGuid(), ReporterId = Guid.NewGuid(), Status = "Open" };
        _supportRepoMock.Setup(r => r.GetPagedReportsAsync(null, null, 1, 20, false))
            .ReturnsAsync((2, new List<Report> { withShot, withoutShot }));

        var result = await _service.GetFilteredReportsAsync(null, null, callerIsAdmin: true);

        var items = result.Data!.ToList();
        Assert.Equal(2, result.TotalCount);
        Assert.Equal(1, result.CurrentPage);
        Assert.Equal(20, result.PageSize);
        Assert.True(items[0].HasScreenshot);
        Assert.False(items[1].HasScreenshot);
        Assert.All(items, r => Assert.Null(r.ScreenshotPath));
        Assert.Empty(FileUrls.Calls);
    }

    [Fact]
    public async Task GetReportScreenshotUrlAsync_ReportWithScreenshot_ReturnsAnIssuedUrl()
    {
        var report = new Report
        {
            Id = Guid.NewGuid(),
            ReporterId = Guid.NewGuid(),
            ReportedUserId = Guid.NewGuid(),
            ScreenshotPath = "Uploads/img/Support/r.png"
        };
        _supportRepoMock.Setup(r => r.GetReportByIdAsync(report.Id)).ReturnsAsync(report);

        var result = await _service.GetReportScreenshotUrlAsync(report.Id, callerIsAdmin: true);

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal("signed:Uploads/img/Support/r.png", result.Data!.Url);
        var call = Assert.Single(FileUrls.Calls);
        Assert.Equal(PrivateFileKind.ReportScreenshot, call.Kind);
        Assert.Equal(report.Id, call.Files[0].SubjectId);
        Assert.Equal(report.ReportedUserId, call.Files[0].TargetUserId);
    }

    [Fact]
    public async Task GetReportScreenshotUrlAsync_NoScreenshot_ReturnsNotFound()
    {
        var report = new Report { Id = Guid.NewGuid(), ReporterId = Guid.NewGuid() };
        _supportRepoMock.Setup(r => r.GetReportByIdAsync(report.Id)).ReturnsAsync(report);

        var result = await _service.GetReportScreenshotUrlAsync(report.Id, callerIsAdmin: true);

        Assert.Equal(HttpStatusCode.NotFound, result.StatusCode);
    }

    [Fact]
    public async Task GetReportScreenshotUrlAsync_UnknownReport_ReturnsNotFoundWithoutIssuing()
    {
        _supportRepoMock.Setup(r => r.GetReportByIdAsync(It.IsAny<Guid>())).ReturnsAsync((Report?)null);

        var result = await _service.GetReportScreenshotUrlAsync(Guid.NewGuid(), callerIsAdmin: true);

        Assert.Equal(HttpStatusCode.NotFound, result.StatusCode);
        Assert.Empty(FileUrls.Calls);
    }

    [Fact]
    public async Task GetTicketScreenshotUrlAsync_AnonymousTicket_ReturnsAnIssuedUrl()
    {
        var ticket = new SupportTicket { Id = Guid.NewGuid(), UserId = null, ScreenshotPath = "Uploads/img/Support/t.png" };
        _supportRepoMock.Setup(r => r.GetTicketByIdAsync(ticket.Id)).ReturnsAsync(ticket);

        var result = await _service.GetTicketScreenshotUrlAsync(ticket.Id, callerIsAdmin: true);

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal("signed:Uploads/img/Support/t.png", result.Data!.Url);
        var call = Assert.Single(FileUrls.Calls);
        Assert.Equal(PrivateFileKind.TicketScreenshot, call.Kind);
        Assert.Null(call.Files[0].TargetUserId);
    }

    [Fact]
    public async Task GetTicketScreenshotUrlAsync_UnknownTicket_ReturnsNotFound()
    {
        _supportRepoMock.Setup(r => r.GetTicketByIdAsync(It.IsAny<Guid>())).ReturnsAsync((SupportTicket?)null);

        var result = await _service.GetTicketScreenshotUrlAsync(Guid.NewGuid(), callerIsAdmin: true);

        Assert.Equal(HttpStatusCode.NotFound, result.StatusCode);
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
        _uploadImageMock.Setup(u => u.SavePrivateImageAsync(fileMock.Object, "Support"))
            .ReturnsAsync("Uploads/img/Support/screenshot.jpg");

        var dto = new SubmitSupportTicketDto
        {
            Type = SupportTicketType.TechnicalProblem,
            Message = "Button not working",
            Screenshot = fileMock.Object
        };

        var result = await _service.SubmitTicketAsync(null, dto);

        Assert.True(result.Succeeded);
        _supportRepoMock.Verify(r => r.AddTicketAsync(It.Is<SupportTicket>(t =>
            t.ScreenshotPath == "Uploads/img/Support/screenshot.jpg")), Times.Once);
        // Screenshots are private: never stored through the public-URL save path.
        _uploadImageMock.Verify(u => u.SaveImageAsync(It.IsAny<IFormFile>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task SubmitReportAsync_WithScreenshot_StoresTheObjectKey()
    {
        var fileMock = new Mock<IFormFile>();
        fileMock.Setup(f => f.Length).Returns(100);
        _uploadImageMock.Setup(u => u.SavePrivateImageAsync(fileMock.Object, "Support"))
            .ReturnsAsync("Uploads/img/Support/report.png");

        var result = await _service.SubmitReportAsync(Guid.NewGuid(), new SubmitReportDto
        {
            ReportedUserId = Guid.NewGuid(),
            Category = ReportCategory.Other,
            Screenshot = fileMock.Object
        });

        Assert.True(result.Succeeded);
        _supportRepoMock.Verify(r => r.AddReportAsync(It.Is<Report>(rep =>
            rep.ScreenshotPath == "Uploads/img/Support/report.png")), Times.Once);
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

        var result = await _service.UpdateReportStatusAsync(reportId, "Resolved", "Investigating report", Guid.NewGuid(), callerIsAdmin: true);

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.NotFound, result.StatusCode);
    }

    [Fact]
    public async Task UpdateReportStatusAsync_ValidReport_UpdatesStatus()
    {
        var reportId = Guid.NewGuid();
        var report = new Report { Id = reportId, Status = "Open" };
        _supportRepoMock.Setup(r => r.GetReportByIdAsync(reportId)).ReturnsAsync(report);

        var result = await _service.UpdateReportStatusAsync(reportId, "Resolved", "Verified evidence", Guid.NewGuid(), callerIsAdmin: true);

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
        _userManagerMock.Setup(m => m.GetRolesAsync(reportedUser)).ReturnsAsync(new List<string> { AppRoles.User });

        var dto = new TakeReportActionDto
        {
            Action = AdminReportAction.WarnUser,
            AdminNote = "Stop spamming rooms",
            Reason = "First spam offense"
        };

        var result = await _service.TakeActionOnReportAsync(reportId, dto, Guid.NewGuid(), callerIsAdmin: true);

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
        _userManagerMock.Setup(m => m.GetRolesAsync(reportedUser)).ReturnsAsync(new List<string> { AppRoles.User });
        _userManagerMock.Setup(m => m.UpdateAsync(reportedUser)).ReturnsAsync(IdentityResult.Success);
        _userManagerMock.Setup(m => m.UpdateSecurityStampAsync(reportedUser)).ReturnsAsync(IdentityResult.Success);

        var dto = new TakeReportActionDto
        {
            Action = AdminReportAction.BanUser,
            Reason = "Severe harassment violation"
        };

        var result = await _service.TakeActionOnReportAsync(reportId, dto, Guid.NewGuid(), callerIsAdmin: true);

        Assert.True(result.Succeeded);
        Assert.Equal(UserStatus.Banned, reportedUser.Status);
        Assert.Null(reportedUser.RefreshToken);
        Assert.Null(reportedUser.FcmToken);

        _userManagerMock.Verify(m => m.UpdateSecurityStampAsync(reportedUser), Times.Once);
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

        var result = await _service.CloseChatAsync(chatId, "admin-1", callerIsAdmin: false);

        Assert.True(result.Succeeded);
        Assert.Equal(SupportChatStatus.Closed, chat.Status);
        _supportRepoMock.Verify(r => r.UpdateChatAsync(chat), Times.Once);
    }

    [Fact]
    public async Task GetAdminTicketsAsync_MapsUserAnonymousAndDeleted_AndNeverAPathOrUrl()
    {
        var user = new ApplicationUser { Id = Guid.NewGuid(), FirstName = "Sara", LastName = "Ali", Email = "sara@test.local" };
        var owned = new SupportTicket { Id = Guid.NewGuid(), UserId = user.Id, User = user, Type = SupportTicketType.TechnicalProblem, Message = "Crash", ScreenshotPath = "Uploads/img/Support/t.png" };
        var anonymous = new SupportTicket { Id = Guid.NewGuid(), UserId = null, Type = SupportTicketType.GeneralQuestion, Message = "Hi", ContactEmail = "a@b.c" };
        var deleted = new SupportTicket { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), User = null, Message = "Old", StatusCode = SupportTicketStatus.Resolved };
        _supportRepoMock.Setup(r => r.GetPagedTicketsAsync(null, null, 1, 10, false))
            .ReturnsAsync((3, new List<SupportTicket> { owned, anonymous, deleted }));

        var result = await _service.GetAdminTicketsAsync(null, null, 1, 10, callerIsAdmin: true);

        var items = result.Data!.ToList();
        Assert.True(result.Succeeded);
        Assert.Equal(3, result.TotalCount);
        Assert.Equal("Sara Ali", items[0].UserName);
        Assert.Equal("sara@test.local", items[0].UserEmail);
        Assert.True(items[0].HasScreenshot);
        Assert.Equal("Anonymous", items[1].UserName);
        Assert.Equal("a@b.c", items[1].ContactEmail);
        Assert.False(items[1].HasScreenshot);
        Assert.Equal("Deleted user", items[2].UserName);
        Assert.Equal(SupportTicketStatus.Resolved, items[2].Status);
        Assert.Empty(FileUrls.Calls);
    }

    [Theory]
    [InlineData(SupportTicketType.GeneralQuestion, "General question")]
    [InlineData(SupportTicketType.TechnicalProblem, "Technical problem")]
    [InlineData(SupportTicketType.Other, "Other")]
    [InlineData((SupportTicketType)99, "99")]
    public async Task GetAdminTicketsAsync_KnownAndUndefinedTypes_MapsExpectedTypeName(
        SupportTicketType type, string expectedTypeName)
    {
        var ticket = new SupportTicket
        {
            Id = Guid.NewGuid(),
            Type = type,
            Message = "Need help"
        };
        _supportRepoMock.Setup(r => r.GetPagedTicketsAsync(null, null, 1, 10, false))
            .ReturnsAsync((1, new List<SupportTicket> { ticket }));

        var result = await _service.GetAdminTicketsAsync(null, null, 1, 10, callerIsAdmin: true);

        Assert.True(result.Succeeded);
        var item = Assert.Single(result.Data!);
        Assert.Equal(type, item.Type);
        Assert.Equal(expectedTypeName, item.TypeName);
    }

    [Theory]
    [InlineData("Uploads/img/p.png", "https://api.example.com/Uploads/img/p.png")]
    [InlineData("/Uploads/p.png", "https://api.example.com/Uploads/p.png")]
    [InlineData(@"Uploads\img\p.png", "https://api.example.com/Uploads/img/p.png")]
    [InlineData("https://cdn.example.com/p.png", "https://cdn.example.com/p.png")]
    [InlineData("HTTP://cdn.example.com/p.png", "HTTP://cdn.example.com/p.png")]
    [InlineData(null, null)]
    [InlineData("   ", null)]
    public async Task GetAdminTicketsAsync_UserWithProfilePicturePath_ResolvesExpectedUrl(
        string? profilePicturePath, string? expectedUrl)
    {
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            FirstName = "Sara",
            LastName = "Ali",
            ProfilePicturePath = profilePicturePath
        };
        var ticket = new SupportTicket
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            User = user,
            Message = "Need help"
        };
        _supportRepoMock.Setup(r => r.GetPagedTicketsAsync(null, null, 1, 10, false))
            .ReturnsAsync((1, new List<SupportTicket> { ticket }));

        var result = await _service.GetAdminTicketsAsync(null, null, 1, 10, callerIsAdmin: true);

        Assert.True(result.Succeeded);
        var item = Assert.Single(result.Data!);
        Assert.Equal(expectedUrl, item.UserProfilePicture);
    }

    [Fact]
    public async Task GetAdminTicketsAsync_AnonymousAndDeletedUserTickets_LeaveProfilePictureNullAndHideScreenshotPath()
    {
        const string anonymousScreenshotPath = "Uploads/img/Support/anon-secret.png";
        const string deletedUserScreenshotPath = "Uploads/img/Support/deleted-secret.png";

        var anonymousWithScreenshot = new SupportTicket
        {
            Id = Guid.NewGuid(),
            UserId = null,
            User = null,
            ContactEmail = "anon@test.local",
            Message = "Anonymous report",
            ScreenshotPath = anonymousScreenshotPath
        };
        var deletedWithScreenshot = new SupportTicket
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            User = null,
            Message = "Deleted user report",
            ScreenshotPath = deletedUserScreenshotPath
        };
        var deletedWithoutScreenshot = new SupportTicket
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            User = null,
            Message = "Deleted user without shot",
            ScreenshotPath = null
        };
        _supportRepoMock.Setup(r => r.GetPagedTicketsAsync(null, null, 1, 10, false))
            .ReturnsAsync((3, new List<SupportTicket> { anonymousWithScreenshot, deletedWithScreenshot, deletedWithoutScreenshot }));

        var result = await _service.GetAdminTicketsAsync(null, null, 1, 10, callerIsAdmin: true);

        Assert.True(result.Succeeded);
        var items = result.Data!.ToList();
        Assert.Equal(3, items.Count);

        Assert.Null(items[0].UserProfilePicture);
        Assert.True(items[0].HasScreenshot);

        Assert.Null(items[1].UserProfilePicture);
        Assert.True(items[1].HasScreenshot);

        Assert.Null(items[2].UserProfilePicture);
        Assert.False(items[2].HasScreenshot);

        var stringProperties = typeof(SupportTicketDetailsDto)
            .GetProperties()
            .Where(p => p.PropertyType == typeof(string))
            .ToList();

        Assert.All(items, dto =>
        {
            foreach (var property in stringProperties)
            {
                var value = (string?)property.GetValue(dto);
                if (value is not null)
                {
                    Assert.DoesNotContain(anonymousScreenshotPath, value, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain(deletedUserScreenshotPath, value, StringComparison.OrdinalIgnoreCase);
                }
            }
        });
        Assert.Empty(FileUrls.Calls);
    }

    [Fact]
    public async Task UpdateTicketStatusAsync_Resolve_SyncsStringTypedAndResolvedAt()
    {
        var ticket = new SupportTicket { Id = Guid.NewGuid(), Status = "Open" };
        _supportRepoMock.Setup(r => r.GetTicketByIdAsync(ticket.Id)).ReturnsAsync(ticket);

        var result = await _service.UpdateTicketStatusAsync(ticket.Id, SupportTicketStatus.Resolved, "Issue fixed", Guid.NewGuid(), callerIsAdmin: true);

        Assert.True(result.Succeeded);
        Assert.Equal("Resolved", ticket.Status);
        Assert.Equal(SupportTicketStatus.Resolved, ticket.StatusCode);
        Assert.NotNull(ticket.ResolvedAt);
        Assert.NotNull(ticket.UpdatedAt);
        _supportRepoMock.Verify(r => r.UpdateTicketAsync(ticket), Times.Once);
    }

    [Fact]
    public async Task UpdateTicketStatusAsync_Reopen_ClearsResolvedAt()
    {
        var ticket = new SupportTicket { Id = Guid.NewGuid(), Status = "Resolved", StatusCode = SupportTicketStatus.Resolved, ResolvedAt = DateTime.UtcNow };
        _supportRepoMock.Setup(r => r.GetTicketByIdAsync(ticket.Id)).ReturnsAsync(ticket);

        var result = await _service.UpdateTicketStatusAsync(ticket.Id, SupportTicketStatus.Open, "User replied with more details", Guid.NewGuid(), callerIsAdmin: true);

        Assert.True(result.Succeeded);
        Assert.Equal("Open", ticket.Status);
        Assert.Equal(SupportTicketStatus.Open, ticket.StatusCode);
        Assert.Null(ticket.ResolvedAt);
    }

    [Fact]
    public async Task UpdateTicketStatusAsync_UnknownTicket_ReturnsNotFound()
    {
        _supportRepoMock.Setup(r => r.GetTicketByIdAsync(It.IsAny<Guid>())).ReturnsAsync((SupportTicket?)null);

        var result = await _service.UpdateTicketStatusAsync(Guid.NewGuid(), SupportTicketStatus.Resolved, "Closing ticket", Guid.NewGuid(), callerIsAdmin: true);

        Assert.Equal(HttpStatusCode.NotFound, result.StatusCode);
        _supportRepoMock.Verify(r => r.UpdateTicketAsync(It.IsAny<SupportTicket>()), Times.Never);
    }
}
