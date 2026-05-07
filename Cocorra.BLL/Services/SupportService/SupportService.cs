using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Cocorra.BLL.Base;
using Cocorra.BLL.Services.RealTimeNotifier;
using Cocorra.BLL.Services.Upload;
using Cocorra.DAL.DTOS.ReportDto;
using Cocorra.DAL.DTOS.SupportDto;
using Cocorra.DAL.DTOS.SupportChatDto;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;
using Cocorra.DAL.Repository.NotificationRepository;
using Microsoft.EntityFrameworkCore;
using Cocorra.DAL.Repository.SupportRepository;
using Microsoft.AspNetCore.Identity;
using Cocorra.BLL.Services.NotificationService;

namespace Cocorra.BLL.Services.SupportService
{
    public class SupportService : ResponseHandler, ISupportService
    {
        private readonly ISupportRepository _supportRepo;
        private readonly IUploadImage _uploadImage;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly INotificationRepository _notificationRepo;
        private readonly IRealTimeNotifier _realTimeNotifier;
        private readonly IPushNotificationService _pushService;

        public SupportService(
            ISupportRepository supportRepo,
            IUploadImage uploadImage,
            UserManager<ApplicationUser> userManager,
            INotificationRepository notificationRepo,
            IRealTimeNotifier realTimeNotifier,
            IPushNotificationService pushService)
        {
            _supportRepo = supportRepo;
            _uploadImage = uploadImage;
            _userManager = userManager;
            _notificationRepo = notificationRepo;
            _realTimeNotifier = realTimeNotifier;
            _pushService = pushService;
        }

        public async Task<Response<string>> SubmitTicketAsync(Guid? userId, SubmitSupportTicketDto dto)
        {
            string? screenshotPath = null;
            if (dto.Screenshot != null && dto.Screenshot.Length > 0)
            {
                screenshotPath = await _uploadImage.SaveImageAsync(dto.Screenshot);
            }

            var ticket = new SupportTicket
            {
                UserId = userId,
                Type = dto.Type,
                Message = dto.Message,
                ContactEmail = dto.ContactEmail,
                ScreenshotPath = screenshotPath,
                Status = "Open"
            };

            await _supportRepo.AddTicketAsync(ticket);

            return Success("Support ticket submitted successfully.");
        }

        public async Task<Response<string>> SubmitReportAsync(Guid reporterId, SubmitReportDto dto)
        {
            if (dto.ReportedUserId == null && dto.ReportedRoomId == null)
                return BadRequest<string>("You must specify a user or a room to report.");

            string? screenshotPath = null;
            if (dto.Screenshot != null && dto.Screenshot.Length > 0)
            {
                screenshotPath = await _uploadImage.SaveImageAsync(dto.Screenshot);
            }

            var report = new Report
            {
                ReporterId = reporterId,
                ReportedUserId = dto.ReportedUserId,
                ReportedRoomId = dto.ReportedRoomId,
                Category = dto.Category,
                Description = dto.Description,
                ScreenshotPath = screenshotPath,
                Status = "Open"
            };

            await _supportRepo.AddReportAsync(report);

            return Success("Report submitted successfully.");
        }

        public async Task<Response<List<ReportDetailsDto>>> GetFilteredReportsAsync(ReportCategory? category, string? status)
        {
            var reports = await _supportRepo.GetFilteredReportsAsync(category, status);

            var result = reports.Select(r => new ReportDetailsDto
            {
                Id = r.Id,
                ReporterId = r.ReporterId,
                ReporterName = r.Reporter != null
                    ? $"{r.Reporter.FirstName} {r.Reporter.LastName}"
                    : "Unknown",
                ReportedUserId = r.ReportedUserId,
                ReportedUserName = r.ReportedUser != null
                    ? $"{r.ReportedUser.FirstName} {r.ReportedUser.LastName}"
                    : null,
                ReportedRoomId = r.ReportedRoomId,
                ReportedRoomName = r.ReportedRoom?.RoomTitle,
                ReportedRoomHostName = r.ReportedRoom?.Host != null
                    ? $"{r.ReportedRoom.Host.FirstName} {r.ReportedRoom.Host.LastName}"
                    : null,
                ReportedRoomCreatedAt = r.ReportedRoom?.CreatedAt,
                Category = (int)r.Category,
                CategoryName = r.Category.ToString(),
                Description = r.Description,
                ScreenshotPath = r.ScreenshotPath,
                Status = r.Status,
                CreatedAt = r.CreatedAt
            }).ToList();

            return Success(result);
        }

