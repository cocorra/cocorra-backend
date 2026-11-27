using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Cocorra.BLL.Base;
using Cocorra.BLL.Services.AuditLogs;
using Cocorra.BLL.Services.RealTimeNotifier;
using Cocorra.BLL.Services.Upload;
using Cocorra.BLL.Services.Storage;
using Cocorra.DAL.DTOS.ReportDto;
using Cocorra.DAL.DTOS.SupportDto;
using Cocorra.DAL.DTOS.SupportChatDto;
using Cocorra.DAL.DTOS.StorageDto;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;
using Cocorra.DAL.Repository.NotificationRepository;
using Microsoft.EntityFrameworkCore;
using Cocorra.DAL.Repository.SupportRepository;
using Microsoft.AspNetCore.Identity;
using Cocorra.BLL.Services.NotificationService;
using Cocorra.BLL.Services.EventTracking;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Cocorra.BLL.Services.SupportService
{
    public class SupportService : ResponseHandler, ISupportService
    {
        private const int MinReasonLength = 3;
        private const int MaxReasonLength = 1000;
        private const string InvalidReasonMessage = "A reason between 3 and 1000 characters is required.";

        private readonly ISupportRepository _supportRepo;
        private readonly IUploadImage _uploadImage;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly INotificationRepository _notificationRepo;
        private readonly IRealTimeNotifier _realTimeNotifier;
        private readonly IPushNotificationService _pushService;
        private readonly IEventTracker _eventTracker;
        private readonly ILogger<SupportService> _logger;
        private readonly IPrivateFileUrlIssuer _fileUrls;
        private readonly IAuditLogService? _auditLog;
        private readonly string _baseUrl;

        public SupportService(
            ISupportRepository supportRepo,
            IUploadImage uploadImage,
            UserManager<ApplicationUser> userManager,
            INotificationRepository notificationRepo,
            IRealTimeNotifier realTimeNotifier,
            IPushNotificationService pushService,
            IEventTracker eventTracker,
            ILogger<SupportService> logger,
            IPrivateFileUrlIssuer fileUrls,
            IConfiguration configuration,
            // Optional so existing direct callers keep compiling; DI always supplies IAuditLogService.
            IAuditLogService? auditLog = null)
        {
            _supportRepo = supportRepo;
            _uploadImage = uploadImage;
            _userManager = userManager;
            _notificationRepo = notificationRepo;
            _realTimeNotifier = realTimeNotifier;
            _pushService = pushService;
            _eventTracker = eventTracker;
            _logger = logger;
            _fileUrls = fileUrls;
            _auditLog = auditLog;
            _baseUrl = configuration["AppSettings:BaseUrl"]?.TrimEnd('/') ?? "";
        }

        private Task RecordAuditAsync(AuditEntry entry) =>
            _auditLog is null ? Task.CompletedTask : _auditLog.RecordAsync(entry);

        private static bool TryNormalizeReason(string? reason, out string normalized)
        {
            normalized = (reason ?? string.Empty).Trim();
            return normalized.Length >= MinReasonLength && normalized.Length <= MaxReasonLength;
        }

        private static bool TryParseReportStatus(string raw, out ReportStatus status)
        {
            if (string.IsNullOrWhiteSpace(raw) || char.IsDigit(raw[0]) || raw[0] is '+' or '-')
            {
                status = default;
                return false;
            }

            return Enum.TryParse(raw, ignoreCase: true, out status) && Enum.IsDefined(status);
        }

        private async Task<bool> IsUserAdminAsync(Guid? userId, ApplicationUser? loadedUser = null)
        {
            if (!userId.HasValue) return false;

            var user = loadedUser ?? await _userManager.FindByIdAsync(userId.Value.ToString());
            if (user is null) return false;

            var roles = await _userManager.GetRolesAsync(user) ?? Array.Empty<string>();
            return roles.Any(r => string.Equals(r, AppRoles.Admin, StringComparison.OrdinalIgnoreCase));
        }

        private async Task<bool> ReportInvolvesAdminAsync(Report report) =>
            await IsUserAdminAsync(report.ReporterId, report.Reporter)
            || await IsUserAdminAsync(report.ReportedUserId, report.ReportedUser);

        private async Task<bool> IsActiveSupportStaffAsync(string staffUserId)
        {
            var staffUser = await _userManager.FindByIdAsync(staffUserId);
            if (staffUser is null || staffUser.Status != UserStatus.Active)
                return false;

            if (await _userManager.IsLockedOutAsync(staffUser))
                return false;

            var roles = await _userManager.GetRolesAsync(staffUser) ?? Array.Empty<string>();
            return roles.Any(r =>
                string.Equals(r, AppRoles.Admin, StringComparison.OrdinalIgnoreCase)
                || string.Equals(r, AppRoles.Moderator, StringComparison.OrdinalIgnoreCase));
        }

        private static Guid? ParseGuidOrNull(string? raw) =>
            Guid.TryParse(raw, out var parsed) ? parsed : null;

        private static string FormatUserDisplay(ApplicationUser? user, string fallback)
        {
            if (user is null) return fallback;
            if (!string.IsNullOrWhiteSpace(user.Email)) return user.Email;
            var fullName = $"{user.FirstName} {user.LastName}".Trim();
            return string.IsNullOrWhiteSpace(fullName) ? fallback : fullName;
        }

        private async Task<string> ResolveUserDisplayAsync(Guid? userId, string fallback)
        {
            if (!userId.HasValue) return fallback;
            var user = await _userManager.FindByIdAsync(userId.Value.ToString());
            return FormatUserDisplay(user, fallback);
        }

        public async Task<Response<string>> SubmitTicketAsync(Guid? userId, SubmitSupportTicketDto dto)
        {
            string? screenshotPath = null;
            if (dto.Screenshot != null && dto.Screenshot.Length > 0)
            {
                screenshotPath = await _uploadImage.SavePrivateImageAsync(dto.Screenshot, "Support");
                if (screenshotPath.StartsWith("Error"))
                    return BadRequest<string>(screenshotPath);
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
                screenshotPath = await _uploadImage.SavePrivateImageAsync(dto.Screenshot, "Support");
                if (screenshotPath.StartsWith("Error"))
                    return BadRequest<string>(screenshotPath);
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

            _eventTracker.Track(EventTypes.UserReported, reporterId, new { reportedUserId = dto.ReportedUserId, reportedRoomId = dto.ReportedRoomId, category = dto.Category.ToString(), description = dto.Description });

            return Success("Report submitted successfully.");
        }

        public async Task<PagedResponse<ReportDetailsDto>> GetFilteredReportsAsync(
            ReportCategory? category, string? status, int pageNumber = 1, int pageSize = 20, bool callerIsAdmin = false)
        {
            pageNumber = Math.Max(1, pageNumber);
            pageSize = Math.Clamp(pageSize, 1, 100);

            var (totalCount, reports) = await _supportRepo.GetPagedReportsAsync(
                category, status, pageNumber, pageSize, excludeAdmins: !callerIsAdmin);
            var items = reports.Select(MapToReportDetailsDto).ToList();

            return Paginated(items, totalCount, pageNumber, pageSize);
        }

        public async Task<Response<ReportDetailsDto>> GetReportByIdAsync(Guid reportId, Guid callerId, bool callerIsAdmin)
        {
            var report = await _supportRepo.GetReportDetailsByIdAsync(reportId);
            if (report == null || (!callerIsAdmin && await ReportInvolvesAdminAsync(report)))
                return NotFound<ReportDetailsDto>("Report not found.");

            await RecordAuditAsync(new AuditEntry(
                AuditActions.ReportViewed,
                callerId,
                report.ReportedUserId,
                FormatUserDisplay(report.ReportedUser, $"report {report.Id}"),
                new { category = report.Category.ToString(), status = report.Status })
            {
                Category = AuditCategories.Report,
                EntityType = AuditEntityTypes.Report,
                EntityId = report.Id.ToString()
            });

            return Success(MapToReportDetailsDto(report));
        }

        private ReportDetailsDto MapToReportDetailsDto(Report report) => new()
        {
            Id = report.Id,
            ReporterId = report.ReporterId,
            ReporterName = report.Reporter != null
                ? $"{report.Reporter.FirstName} {report.Reporter.LastName}"
                : "Unknown",
            ReportedUserId = report.ReportedUserId,
            ReportedUserName = report.ReportedUser != null
                ? $"{report.ReportedUser.FirstName} {report.ReportedUser.LastName}"
                : null,
            ReportedRoomId = report.ReportedRoomId,
            ReportedRoomName = report.ReportedRoom?.RoomTitle,
            ReportedRoomHostName = report.ReportedRoom?.Host != null
                ? $"{report.ReportedRoom.Host.FirstName} {report.ReportedRoom.Host.LastName}"
                : null,
            ReportedRoomCreatedAt = report.ReportedRoom?.CreatedAt,
            Category = (int)report.Category,
            CategoryName = report.Category.ToString(),
            Description = report.Description,
            // Never a URL in a list or detail; GetReportScreenshotUrlAsync issues one on demand.
            ScreenshotPath = null,
            HasScreenshot = _fileUrls.HasFile(report.ScreenshotPath),
            Status = report.Status,
            CreatedAt = report.CreatedAt
        };

        public async Task<Response<PrivateFileUrlDto>> GetReportScreenshotUrlAsync(Guid reportId, bool callerIsAdmin)
        {
            var report = await _supportRepo.GetReportByIdAsync(reportId);
            if (report == null || (!callerIsAdmin && await ReportInvolvesAdminAsync(report)))
                return NotFound<PrivateFileUrlDto>("Report not found.");

            return await IssueScreenshotUrlAsync(
                PrivateFileKind.ReportScreenshot,
                new PrivateFileRef(report.Id, report.ScreenshotPath, report.ReportedUserId),
                "Support.ReportScreenshotUrl",
                "This report has no screenshot.");
        }

        public async Task<Response<PrivateFileUrlDto>> GetTicketScreenshotUrlAsync(Guid ticketId, bool callerIsAdmin)
        {
            var ticket = await _supportRepo.GetTicketByIdAsync(ticketId);
            if (ticket == null || (!callerIsAdmin && await IsUserAdminAsync(ticket.UserId, ticket.User)))
                return NotFound<PrivateFileUrlDto>("Ticket not found.");

            return await IssueScreenshotUrlAsync(
                PrivateFileKind.TicketScreenshot,
                new PrivateFileRef(ticket.Id, ticket.ScreenshotPath, ticket.UserId),
                "Support.TicketScreenshotUrl",
                "This ticket has no screenshot.");
        }

        private async Task<Response<PrivateFileUrlDto>> IssueScreenshotUrlAsync(
            PrivateFileKind kind, PrivateFileRef file, string source, string noFileMessage)
        {
            var issued = (await _fileUrls.IssueAsync(kind, new[] { file }, source))[0];
            if (issued == null) return NotFound<PrivateFileUrlDto>(noFileMessage);

            return Success(new PrivateFileUrlDto { Url = issued.Url, ExpiresAtUtc = issued.ExpiresAtUtc });
        }

        public async Task<Response<string>> UpdateReportStatusAsync(
            Guid reportId, string newStatus, string? reason, Guid callerId, bool callerIsAdmin)
        {
            if (!TryNormalizeReason(reason, out var normalizedReason))
                return BadRequest<string>(InvalidReasonMessage);

            var trimmedStatus = (newStatus ?? string.Empty).Trim();
            if (!TryParseReportStatus(trimmedStatus, out var parsedStatus))
                return BadRequest<string>("Invalid report status. Allowed values: Open, Resolved, Rejected.");

            var report = await _supportRepo.GetReportByIdAsync(reportId);
            if (report == null || (!callerIsAdmin && await ReportInvolvesAdminAsync(report)))
                return NotFound<string>("Report not found.");

            var oldStatus = string.IsNullOrWhiteSpace(report.Status) ? report.StatusCode.ToString() : report.Status;
            var canonicalStatus = parsedStatus.ToString();

            if (string.Equals(oldStatus, canonicalStatus, StringComparison.OrdinalIgnoreCase)
                && report.StatusCode == parsedStatus)
                return BadRequest<string>($"Report is already in status '{oldStatus}'.");

            report.Status = canonicalStatus;
            report.StatusCode = parsedStatus;
            report.ResolvedAt = parsedStatus == ReportStatus.Open ? null : report.ResolvedAt ?? DateTime.UtcNow;
            report.UpdatedAt = DateTime.UtcNow;

            await _supportRepo.UpdateReportAsync(report);

            await RecordAuditAsync(new AuditEntry(
                AuditActions.ReportStatusChanged,
                callerId,
                report.ReportedUserId,
                await ResolveUserDisplayAsync(report.ReportedUserId, $"report {report.Id}"),
                new { fromStatus = oldStatus, toStatus = canonicalStatus })
            {
                Category = AuditCategories.Report,
                EntityType = AuditEntityTypes.Report,
                EntityId = report.Id.ToString(),
                FromState = oldStatus,
                ToState = canonicalStatus,
                Reason = normalizedReason
            });

            return Success($"Report status updated to '{canonicalStatus}'.");
        }

        public async Task<PagedResponse<SupportTicketDetailsDto>> GetAdminTicketsAsync(
            SupportTicketType? type, SupportTicketStatus? status, int pageNumber, int pageSize, bool callerIsAdmin)
        {
            var (totalCount, tickets) = await _supportRepo.GetPagedTicketsAsync(
                type, status, pageNumber, pageSize, excludeAdmins: !callerIsAdmin);

            var items = tickets.Select(t => MapToTicketDetailsDto(t, callerIsAdmin)).ToList();

            return Paginated(items, totalCount, pageNumber, pageSize);
        }

        public async Task<Response<SupportTicketDetailsDto>> GetTicketByIdAsync(
            Guid ticketId, Guid callerId, bool callerIsAdmin)
        {
            var ticket = await _supportRepo.GetTicketDetailsByIdAsync(ticketId);
            if (ticket == null || (!callerIsAdmin && await IsUserAdminAsync(ticket.UserId, ticket.User)))
                return NotFound<SupportTicketDetailsDto>("Ticket not found.");

            await RecordAuditAsync(new AuditEntry(
                AuditActions.TicketViewed,
                callerId,
                ticket.UserId,
                FormatUserDisplay(ticket.User, ticket.ContactEmail ?? $"ticket {ticket.Id}"),
                new { type = ticket.Type.ToString(), status = ticket.StatusCode.ToString() })
            {
                Category = AuditCategories.Ticket,
                EntityType = AuditEntityTypes.Ticket,
                EntityId = ticket.Id.ToString()
            });

            return Success(MapToTicketDetailsDto(ticket, callerIsAdmin));
        }

        private SupportTicketDetailsDto MapToTicketDetailsDto(SupportTicket ticket, bool includeEmails) => new()
        {
            Id = ticket.Id,
            UserId = ticket.UserId,
            UserName = ticket.User != null
                ? $"{ticket.User.FirstName} {ticket.User.LastName}"
                : ticket.UserId == null ? "Anonymous" : "Deleted user",
            UserEmail = includeEmails ? ticket.User?.Email : null,
            UserProfilePicture = BuildFullUrl(ticket.User?.ProfilePicturePath),
            ContactEmail = includeEmails ? ticket.ContactEmail : null,
            Type = ticket.Type,
            TypeName = TicketTypeName(ticket.Type),
            Message = ticket.Message,
            // Never a URL in a list or detail; GetTicketScreenshotUrlAsync issues one on demand.
            HasScreenshot = _fileUrls.HasFile(ticket.ScreenshotPath),
            Status = ticket.StatusCode,
            CreatedAt = ticket.CreatedAt,
            UpdatedAt = ticket.UpdatedAt,
            ResolvedAt = ticket.ResolvedAt
        };

        private string? BuildFullUrl(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return path;
            }

            return $"{_baseUrl}/{path.Replace("\\", "/").TrimStart('/')}";
        }

        private static string TicketTypeName(SupportTicketType type) => type switch
        {
            SupportTicketType.GeneralQuestion => "General question",
            SupportTicketType.TechnicalProblem => "Technical problem",
            SupportTicketType.Other => "Other",
            _ => type.ToString()
        };

        public async Task<Response<string>> UpdateTicketStatusAsync(
            Guid ticketId, SupportTicketStatus newStatus, string? reason, Guid callerId, bool callerIsAdmin)
        {
            if (!TryNormalizeReason(reason, out var normalizedReason))
                return BadRequest<string>(InvalidReasonMessage);

            if (!Enum.IsDefined(newStatus))
                return BadRequest<string>("Invalid ticket status.");

            var ticket = await _supportRepo.GetTicketByIdAsync(ticketId);
            if (ticket == null || (!callerIsAdmin && await IsUserAdminAsync(ticket.UserId, ticket.User)))
                return NotFound<string>("Ticket not found.");

            var oldStatus = ticket.StatusCode;
            if (oldStatus == newStatus)
                return BadRequest<string>($"Ticket is already '{newStatus}'.");

            // AN-033: the string and typed columns move together, and ResolvedAt marks when the
            // ticket reached a terminal state (cleared again if it is reopened).
            ticket.Status = newStatus.ToString();
            ticket.StatusCode = newStatus;
            ticket.ResolvedAt = newStatus == SupportTicketStatus.Open ? null : ticket.ResolvedAt ?? DateTime.UtcNow;
            ticket.UpdatedAt = DateTime.UtcNow;

            await _supportRepo.UpdateTicketAsync(ticket);

            await RecordAuditAsync(new AuditEntry(
                AuditActions.TicketStatusChanged,
                callerId,
                ticket.UserId,
                await ResolveUserDisplayAsync(ticket.UserId, ticket.ContactEmail ?? $"ticket {ticket.Id}"),
                new { fromStatus = oldStatus.ToString(), toStatus = newStatus.ToString() })
            {
                Category = AuditCategories.Ticket,
                EntityType = AuditEntityTypes.Ticket,
                EntityId = ticket.Id.ToString(),
                FromState = oldStatus.ToString(),
                ToState = newStatus.ToString(),
                Reason = normalizedReason
            });

            return Success($"Ticket status updated to '{newStatus}'.");
        }

        internal async Task<(ApplicationUser? User, Response<string>? Denial)> AuthorizeReportTargetAsync(
            Guid? reportedUserId, bool callerIsAdmin)
        {
            if (!reportedUserId.HasValue)
            {
                return (null, null);
            }

            var reportedUser = await _userManager.FindByIdAsync(reportedUserId.Value.ToString());
            if (reportedUser is null)
            {
                return (null, null);
            }

            var roles = await _userManager.GetRolesAsync(reportedUser) ?? Array.Empty<string>();
            if (!callerIsAdmin)
            {
                if (AppRoles.AnyProtectedFromModerators(roles))
                {
                    return (reportedUser, Forbidden<string>("Moderators cannot take actions against staff accounts."));
                }

                if (reportedUser.Status is UserStatus.Pending or UserStatus.ReRecord)
                {
                    return (reportedUser, Forbidden<string>("Moderators cannot take actions against accounts in voice verification."));
                }

                return (reportedUser, null);
            }

            if (roles.Any(r => string.Equals(r, AppRoles.Admin, StringComparison.OrdinalIgnoreCase)))
            {
                return (reportedUser, Forbidden<string>("Cannot take disciplinary actions on an Admin account."));
            }

            return (reportedUser, null);
        }

        public async Task<Response<string>> TakeActionOnReportAsync(
            Guid reportId, TakeReportActionDto dto, Guid callerId, bool callerIsAdmin)
        {
            if (!TryNormalizeReason(dto.Reason, out var normalizedReason))
                return BadRequest<string>(InvalidReasonMessage);

            var report = await _supportRepo.GetReportByIdAsync(reportId);
            if (report == null || (!callerIsAdmin && await ReportInvolvesAdminAsync(report)))
                return NotFound<string>("Report not found.");

            if (report.StatusCode != ReportStatus.Open)
                return Conflict<string>("This report has already been handled.");

            var oldStatus = string.IsNullOrWhiteSpace(report.Status) ? report.StatusCode.ToString() : report.Status;

            var (reportedUser, denial) = await AuthorizeReportTargetAsync(report.ReportedUserId, callerIsAdmin);
            if (denial != null)
            {
                return denial;
            }

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

                    if (!string.IsNullOrEmpty(reportedUser?.FcmToken))
                    {
                        var data = new Dictionary<string, string> { { "type", "report" }, { "reportId", report.Id.ToString() } };
                        await _pushService.SendPushNotificationAsync(reportedUser.FcmToken, warning.Title, warning.Message, data);
                    }

                    report.Status = "Resolved";
                    report.StatusCode = ReportStatus.Resolved;
                    break;

                case AdminReportAction.Mute24h:
                    if (report.ReportedUserId == null)
                        return BadRequest<string>("This report has no reported user to mute.");

                    if (reportedUser == null) return NotFound<string>("Reported user not found.");

                    // Held in a variable rather than recomputed: the push below sends this
                    // exact instant to the app, and two calls to UtcNow would not agree.
                    var muteLockoutEnd = DateTimeOffset.UtcNow.AddHours(24);

                    await _userManager.SetLockoutEnabledAsync(reportedUser, true);
                    await _userManager.SetLockoutEndDateAsync(reportedUser, muteLockoutEnd);

                    // Force kick from any active room via SignalR
                    await _realTimeNotifier.ForceLogoutAsync(
                        report.ReportedUserId.Value,
                        "Your account has been temporarily suspended for 24 hours.");

                    var muteNotification = new Notification
                    {
                        UserId = report.ReportedUserId.Value,
                        Title = "Account Locked",
                        Message = "Your account has been temporarily suspended for 24 hours.",
                        Type = NotificationType.AdminWarning,
                        ReferenceId = report.Id,
                        CreatedAt = DateTime.UtcNow,
                        IsRead = false
                    };
                    await _notificationRepo.AddAsync(muteNotification);

                    if (!string.IsNullOrEmpty(reportedUser.FcmToken))
                    {
                        // Data-only, per the account_locked contract: empty title and body are
                        // what stop Firebase attaching a Notification object, so the app handles
                        // the redirect to BannedScreen itself instead of the OS showing a pop-up.
                        // lockout_end is how the app tells a 24h mute from a permanent ban now
                        // that there is no body to read it from; muteNotification is still
                        // persisted, so the wording survives in the notifications list.
                        var data = new Dictionary<string, string>
                        {
                            { "type", "account_locked" },
                            { "lockout_end", muteLockoutEnd.UtcDateTime.ToString("O") }
                        };
                        await _pushService.SendPushNotificationAsync(
                            reportedUser.FcmToken, "", "", data);
                    }

                    report.Status = "Resolved";
                    report.StatusCode = ReportStatus.Resolved;
                    break;

                case AdminReportAction.BanUser:
                    if (report.ReportedUserId == null)
                        return BadRequest<string>("This report has no reported user to ban.");

                    if (reportedUser == null) return NotFound<string>("Reported user not found.");

                    reportedUser.Status = UserStatus.Banned;
                    await _userManager.SetLockoutEnabledAsync(reportedUser, true);
                    await _userManager.SetLockoutEndDateAsync(reportedUser, DateTimeOffset.UtcNow.AddYears(100));

                    // Invalidate refresh token to prevent session resurrection
                    reportedUser.RefreshToken = null;
                    reportedUser.RefreshTokenExpiryTime = DateTime.UtcNow;

                    // Force kick from any active room via SignalR
                    await _realTimeNotifier.ForceLogoutAsync(
                        report.ReportedUserId.Value,
                        "Your account has been permanently banned.");

                    var banNotification = new Notification
                    {
                        UserId = report.ReportedUserId.Value,
                        Title = "Account Locked",
                        Message = "Your account has been permanently banned.",
                        Type = NotificationType.AdminWarning,
                        ReferenceId = report.Id,
                        CreatedAt = DateTime.UtcNow,
                        IsRead = false
                    };
                    await _notificationRepo.AddAsync(banNotification);

                    if (!string.IsNullOrEmpty(reportedUser.FcmToken))
                    {
                        var data = new Dictionary<string, string>
                        {
                            { "type", "account_locked" },
                            { "lockout_end", DateTimeOffset.UtcNow.AddYears(100).UtcDateTime.ToString("O") }
                        };
                        await _pushService.SendPushNotificationAsync(
                            reportedUser.FcmToken, "", "", data);
                    }

                    // Clear FCM token after push is sent
                    reportedUser.FcmToken = null;
                    await _userManager.UpdateAsync(reportedUser);
                    await _userManager.UpdateSecurityStampAsync(reportedUser);
                    await _supportRepo.ReleaseActiveChatsForStaffAsync(reportedUser.Id.ToString());

                    report.Status = "Resolved";
                    report.StatusCode = ReportStatus.Resolved;
                    break;

                case AdminReportAction.RejectReport:
                    report.Status = "Rejected";
                    report.StatusCode = ReportStatus.Rejected;
                    break;

                default:
                    return BadRequest<string>("Invalid action.");
            }

            report.UpdatedAt = DateTime.UtcNow;

            // AN-033: the only record of WHEN the report reached a terminal state. Without it
            // moderation response time is unmeasurable — only the current status exists.
            report.ResolvedAt = DateTime.UtcNow;

            await _supportRepo.UpdateReportAsync(report);

            await RecordAuditAsync(new AuditEntry(
                AuditActions.ReportActionTaken,
                callerId,
                report.ReportedUserId,
                FormatUserDisplay(reportedUser, $"report {report.Id}"),
                new
                {
                    action = dto.Action.ToString(),
                    reportCategory = report.Category.ToString(),
                    outcome = report.StatusCode.ToString(),
                    adminNote = dto.AdminNote
                })
            {
                Category = AuditCategories.Report,
                EntityType = AuditEntityTypes.Report,
                EntityId = report.Id.ToString(),
                FromState = oldStatus,
                ToState = dto.Action.ToString(),
                Reason = normalizedReason
            });

            // AN-034: the enforcement OUTCOME. Report.Status alone cannot distinguish "we
            // banned someone" from "we dismissed it" — both end as a status change — so the
            // action taken is recorded explicitly. Emitted after the write succeeds.
            if (_eventTracker.NewEventEmissionEnabled)
            {
                _eventTracker.Track(EventTypes.ModerationActionTaken, report.ReportedUserId, new
                {
                    reportId = report.Id,
                    reportedRoomId = report.ReportedRoomId,
                    reportCategory = report.Category.ToString(),
                    action = dto.Action.ToString(),
                    outcome = report.StatusCode.ToString(),
                    hoursFromReportToAction = Math.Round((DateTime.UtcNow - report.CreatedAt).TotalHours, 2)
                });
            }

            return Success($"Action '{dto.Action}' applied successfully.");
        }

        // --- Chat Support Methods ---

        /// <summary>
        /// Pushes a support message to the other party, so it still arrives when the app is
        /// backgrounded, locked or terminated — SignalR only reaches a live connection.
        ///
        /// data.type is "support_chat", which is what tells the app to deep-link to the
        /// messaging screen, and chatId lets it open this conversation rather than the list.
        /// The app mutes the notification itself when the chat is already on screen.
        ///
        /// Never throws: a failed push must not fail the message that was already written.
        /// </summary>
        private async Task SendSupportChatPushAsync(
            string? recipientUserId, Guid chatId, string title, string content)
        {
            if (string.IsNullOrEmpty(recipientUserId))
            {
                return;
            }

            try
            {
                var recipient = await _userManager.FindByIdAsync(recipientUserId);
                if (string.IsNullOrEmpty(recipient?.FcmToken))
                {
                    return;
                }

                var data = new Dictionary<string, string>
                {
                    { "type", "support_chat" },
                    { "chatId", chatId.ToString() }
                };

                // Content is client-supplied and may be a JSON envelope; the tray would
                // print it verbatim. See MessagePreview.
                await _pushService.SendPushNotificationAsync(
                    recipient.FcmToken, title, MessagePreview.ForNotificationBody(content), data);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Support chat push failed. ChatId: {ChatId}, RecipientId: {RecipientId}",
                    chatId, recipientUserId);
            }
        }


        public async Task<Response<SendMessageResultDto>> SendMessageAsync(string userId, SendMessageDto dto)
        {
            var chat = await _supportRepo.GetUserOpenChatAsync(userId);
            bool isNew = false;
            bool releasedOrphanedChat = false;

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
            else if (chat.Status == SupportChatStatus.Active)
            {
                if (string.IsNullOrEmpty(chat.AdminId) || !await IsActiveSupportStaffAsync(chat.AdminId))
                {
                    chat.AdminId = null;
                    chat.Status = SupportChatStatus.Pending;
                    await _supportRepo.UpdateChatAsync(chat);
                    releasedOrphanedChat = true;
                }
            }

            if (chat.Status == SupportChatStatus.Pending && !releasedOrphanedChat)
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
                IsNewChat = isNew,
                ClaimedByAdminId = chat.Status == SupportChatStatus.Active && !string.IsNullOrEmpty(chat.AdminId)
                    ? chat.AdminId
                    : null
            };

            // Only an Active chat has a counterpart to push to. While the chat is Pending it
            // belongs to no one, so the admins' dashboard alert (NewPendingChatAlert over
            // SignalR) stays the only signal — pushing to every admin would notify people
            // about a conversation none of them owns yet.
            if (!string.IsNullOrEmpty(chat.AdminId))
            {
                var sender = await _userManager.FindByIdAsync(userId);
                var senderName = sender is null
                    ? "Support request"
                    : $"{sender.FirstName} {sender.LastName}".Trim();

                await SendSupportChatPushAsync(
                    chat.AdminId,
                    chat.Id,
                    string.IsNullOrWhiteSpace(senderName) ? "Support request" : senderName,
                    dto.Content);
            }

            return Success(resultDto);
        }

        public async Task<Response<string>> ClaimChatAsync(Guid chatId, string adminId)
        {
            var chat = await _supportRepo.GetChatByIdAsync(chatId);
            if (chat == null) return NotFound<string>("Chat not found.");

            if (chat.Status != SupportChatStatus.Pending || !string.IsNullOrEmpty(chat.AdminId))
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

            var targetUserId = ParseGuidOrNull(chat.UserId);
            await RecordAuditAsync(new AuditEntry(
                AuditActions.SupportChatClaimed,
                ParseGuidOrNull(adminId),
                targetUserId,
                await ResolveUserDisplayAsync(targetUserId, $"chat {chat.Id}"),
                null)
            {
                Category = AuditCategories.SupportChat,
                EntityType = AuditEntityTypes.SupportChat,
                EntityId = chat.Id.ToString()
            });

            return Success("Chat claimed successfully.");
        }

        public async Task<Response<AdminReplyResultDto>> AdminReplyAsync(Guid chatId, string adminId, SendMessageDto dto, bool callerIsAdmin)
        {
            var chat = await _supportRepo.GetChatByIdAsync(chatId);
            if (chat == null) return NotFound<AdminReplyResultDto>("Chat not found.");

            if (chat.Status != SupportChatStatus.Active)
                return BadRequest<AdminReplyResultDto>("Chat is not active.");

            if (!callerIsAdmin && chat.AdminId != adminId)
                return BadRequest<AdminReplyResultDto>("You are not assigned to this chat.");

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

            var targetUserId = ParseGuidOrNull(chat.UserId);
            // Only record messageId so support message content never enters the audit log.
            await RecordAuditAsync(new AuditEntry(
                AuditActions.SupportChatReplied,
                ParseGuidOrNull(adminId),
                targetUserId,
                await ResolveUserDisplayAsync(targetUserId, $"chat {chat.Id}"),
                new { messageId = message.Id })
            {
                Category = AuditCategories.SupportChat,
                EntityType = AuditEntityTypes.SupportChat,
                EntityId = chat.Id.ToString()
            });

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

            // "Cocorra Support" rather than the admin's name: which admin claimed the chat
            // is not something the user is shown anywhere else in the product.
            await SendSupportChatPushAsync(chat.UserId, chat.Id, "Cocorra Support", dto.Content);

            return Success(resultDto);
        }

        public async Task<Response<string>> CloseChatAsync(Guid chatId, string adminId, bool callerIsAdmin)
        {
            var chat = await _supportRepo.GetChatByIdAsync(chatId);
            if (chat == null) return NotFound<string>("Chat not found.");

            if (chat.Status == SupportChatStatus.Closed)
                return BadRequest<string>("Chat is already closed.");

            if (chat.Status != SupportChatStatus.Active)
                return BadRequest<string>("Chat is not active.");

            if (!callerIsAdmin && chat.AdminId != adminId)
                return BadRequest<string>("You are not assigned to this chat.");

            chat.Status = SupportChatStatus.Closed;
            chat.ClosedAt = DateTime.UtcNow;

            await _supportRepo.UpdateChatAsync(chat);

            var targetUserId = ParseGuidOrNull(chat.UserId);
            await RecordAuditAsync(new AuditEntry(
                AuditActions.SupportChatClosed,
                ParseGuidOrNull(adminId),
                targetUserId,
                await ResolveUserDisplayAsync(targetUserId, $"chat {chat.Id}"),
                null)
            {
                Category = AuditCategories.SupportChat,
                EntityType = AuditEntityTypes.SupportChat,
                EntityId = chat.Id.ToString()
            });

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
