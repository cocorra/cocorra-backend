using Cocorra.BLL.Base;
using Cocorra.BLL.Services.SystemLogs;
using Cocorra.DAL.DTOS.StaffActivityDto;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;
using Cocorra.DAL.Repository.AuditLogRepository;
using Cocorra.DAL.Repository.UserRepository;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Cocorra.BLL.Services.StaffActivityService
{
    public class StaffActivityService : ResponseHandler, IStaffActivityService
    {
        private const int MaxPageSize = 100;

        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IAuditLogRepository _auditLogRepo;
        private readonly IUserRepository _userRepository;
        private readonly string _baseUrl;
        private readonly ILogger<StaffActivityService> _logger;

        public StaffActivityService(
            UserManager<ApplicationUser> userManager,
            IAuditLogRepository auditLogRepo,
            IUserRepository userRepository,
            IConfiguration configuration,
            ILogger<StaffActivityService> logger)
        {
            _userManager = userManager;
            _auditLogRepo = auditLogRepo;
            _userRepository = userRepository;
            _baseUrl = configuration["AppSettings:BaseUrl"]?.TrimEnd('/') ?? string.Empty;
            _logger = logger;
        }

        public async Task<Response<StaffActivityPageDto>> GetStaffActivityAsync(Guid staffUserId, StaffActivityQueryDto? query)
        {
            query ??= new StaffActivityQueryDto();

            var category = string.IsNullOrWhiteSpace(query.Category) ? null : query.Category.Trim();
            if (category != null && !AuditCategories.All.Contains(category))
                return BadRequest<StaffActivityPageDto>(
                    $"Invalid category. Allowed values: {string.Join(", ", AuditCategories.All)}.");

            var actionType = string.IsNullOrWhiteSpace(query.ActionType) ? null : query.ActionType.Trim();
            if (actionType != null && !AuditActions.All.Contains(actionType))
                return BadRequest<StaffActivityPageDto>(
                    $"Invalid actionType. Allowed values: {string.Join(", ", AuditActions.All)}.");

            var fromUtc = LogText.ToUtc(query.FromUtc);
            var toUtc = LogText.ToUtc(query.ToUtc);
            if (fromUtc.HasValue && toUtc.HasValue && fromUtc > toUtc)
                return BadRequest<StaffActivityPageDto>("'fromUtc' must not be later than 'toUtc'.");

            var staffUser = await _userManager.FindByIdAsync(staffUserId.ToString());
            if (staffUser == null)
                return NotFound<StaffActivityPageDto>("Staff member not found.");

            var roles = (await _userManager.GetRolesAsync(staffUser)) ?? Array.Empty<string>();
            if (!roles.Any(AppRoles.Staff.Contains))
                return NotFound<StaffActivityPageDto>("Staff member not found.");

            var pageNumber = Math.Max(1, query.PageNumber);
            var pageSize = Math.Clamp(query.PageSize, 1, MaxPageSize);

            var summaryGroups = await _auditLogRepo.GetActorSummaryGroupsAsync(staffUserId, fromUtc, toUtc);
            var (totalCount, logs) = await _auditLogRepo.GetActorPagedAsync(new ActorAuditLogFilter(
                staffUserId,
                category,
                actionType,
                fromUtc,
                toUtc,
                query.TargetUserId,
                pageNumber,
                pageSize));

            var targetIds = logs
                .Where(l => l.TargetUserId.HasValue)
                .Select(l => l.TargetUserId!.Value)
                .Distinct()
                .ToList();
            var targetIdentities = await _userRepository.GetUserIdentitiesByIdsAsync(targetIds);

            var pageDto = new StaffActivityPageDto
            {
                Staff = BuildStaffProfile(staffUser, roles),
                Summary = BuildSummary(summaryGroups),
                Items = logs.Select(l => ToItemDto(l, targetIdentities)).ToList(),
                TotalCount = totalCount,
                PageNumber = pageNumber,
                PageSize = pageSize,
                TotalPages = pageSize > 0 ? (int)Math.Ceiling(totalCount / (double)pageSize) : 0
            };

            return Success(pageDto);
        }

        private StaffProfileDto BuildStaffProfile(ApplicationUser user, IList<string> roles) => new()
        {
            UserId = user.Id,
            FullName = $"{user.FirstName} {user.LastName}".Trim(),
            Email = user.Email ?? string.Empty,
            Roles = roles.ToList(),
            Status = user.Status.ToString(),
            CreatedAt = LogText.AsUtc(user.CreatedAt),
            ProfilePicture = BuildFullUrl(user.ProfilePicturePath)
        };

        private static StaffActivitySummaryDto BuildSummary(List<AuditActionStateAggregate> groups)
        {
            var summary = new StaffActivitySummaryDto();
            if (groups.Count == 0)
                return summary;

            summary.LastActivityAtUtc = groups.Max(g => g.LastOccurredAtUtc);

            foreach (var group in groups)
            {
                summary.TotalActions += group.Count;
                AccumulateGroup(summary, group);
            }

            return summary;
        }

        private static void AccumulateGroup(StaffActivitySummaryDto summary, AuditActionStateAggregate group)
        {
            var count = group.Count;
            switch (group.Action)
            {
                case AuditActions.VoiceUrlIssued:
                    summary.VoiceUrlsIssued += count;
                    break;

                case AuditActions.VoiceReviewDecision:
                    summary.VoiceReviews += count;
                    if (group.ToState == nameof(UserStatus.Active)) summary.VoiceApprovals += count;
                    else if (group.ToState == nameof(UserStatus.ReRecord)) summary.VoiceReRecords += count;
                    else if (group.ToState == nameof(UserStatus.Rejected)) summary.VoiceRejections += count;
                    break;

                case AuditActions.TicketViewed:
                    summary.TicketsViewed += count;
                    break;

                case AuditActions.TicketStatusChanged:
                    summary.TicketStatusChanges += count;
                    break;

                case AuditActions.ReportViewed:
                    summary.ReportsViewed += count;
                    break;

                case AuditActions.ReportStatusChanged:
                    summary.ReportStatusChanges += count;
                    if (group.ToState == nameof(ReportStatus.Resolved)) summary.ReportsResolved += count;
                    else if (group.ToState == nameof(ReportStatus.Rejected)) summary.ReportsRejected += count;
                    break;

                case AuditActions.ReportActionTaken:
                    AccumulateReportAction(summary, group.ToState, count);
                    break;

                case AuditActions.UserStatusChanged:
                    summary.UserStatusChanges += count;
                    if (group.ToState == nameof(UserStatus.Banned)) summary.BansIssued += count;
                    break;

                case AuditActions.UserDeviceAndEmailBlocked:
                    summary.BansIssued += count;
                    break;

                case AuditActions.SupportChatClaimed:
                    summary.ChatsClaimed += count;
                    break;

                case AuditActions.SupportChatReplied:
                    summary.ChatRepliesSent += count;
                    break;

                case AuditActions.SupportChatClosed:
                    summary.ChatsClosed += count;
                    break;

                case AuditActions.RoomCreated:
                    summary.RoomsCreated += count;
                    break;

                case AuditActions.RoomStarted:
                    summary.RoomsStarted += count;
                    break;

                case AuditActions.RoomEnded:
                    summary.RoomsEnded += count;
                    break;

                case AuditActions.RoomUserApproved:
                case AuditActions.RoomStageApproved:
                case AuditActions.RoomMovedToAudience:
                case AuditActions.RoomUserKicked:
                case AuditActions.RoomUserMuted:
                case AuditActions.RoomExtraTimeGranted:
                    summary.RoomModerationActions += count;
                    break;
            }
        }

        private static void AccumulateReportAction(StaffActivitySummaryDto summary, string? toState, int count)
        {
            switch (toState)
            {
                case nameof(AdminReportAction.WarnUser):
                    summary.WarnsIssued += count;
                    summary.ReportsResolved += count;
                    break;

                case nameof(AdminReportAction.Mute24h):
                    summary.MutesIssued += count;
                    summary.ReportsResolved += count;
                    break;

                case nameof(AdminReportAction.BanUser):
                    summary.BansIssued += count;
                    summary.ReportsResolved += count;
                    break;

                case nameof(AdminReportAction.RejectReport):
                    summary.ReportsRejected += count;
                    break;
            }
        }

        private StaffActivityItemDto ToItemDto(
            AuditLog log,
            IReadOnlyDictionary<Guid, UserIdentitySummary> targetIdentities)
        {
            string? targetName = log.TargetDisplay;
            string? targetEmail = null;

            if (log.TargetUserId.HasValue && targetIdentities.TryGetValue(log.TargetUserId.Value, out var identity))
            {
                targetName = !string.IsNullOrWhiteSpace(identity.FullName) ? identity.FullName : log.TargetDisplay;
                targetEmail = identity.Email;
            }

            return new StaffActivityItemDto
            {
                Id = log.Id,
                OccurredAtUtc = LogText.AsUtc(log.OccurredAtUtc),
                Action = log.Action,
                Category = log.Category,
                EntityType = log.EntityType,
                EntityId = log.EntityId,
                TargetUserId = log.TargetUserId,
                TargetName = targetName,
                TargetEmail = targetEmail,
                FromState = log.FromState,
                ToState = log.ToState,
                Reason = log.Reason,
                Details = ParseDetails(log)
            };
        }

        private JsonElement? ParseDetails(AuditLog log)
        {
            if (string.IsNullOrEmpty(log.DetailsJson))
                return null;

            try
            {
                using var document = JsonDocument.Parse(log.DetailsJson);
                return document.RootElement.Clone();
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Audit log {AuditLogId} has malformed DetailsJson.", log.Id);
                return null;
            }
        }

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
    }
}