        public async Task<Response<string>> UpdateReportStatusAsync(Guid reportId, string newStatus)
        {
            var report = await _supportRepo.GetReportByIdAsync(reportId);
            if (report == null) return NotFound<string>("Report not found.");

            report.Status = newStatus;
            report.UpdatedAt = DateTime.UtcNow;

            await _supportRepo.UpdateReportAsync(report);

            return Success($"Report status updated to '{newStatus}'.");
        }

        public async Task<Response<string>> TakeActionOnReportAsync(Guid reportId, TakeReportActionDto dto)
        {
            var report = await _supportRepo.GetReportByIdAsync(reportId);
            if (report == null) return NotFound<string>("Report not found.");

            switch (dto.Action)
            {
                case AdminReportAction.WarnUser:
                    if (report.ReportedUserId == null)
                        return BadRequest<string>("This report has no reported user to warn.");

                    var warning = new Notification
                    {
                        UserId = report.ReportedUserId.Value,
                        Title = "Admin Warning",
                        Message = dto.AdminNote ?? "You have received a warning from the admin team for violating community guidelines.",
                        Type = NotificationType.AdminWarning,
                        ReferenceId = report.Id,
                        CreatedAt = DateTime.UtcNow,
                        IsRead = false
                    };
                    await _notificationRepo.AddAsync(warning);

                    var warnUser = await _userManager.FindByIdAsync(report.ReportedUserId.Value.ToString());
                    if (!string.IsNullOrEmpty(warnUser?.FcmToken))
                    {
                        var data = new Dictionary<string, string> { { "type", "report" }, { "reportId", report.Id.ToString() } };
                        try { await _pushService.SendPushNotificationAsync(warnUser.FcmToken, warning.Title, warning.Message, data); } catch { }
                    }

                    report.Status = "Resolved";
                    break;

                case AdminReportAction.Mute24h:
                    if (report.ReportedUserId == null)
                        return BadRequest<string>("This report has no reported user to mute.");

                    var muteUser = await _userManager.FindByIdAsync(report.ReportedUserId.Value.ToString());
                    if (muteUser == null) return NotFound<string>("Reported user not found.");

                    await _userManager.SetLockoutEnabledAsync(muteUser, true);
                    await _userManager.SetLockoutEndDateAsync(muteUser, DateTimeOffset.UtcNow.AddHours(24));

                    // Force kick from any active room via SignalR
                    await _realTimeNotifier.ForceLogoutAsync(
                        report.ReportedUserId.Value,
                        "Your account has been temporarily suspended for 24 hours.");

                    if (!string.IsNullOrEmpty(muteUser?.FcmToken))
                    {
                        var data = new Dictionary<string, string> { { "type", "account_locked" } };
                        try { await _pushService.SendPushNotificationAsync(muteUser.FcmToken, "Account Locked", "Your account has been temporarily suspended for 24 hours.", data); } catch { }
                    }

                    report.Status = "Resolved";
                    break;

                case AdminReportAction.BanUser:
                    if (report.ReportedUserId == null)
                        return BadRequest<string>("This report has no reported user to ban.");

                    var banUser = await _userManager.FindByIdAsync(report.ReportedUserId.Value.ToString());
                    if (banUser == null) return NotFound<string>("Reported user not found.");

                    await _userManager.SetLockoutEnabledAsync(banUser, true);
                    await _userManager.SetLockoutEndDateAsync(banUser, DateTimeOffset.UtcNow.AddYears(100));

                    // Force kick from any active room via SignalR
                    await _realTimeNotifier.ForceLogoutAsync(
                        report.ReportedUserId.Value,
                        "Your account has been permanently banned.");

                    if (!string.IsNullOrEmpty(banUser?.FcmToken))
                    {
                        var data = new Dictionary<string, string> { { "type", "account_locked" } };
                        try { await _pushService.SendPushNotificationAsync(banUser.FcmToken, "Account Locked", "Your account has been permanently banned.", data); } catch { }
                    }

                    report.Status = "Resolved";
                    break;

                case AdminReportAction.RejectReport:
                    report.Status = "Rejected";
                    break;

                default:
                    return BadRequest<string>("Invalid action.");
            }

            report.UpdatedAt = DateTime.UtcNow;
            await _supportRepo.UpdateReportAsync(report);

            return Success($"Action '{dto.Action}' applied successfully.");
        }

