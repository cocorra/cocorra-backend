using Cocorra.BLL.Base;
using Cocorra.BLL.Services.AuditLogs;
using Cocorra.BLL.Services.Email;
using Cocorra.BLL.Services.EventTracking;
using Cocorra.BLL.Services.NotificationService;
using Cocorra.BLL.Services.Storage;
using Cocorra.BLL.Services.Upload;
using Cocorra.DAL.Data;
using Cocorra.DAL.DTOS.AdminDto;
using Cocorra.DAL.DTOS.StorageDto;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;
using Cocorra.DAL.Repository.BlockedDevicesRepository;
using Cocorra.DAL.Repository.NotificationRepository;
using Cocorra.DAL.Repository.SupportRepository;
using Cocorra.DAL.Repository.UserRepository;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Cocorra.BLL.Services.AdminService
{
    public class AdminService : ResponseHandler, IAdminService
    {
        private const string ReRecordTitle = "إعادة تسجيل صوتي 🎙️";
        private const string ReRecordBody = "نعتذر منك، نحتاج منك إعادة تسجيل المقطع الصوتي الخاص بك بوضوح أكبر.";
        private const int MaxBulkStatusBatch = 200;

        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IUploadVoice _uploadVoice;
        private readonly IEmailService _emailService;
        private readonly string _emailLogoUrl;
        private readonly IUserRepository _userRepository;
        private readonly IPushNotificationService _pushService;
        private readonly IBlockedDevicesRepository _blockedDevicesRepository;
        private readonly IEventTracker _eventTracker;
        private readonly AppDbContext _context;
        private readonly INotificationRepository _notificationRepo;
        private readonly IAuditLogService? _auditLog;
        private readonly IPrivateFileUrlIssuer _fileUrls;

        public AdminService(
            UserManager<ApplicationUser> userManager,
            IUploadVoice uploadVoice,
            IConfiguration configuration,
            IEmailService emailService,
            IUserRepository userRepository,
            IPushNotificationService pushService,
            IBlockedDevicesRepository blockedDevicesRepository,
            IEventTracker eventTracker,
            AppDbContext context,
            INotificationRepository notificationRepo,
            IPrivateFileUrlIssuer fileUrls,
            IAuditLogService? auditLog = null)
        {
            _blockedDevicesRepository = blockedDevicesRepository;
            _userManager = userManager;
            _uploadVoice = uploadVoice;
            _emailLogoUrl = EmailTemplates.ResolveLogoUrl(configuration["EmailSettings:LogoUrl"]);
            _emailService = emailService;
            _userRepository = userRepository;
            _pushService = pushService;
            _eventTracker = eventTracker;
            _context = context;
            _notificationRepo = notificationRepo;
            _auditLog = auditLog;
            _fileUrls = fileUrls;
        }

        private Task RecordAuditAsync(AuditEntry entry) =>
            _auditLog is null ? Task.CompletedTask : _auditLog.RecordAsync(entry);

        public async Task<StatusChangeResponse> ChangeUserStatusAsync(
            Guid userId,
            UserStatus newStatus,
            string? reason,
            Guid actorUserId,
            bool callerIsAdmin,
            bool isBulk = false)
        {
            if (!AuditReasonValidator.TryValidateRequired(reason, out var normalizedReason, out var reasonError))
                return StatusChangeResponse.From(BadRequest<string>(reasonError));

            if (!Enum.IsDefined(typeof(UserStatus), newStatus))
                return StatusChangeResponse.From(BadRequest<string>("Invalid status value."));

            if (userId == actorUserId)
                return StatusChangeResponse.From(BadRequest<string>("You cannot change your own status."));

            var user = await _userManager.FindByIdAsync(userId.ToString());
            if (user == null)
                return StatusChangeResponse.From(BadRequest<string>("User not found"));

            var targetRoles = (await _userManager.GetRolesAsync(user)) ?? Array.Empty<string>();
            var guardError = ValidateStatusChangePermission(user, targetRoles, newStatus, callerIsAdmin);
            if (guardError != null)
                return guardError;

            if (user.Status == newStatus)
                return StatusChangeResponse.From(BadRequest<string>($"User is already {newStatus}"));

            var transition = await ApplyStatusTransitionAsync(user, newStatus, targetRoles, actorUserId, isBulk);
            if (!transition.Succeeded)
            {
                var errorResponse = transition.IsConcurrencyConflict
                    ? Conflict<string>(transition.ErrorMessage ?? "The user's status was modified by another request.")
                    : BadRequest<string>(transition.ErrorMessage ?? "Failed to change status");
                return StatusChangeResponse.From(errorResponse);
            }

            await RecordAuditAsync(new AuditEntry(
                AuditActions.UserStatusChanged,
                actorUserId,
                user.Id,
                user.Email,
                new { fromStatus = transition.OldStatus.ToString(), toStatus = transition.NewStatus.ToString(), isBulk })
            {
                Category = AuditCategories.UserModeration,
                EntityType = AuditEntityTypes.User,
                EntityId = user.Id.ToString(),
                FromState = transition.OldStatus.ToString(),
                ToState = transition.NewStatus.ToString(),
                Reason = normalizedReason
            });

            return StatusChangeResponse.From(
                Success($"User status changed from {transition.OldStatus} to {transition.NewStatus}"),
                transition.ShouldRevokeSession);
        }

        private StatusChangeResponse? ValidateStatusChangePermission(
            ApplicationUser user,
            IList<string> targetRoles,
            UserStatus newStatus,
            bool callerIsAdmin)
        {
            if (!callerIsAdmin)
            {
                if (AppRoles.AnyProtectedFromModerators(targetRoles))
                    return StatusChangeResponse.From(Forbidden<string>("Moderators cannot change the status of staff or admin accounts."));

                if (user.Status is UserStatus.Pending or UserStatus.ReRecord)
                    return StatusChangeResponse.From(Forbidden<string>("Moderators cannot change the status of accounts in voice verification."));

                if (newStatus is not (UserStatus.Active or UserStatus.Banned))
                    return StatusChangeResponse.From(Forbidden<string>("Moderators can only set user status to Active or Banned."));

                return null;
            }

            if (targetRoles.Contains(AppRoles.Admin, StringComparer.OrdinalIgnoreCase))
                return StatusChangeResponse.From(BadRequest<string>("Cannot perform actions on an Admin account."));

            return null;
        }

        public async Task<StatusTransitionOutcome> ApplyStatusTransitionAsync(
            ApplicationUser user,
            UserStatus newStatus,
            IEnumerable<string> targetRoles,
            Guid actorUserId,
            bool isBulk = false)
        {
            var oldStatus = user.Status;
            var voicePathToDelete = newStatus is UserStatus.Active or UserStatus.Banned or UserStatus.Rejected or UserStatus.ReRecord
                ? user.VoiceVerificationPath
                : null;

            user.Status = newStatus;
            var banLockoutEnd = ApplyStatusMutations(user, newStatus);

            IdentityResult updateResult;
            try
            {
                updateResult = await _userManager.UpdateAsync(user);
            }
            catch (DbUpdateConcurrencyException)
            {
                return new StatusTransitionOutcome(
                    false,
                    oldStatus,
                    newStatus,
                    false,
                    "The user's status was modified by another request.",
                    IsConcurrencyConflict: true);
            }

            if (!updateResult.Succeeded)
                return FailedTransitionOutcome(oldStatus, newStatus, updateResult);

            if (!string.IsNullOrEmpty(voicePathToDelete))
            {
                _uploadVoice.DeleteVoice(voicePathToDelete);
            }

            // Rotate security stamp for privileged accounts, terminal bans/rejections, and
            // Active demotions, while keeping plain users' Pending->Active approval tokens intact.
            var shouldRevokeSession = AppRoles.AnyProtectedFromModerators(targetRoles)
                || newStatus is UserStatus.Banned or UserStatus.Rejected
                || (oldStatus == UserStatus.Active && newStatus != UserStatus.Active);
            if (shouldRevokeSession)
            {
                await _userManager.UpdateSecurityStampAsync(user);
            }

            if (newStatus is UserStatus.Banned or UserStatus.Rejected or UserStatus.Pending or UserStatus.ReRecord)
            {
                await ReleaseActiveSupportChatsAsync(user.Id);
            }

            TrackStatusTransitionEvents(user.Id, oldStatus, newStatus, actorUserId, isBulk);
            await PersistStatusNotificationAsync(user.Id, newStatus);
            await TrySendVerificationEmailAsync(user, newStatus);
            await DispatchStatusPushAndCleanupFcmAsync(user, newStatus, banLockoutEnd);

            return new StatusTransitionOutcome(true, oldStatus, newStatus, shouldRevokeSession);
        }

        private static StatusTransitionOutcome FailedTransitionOutcome(
            UserStatus oldStatus,
            UserStatus newStatus,
            IdentityResult failedResult)
        {
            var isConcurrencyConflict = failedResult.Errors.Any(e =>
                string.Equals(e.Code, nameof(IdentityErrorDescriber.ConcurrencyFailure), StringComparison.OrdinalIgnoreCase));

            return new StatusTransitionOutcome(
                false,
                oldStatus,
                newStatus,
                false,
                isConcurrencyConflict ? "The user's status was modified by another request." : "Failed to change status",
                isConcurrencyConflict);
        }

        private static DateTimeOffset? ApplyStatusMutations(ApplicationUser user, UserStatus newStatus)
        {
            DateTimeOffset? banLockoutEnd = null;

            // Mutate in-memory so the single UpdateAsync commits every field atomically under one concurrency check.
            switch (newStatus)
            {
                case UserStatus.Active:
                    user.LockoutEnabled = false;
                    user.LockoutEnd = null;
                    user.AccessFailedCount = 0;
                    user.VoiceVerificationPath = null;
                    break;

                case UserStatus.Banned:
                    user.LockoutEnabled = true;
                    banLockoutEnd = DateTimeOffset.MaxValue;
                    user.LockoutEnd = banLockoutEnd;
                    user.VoiceVerificationPath = null;
                    user.RefreshToken = null;
                    break;

                case UserStatus.Rejected:
                    user.VoiceVerificationPath = null;
                    user.RefreshToken = null;
                    break;

                case UserStatus.ReRecord:
                    user.VoiceVerificationPath = null;
                    break;

                case UserStatus.Pending:
                    break;
            }

            return banLockoutEnd;
        }

        private Task ReleaseActiveSupportChatsAsync(Guid userId) =>
            SupportRepository.ReleaseActiveChatsForStaffAsync(_context, userId.ToString());

        private void TrackStatusTransitionEvents(
            Guid userId,
            UserStatus oldStatus,
            UserStatus newStatus,
            Guid actorUserId,
            bool isBulk)
        {
            _eventTracker.Track(EventTypes.VoiceVerificationResult, userId, new { status = newStatus.ToString() });
            _eventTracker.Track(
                EventTypes.UserStatusChanged,
                userId,
                new
                {
                    fromStatus = oldStatus.ToString(),
                    toStatus = newStatus.ToString(),
                    changedByAdminId = actorUserId,
                    isBulkOperation = isBulk
                });

            if (newStatus == UserStatus.Active)
            {
                _eventTracker.Track(
                    EventTypes.ActivationCompleted,
                    userId,
                    properties: null,
                    eventKey: $"{EventTypes.ActivationCompleted}:{userId}");
            }
        }

        private async Task PersistStatusNotificationAsync(Guid userId, UserStatus newStatus)
        {
            Notification? notification = newStatus switch
            {
                UserStatus.Active => new Notification
                {
                    UserId = userId,
                    Title = "Account Verified ✅",
                    Message = "Your voice verification has been approved. You now have full access to Cocorra.",
                    Type = NotificationType.System,
                    IsRead = false
                },
                UserStatus.Banned => new Notification
                {
                    UserId = userId,
                    Title = "Account Suspended",
                    Message = "Your account has been permanently suspended for violating community guidelines.",
                    Type = NotificationType.AdminWarning,
                    IsRead = false
                },
                UserStatus.Rejected => new Notification
                {
                    UserId = userId,
                    Title = "Verification Rejected",
                    Message = "Your voice verification has been rejected. Please contact support for more information.",
                    Type = NotificationType.System,
                    IsRead = false
                },
                UserStatus.ReRecord => new Notification
                {
                    UserId = userId,
                    Title = ReRecordTitle,
                    Message = ReRecordBody,
                    Type = NotificationType.System,
                    IsRead = false
                },
                _ => null
            };

            if (notification != null)
            {
                await _notificationRepo.AddAsync(notification);
            }
        }

        private async Task TrySendVerificationEmailAsync(ApplicationUser user, UserStatus newStatus)
        {
            // Email delivery is best-effort after the status update has already committed.
            try
            {
                await SendVerificationEmailAsync(user, newStatus);
            }
            catch
            {
            }
        }

        private async Task DispatchStatusPushAndCleanupFcmAsync(
            ApplicationUser user,
            UserStatus newStatus,
            DateTimeOffset? banLockoutEnd)
        {
            if (!string.IsNullOrEmpty(user.FcmToken))
            {
                switch (newStatus)
                {
                    case UserStatus.Active:
                        await _pushService.SendPushNotificationAsync(
                            user.FcmToken,
                            "Account Verified ✅",
                            "Your account is now fully active.",
                            new Dictionary<string, string> { { "type", "account_activated" } });
                        break;

                    case UserStatus.Banned:
                        var banData = new Dictionary<string, string> { { "type", "account_locked" } };
                        if (banLockoutEnd.HasValue)
                        {
                            banData["lockout_end"] = banLockoutEnd.Value.UtcDateTime.ToString("O");
                        }
                        await _pushService.SendPushNotificationAsync(user.FcmToken, "", "", banData);
                        break;

                    case UserStatus.Rejected:
                        await _pushService.SendPushNotificationAsync(
                            user.FcmToken, "", "",
                            new Dictionary<string, string> { { "type", "account_rejected" } });
                        break;

                    case UserStatus.ReRecord:
                        await _pushService.SendPushNotificationAsync(
                            user.FcmToken, ReRecordTitle, ReRecordBody,
                            new Dictionary<string, string> { { "type", "reRecord" } });
                        break;
                }
            }

            if (newStatus is UserStatus.Banned or UserStatus.Rejected)
            {
                user.FcmToken = null;
                await _userManager.UpdateAsync(user);
            }
        }

        public async Task<Response<BulkChangeStatusResultDto>> BulkChangeUserStatusAsync(BulkChangeStatusDto model, Guid adminId)
        {
            if (model?.UserIds == null || model.UserIds.Count == 0)
                return BadRequest<BulkChangeStatusResultDto>("At least one user id is required.");

            if (!model.NewStatus.HasValue || !Enum.IsDefined(typeof(UserStatus), model.NewStatus.Value))
                return BadRequest<BulkChangeStatusResultDto>("Invalid status value.");

            var newStatus = model.NewStatus.Value;

            if (!AuditReasonValidator.TryValidateRequired(model.Reason, out var normalizedReason, out var reasonError))
                return BadRequest<BulkChangeStatusResultDto>(reasonError);

            var distinctIds = model.UserIds.Distinct().ToList();
            if (distinctIds.Count > MaxBulkStatusBatch)
                return BadRequest<BulkChangeStatusResultDto>(
                    $"Too many users in one request. Maximum is {MaxBulkStatusBatch}.");

            var bulkResult = new BulkChangeStatusResultDto { TotalRequested = distinctIds.Count };

            foreach (var userId in distinctIds)
            {
                if (userId == adminId)
                {
                    bulkResult.Results.Add(new BulkItemResultDto
                    {
                        UserId = userId,
                        Succeeded = false,
                        Message = "You cannot change your own status."
                    });
                    continue;
                }

                var single = await ChangeUserStatusAsync(
                    userId,
                    newStatus,
                    normalizedReason,
                    adminId,
                    callerIsAdmin: true,
                    isBulk: true);

                bulkResult.Results.Add(new BulkItemResultDto
                {
                    UserId = userId,
                    Succeeded = single.Succeeded,
                    Message = single.Message,
                    SessionRevoked = single.SessionRevoked
                });
            }

            bulkResult.SucceededCount = bulkResult.Results.Count(r => r.Succeeded);
            bulkResult.FailedCount = bulkResult.Results.Count(r => !r.Succeeded);

            var message = bulkResult.FailedCount == 0
                ? $"All {bulkResult.SucceededCount} user(s) updated to {newStatus}."
                : $"{bulkResult.SucceededCount} succeeded, {bulkResult.FailedCount} failed.";

            return Success(bulkResult, message: message);
        }

        private async Task SendVerificationEmailAsync(ApplicationUser user, UserStatus newStatus)
        {
            if (string.IsNullOrEmpty(user.Email)) return;

            var firstName = user.FirstName ?? string.Empty;

            switch (newStatus)
            {
                case UserStatus.Pending:
                    await _emailService.SendEmailAsync(
                        user.Email,
                        "كوكورا — تم استلام العينة الصوتية للتحقق",
                        EmailTemplates.VerificationPending(firstName, _emailLogoUrl));
                    break;

                case UserStatus.Active:
                    await _emailService.SendEmailAsync(
                        user.Email,
                        "كوكورا — أهلاً بك! تم توثيق حسابك بنجاح (Verified) ✅",
                        EmailTemplates.AccountVerified(firstName, _emailLogoUrl));
                    break;

                case UserStatus.ReRecord:
                    await _emailService.SendEmailAsync(
                        user.Email,
                        "كوكورا — مطلوب إعادة تسجيل العينة الصوتية",
                        EmailTemplates.ReRecordRequired(firstName, _emailLogoUrl));
                    break;

                default:
                    break;
            }
        }

        public async Task<PagedResponse<UserDto>> GetAllUsersAsync(
            string? search,
            int page = 1,
            int pageSize = 10,
            bool callerIsAdmin = false)
        {
            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 1, 100);

            var (totalCount, users) = await _userRepository.GetPaginatedUsersWithRolesAsync(
                search,
                page,
                pageSize,
                excludeAdmins: !callerIsAdmin);

            var list = users.ToList();

            foreach (var user in list)
            {
                user.HasVoice = _fileUrls.HasFile(user.VoicePath);
                user.VoicePath = null;

                if (!callerIsAdmin)
                {
                    user.Email = null;
                    user.Age = null;
                    user.MBTI = null;
                }
            }

            return Paginated(list, totalCount, page, pageSize);
        }

        public async Task<Response<UserDto>> GetUserByIdAsync(Guid userId, bool callerIsAdmin = false)
        {
            var user = await _userManager.FindByIdAsync(userId.ToString());
            if (user == null)
                return NotFound<UserDto>("User not found");

            var roles = (await _userManager.GetRolesAsync(user)) ?? Array.Empty<string>();
            if (!callerIsAdmin && roles.Contains(AppRoles.Admin, StringComparer.OrdinalIgnoreCase))
                return NotFound<UserDto>("User not found");

            var userDto = new UserDto
            {
                Id = user.Id.ToString(),
                FullName = $"{user.FirstName} {user.LastName}",
                Email = callerIsAdmin ? user.Email : null,
                Age = callerIsAdmin ? user.Age : null,
                MBTI = callerIsAdmin ? (user.MBTI ?? "N/A") : null,
                Status = user.Status.ToString(),
                CreatedAt = user.CreatedAt,
                VoicePath = null,
                HasVoice = _fileUrls.HasFile(user.VoiceVerificationPath),
                Roles = roles
            };

            return Success(userDto);
        }

        public async Task<Response<PrivateFileUrlDto>> GetUserVoiceUrlAsync(Guid userId)
        {
            var user = await _userManager.FindByIdAsync(userId.ToString());
            if (user == null)
                return NotFound<PrivateFileUrlDto>("User not found");

            var issued = (await _fileUrls.IssueAsync(
                PrivateFileKind.Voice,
                new[] { new PrivateFileRef(user.Id, user.VoiceVerificationPath, user.Id) },
                "Admin.VoiceUrl"))[0];
            if (issued == null)
                return NotFound<PrivateFileUrlDto>("This user has no voice recording.");

            return Success(new PrivateFileUrlDto { Url = issued.Url, ExpiresAtUtc = issued.ExpiresAtUtc });
        }

        public async Task<Response<DashboardStatsDto>> GetDashboardStatsAsync()
        {
            var statusCounts = await _userManager.Users
                .GroupBy(u => u.Status)
                .Select(g => new { Status = g.Key, Count = g.Count() })
                .ToListAsync();

            var stats = new DashboardStatsDto
            {
                TotalUsers = statusCounts.Sum(s => s.Count),
                ActiveUsers = statusCounts.FirstOrDefault(s => s.Status == UserStatus.Active)?.Count ?? 0,
                PendingUsers = statusCounts.FirstOrDefault(s => s.Status == UserStatus.Pending)?.Count ?? 0,
                BannedUsers = statusCounts.FirstOrDefault(s => s.Status == UserStatus.Banned)?.Count ?? 0,
                RejectedUsers = statusCounts.FirstOrDefault(s => s.Status == UserStatus.Rejected)?.Count ?? 0,
                ReRecordUsers = statusCounts.FirstOrDefault(s => s.Status == UserStatus.ReRecord)?.Count ?? 0
            };

            return Success(stats);
        }

        public async Task<Response<BlockDeviceAndEmailResultDto>> BlockDeviceAndEmailAsync(
            BlockDeviceAndEmailDto model,
            Guid adminId)
        {
            if (model == null || string.IsNullOrWhiteSpace(model.Email))
                return BadRequest<BlockDeviceAndEmailResultDto>("Email is required.");

            if (!AuditReasonValidator.TryValidateRequired(model.Reason, out var normalizedReason, out var reasonError))
                return BadRequest<BlockDeviceAndEmailResultDto>(reasonError);

            var user = await _userManager.FindByEmailAsync(model.Email);
            if (user == null)
                return NotFound<BlockDeviceAndEmailResultDto>("User not found with the provided email.");

            if (await _userManager.IsInRoleAsync(user, AppRoles.Admin))
                return BadRequest<BlockDeviceAndEmailResultDto>("Cannot perform actions on an Admin account.");

            var oldStatus = user.Status;

            user.Status = UserStatus.Banned;
            await _userManager.SetLockoutEnabledAsync(user, true);
            await _userManager.SetLockoutEndDateAsync(user, DateTimeOffset.MaxValue);
            user.RefreshToken = null;
            user.RefreshTokenExpiryTime = DateTime.UtcNow;
            user.FcmToken = null;

            await _userManager.UpdateAsync(user);
            await _userManager.UpdateSecurityStampAsync(user);
            await ReleaseActiveSupportChatsAsync(user.Id);

            var devicesBlocked = await _blockedDevicesRepository.BlockAllDevicesForUserAsync(user.Id);

            _eventTracker.Track(
                EventTypes.UserStatusChanged,
                user.Id,
                new
                {
                    fromStatus = oldStatus.ToString(),
                    toStatus = UserStatus.Banned.ToString(),
                    changedByAdminId = adminId,
                    isBulkOperation = false,
                    devicesBlocked
                });

            await RecordAuditAsync(new AuditEntry(
                AuditActions.UserDeviceAndEmailBlocked,
                adminId,
                user.Id,
                user.Email,
                new { fromStatus = oldStatus.ToString(), toStatus = UserStatus.Banned.ToString(), devicesBlocked })
            {
                Category = AuditCategories.UserModeration,
                EntityType = AuditEntityTypes.User,
                EntityId = user.Id.ToString(),
                FromState = oldStatus.ToString(),
                ToState = UserStatus.Banned.ToString(),
                Reason = normalizedReason
            });

            var result = new BlockDeviceAndEmailResultDto
            {
                UserId = user.Id,
                Email = user.Email!,
                AccountBanned = true,
                DevicesBlocked = devicesBlocked
            };

            var message = devicesBlocked > 0
                ? $"Account permanently banned and {devicesBlocked} registered device(s) blocked."
                : "Account permanently banned. No registered devices were found for this user.";

            return Success(result, message: message);
        }

        public async Task<Response<BroadcastNotificationResultDto>> BroadcastNotificationAsync(
            SendBroadcastNotificationDto model,
            Guid adminId)
        {
            if (model == null)
                return BadRequest<BroadcastNotificationResultDto>("Request model cannot be null.");

            if (string.IsNullOrWhiteSpace(model.Title))
                return BadRequest<BroadcastNotificationResultDto>("Title is required.");

            var body = model.Body;
            if (string.IsNullOrWhiteSpace(body))
                return BadRequest<BroadcastNotificationResultDto>("Notification body or message is required.");

            var query = _context.Users.AsNoTracking();
            query = model.TargetStatus.HasValue
                ? query.Where(u => u.Status == model.TargetStatus.Value)
                : query.Where(u => u.Status != UserStatus.Banned && u.Status != UserStatus.Rejected);

            var targetUsers = await query
                .Select(u => new { u.Id, u.FcmToken })
                .ToListAsync();

            if (targetUsers.Count == 0)
            {
                return Success(new BroadcastNotificationResultDto(), message: "No eligible users found to notify.");
            }

            var fcmTokens = targetUsers
                .Where(u => !string.IsNullOrWhiteSpace(u.FcmToken))
                .Select(u => u.FcmToken!)
                .Distinct()
                .ToList();

            var inAppCount = 0;
            if (model.SaveInAppNotification)
            {
                var now = DateTime.UtcNow;
                var inAppNotifications = targetUsers.Select(u => new Notification
                {
                    Id = Guid.NewGuid(),
                    UserId = u.Id,
                    Title = model.Title,
                    Message = body,
                    Type = NotificationType.System,
                    IsRead = false,
                    CreatedAt = now
                }).ToList();

                await _notificationRepo.AddRangeAsync(inAppNotifications);
                inAppCount = inAppNotifications.Count;
            }

            var successCount = 0;
            var failureCount = 0;
            if (fcmTokens.Count > 0)
            {
                var pushResult = await _pushService.SendMulticastPushNotificationAsync(
                    fcmTokens,
                    model.Title,
                    body,
                    model.Data);

                successCount = pushResult.SuccessCount;
                failureCount = pushResult.FailureCount;
            }

            await RecordAuditAsync(new AuditEntry(
                AuditActions.NotificationBroadcastSent,
                adminId,
                null,
                null,
                new
                {
                    title = model.Title,
                    body,
                    targetUsersCount = targetUsers.Count,
                    tokensCount = fcmTokens.Count,
                    successCount,
                    failureCount,
                    inAppNotificationsCreated = inAppCount
                })
            {
                Category = AuditCategories.Notification
            });

            var resultDto = new BroadcastNotificationResultDto
            {
                TotalTargetUsers = targetUsers.Count,
                TokensCount = fcmTokens.Count,
                SuccessCount = successCount,
                FailureCount = failureCount,
                InAppNotificationsCreated = inAppCount
            };

            return Success(resultDto, message: $"Broadcast notification sent to {fcmTokens.Count} token(s) and saved for {inAppCount} user(s).");
        }
    }
}