        // --- Chat Support Methods ---
        
        public async Task<Response<SendMessageResultDto>> SendMessageAsync(string userId, SendMessageDto dto)
        {
            var chat = await _supportRepo.GetUserOpenChatAsync(userId);
            bool isNew = false;

            if (chat == null)
            {
                chat = new SupportChat
                {
                    UserId = userId,
                    Status = SupportChatStatus.Pending,
                    CreatedAt = DateTime.UtcNow
                };
                await _supportRepo.AddChatAsync(chat);
                isNew = true;
            }

            if (chat.Status == SupportChatStatus.Pending)
            {
                var pendingMessages = await _supportRepo.GetPendingUserMessageCountAsync(chat.Id);
                if (pendingMessages >= 3)
                {
                    return BadRequest<SendMessageResultDto>("You have reached the maximum messages. Please wait for an admin to reply.");
                }
            }

            var message = new SupportMessage
            {
                SupportChatId = chat.Id,
                SenderId = userId,
                Content = dto.Content,
                // SECURITY: IsFromAdmin is server-determined. Never accept from client input.
                IsFromAdmin = false,
                CreatedAt = DateTime.UtcNow
            };

            await _supportRepo.AddMessageAsync(message);

            var messageDto = new SupportMessageDto
            {
                Id = message.Id,
                ChatId = chat.Id,
                SenderId = message.SenderId,
                Content = message.Content,
                IsFromAdmin = message.IsFromAdmin,
                CreatedAt = message.CreatedAt
            };

            var resultDto = new SendMessageResultDto
            {
                ChatId = chat.Id,
                Message = messageDto,
                IsNewChat = isNew
            };

            return Success(resultDto);
        }

        public async Task<Response<string>> ClaimChatAsync(Guid chatId, string adminId)
        {
            var chat = await _supportRepo.GetChatByIdAsync(chatId);
            if (chat == null) return NotFound<string>("Chat not found.");

            if (chat.Status != SupportChatStatus.Pending)
                return BadRequest<string>("Chat is not pending.");

            chat.AdminId = adminId;
            chat.Status = SupportChatStatus.Active;

            try
            {
                await _supportRepo.UpdateChatAsync(chat);
            }
            catch (DbUpdateConcurrencyException)
            {
                return BadRequest<string>("This chat has already been claimed by another admin.");
            }

            return Success("Chat claimed successfully.");
        }

        public async Task<Response<AdminReplyResultDto>> AdminReplyAsync(Guid chatId, string adminId, SendMessageDto dto)
        {
            var chat = await _supportRepo.GetChatByIdAsync(chatId);
            if (chat == null) return NotFound<AdminReplyResultDto>("Chat not found.");

            if (chat.AdminId != adminId)
                return BadRequest<AdminReplyResultDto>("You are not assigned to this chat.");

            if (chat.Status != SupportChatStatus.Active)
                return BadRequest<AdminReplyResultDto>("Chat is not active.");

            var message = new SupportMessage
            {
                SupportChatId = chat.Id,
                SenderId = adminId,
                Content = dto.Content,
                // SECURITY: IsFromAdmin is server-determined. Never accept from client input.
                IsFromAdmin = true,
                CreatedAt = DateTime.UtcNow
            };

            await _supportRepo.AddMessageAsync(message);

            var messageDto = new SupportMessageDto
            {
                Id = message.Id,
                ChatId = chat.Id,
                SenderId = message.SenderId,
                Content = message.Content,
                IsFromAdmin = message.IsFromAdmin,
                CreatedAt = message.CreatedAt
            };

            var resultDto = new AdminReplyResultDto
            {
                Message = messageDto,
                UserId = chat.UserId
            };

            return Success(resultDto);
        }

        public async Task<Response<string>> CloseChatAsync(Guid chatId, string adminId)
        {
            var chat = await _supportRepo.GetChatByIdAsync(chatId);
            if (chat == null) return NotFound<string>("Chat not found.");

            if (chat.Status == SupportChatStatus.Closed)
                return BadRequest<string>("Chat is already closed.");

            if (chat.AdminId != adminId)
                return BadRequest<string>("You are not assigned to this chat.");

            chat.Status = SupportChatStatus.Closed;
            chat.ClosedAt = DateTime.UtcNow;

            await _supportRepo.UpdateChatAsync(chat);

            return Success("Chat closed successfully.");
        }

        public async Task<Response<List<PendingChatDto>>> GetPendingChatsAsync(int pageNumber, int pageSize)
        {
            var totalCount = await _supportRepo.GetPendingChatsCountAsync();
            var chats = await _supportRepo.GetPendingChatsAsync(pageNumber, pageSize);
            var dtos = chats.Select(c => new PendingChatDto
            {
                Id = c.Id,
                UserId = c.UserId,
                Status = c.Status,
                CreatedAt = c.CreatedAt,
                UnreadMessageCount = c.Messages.Count(m => !m.IsFromAdmin),
                LastMessageContent = c.Messages.OrderByDescending(m => m.CreatedAt).FirstOrDefault()?.Content ?? ""
            }).ToList();

            return Success(dtos, meta: new
            {
                currentPage = pageNumber,
                pageSize,
                totalCount,
                totalPages = (int)Math.Ceiling(totalCount / (double)pageSize)
            });
        }

        public async Task<Response<List<SupportChatDetailsDto>>> GetAdminActiveChatsAsync(string adminId, int pageNumber, int pageSize)
        {
            var totalCount = await _supportRepo.GetAdminActiveChatsCountAsync(adminId);
            var chats = await _supportRepo.GetAdminActiveChatsAsync(adminId, pageNumber, pageSize);
            var dtos = chats.Select(MapToDetailsDto).ToList();
            return Success(dtos, meta: new
            {
                currentPage = pageNumber,
                pageSize,
                totalCount,
                totalPages = (int)Math.Ceiling(totalCount / (double)pageSize)
            });
        }

        public async Task<Response<List<SupportChatDetailsDto>>> GetUserChatHistoryAsync(string userId, int pageNumber, int pageSize)
        {
            var totalCount = await _supportRepo.GetUserChatHistoryCountAsync(userId);
            var chats = await _supportRepo.GetUserChatHistoryAsync(userId, pageNumber, pageSize);
            var dtos = chats.Select(MapToDetailsDto).ToList();
            return Success(dtos, meta: new
            {
                currentPage = pageNumber,
                pageSize,
                totalCount,
                totalPages = (int)Math.Ceiling(totalCount / (double)pageSize)
            });
        }

        public async Task<Response<SupportChatDetailsDto>> GetUserOpenChatAsync(string userId)
        {
            var chat = await _supportRepo.GetUserOpenChatAsync(userId);
            if (chat == null) return NotFound<SupportChatDetailsDto>("No active support chat found.");

            return Success(MapToDetailsDto(chat));
        }

        private SupportChatDetailsDto MapToDetailsDto(SupportChat chat)
        {
            return new SupportChatDetailsDto
            {
                Id = chat.Id,
                UserId = chat.UserId,
                AdminId = chat.AdminId,
                Status = chat.Status,
                CreatedAt = chat.CreatedAt,
                ClosedAt = chat.ClosedAt,
                Messages = chat.Messages.Select(m => new SupportMessageDto
                {
                    Id = m.Id,
                    ChatId = chat.Id,
                    SenderId = m.SenderId,
                    Content = m.Content,
                    IsFromAdmin = m.IsFromAdmin,
                    CreatedAt = m.CreatedAt
                }).ToList()
            };
        }
    }
}
