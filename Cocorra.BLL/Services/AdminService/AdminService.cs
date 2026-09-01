using Cocorra.BLL.Services.Email;
using Cocorra.BLL.Services.NotificationService;
using Cocorra.BLL.Services.Upload;
using Cocorra.DAL.DTOS.AdminDto;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;
using Cocorra.BLL.Base;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.IO;
using Cocorra.DAL.Repository.UserRepository;
using Cocorra.DAL.Repository.BlockedDevicesRepository;
using Cocorra.DAL.Repository.NotificationRepository;
using Cocorra.BLL.Services.EventTracking;
using Cocorra.DAL.Data;

namespace Cocorra.BLL.Services.AdminService
{
    public class AdminService : ResponseHandler, IAdminService
    {
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IUploadVoice _uploadVoice;
        private readonly IEmailService _emailService;
        private readonly string _baseUrl;
        private readonly IUserRepository _userRepository;
        private readonly IPushNotificationService _pushService;
        private readonly IBlockedDevicesRepository _blockedDevicesRepository;
        private readonly IEventTracker _eventTracker;
        private readonly AppDbContext _context;
        private readonly INotificationRepository _notificationRepo;

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
            INotificationRepository notificationRepo)
        {
            _blockedDevicesRepository = blockedDevicesRepository;
            _userManager = userManager;
            _uploadVoice = uploadVoice;
            _baseUrl = configuration["AppSettings:BaseUrl"]?.TrimEnd('/') ?? "";
            _emailService = emailService;
            _userRepository = userRepository;
            _pushService = pushService;
            _eventTracker = eventTracker;
            _context = context;
            _notificationRepo = notificationRepo;
        }

        private string? BuildFullUrl(string? relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
                return null;
                
            if (relativePath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || 
                relativePath.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return relativePath;
            }
            
            return $"{_baseUrl}/{relativePath.Replace("\\", "/").TrimStart('/')}";
        }

        // Shared by the persisted Notification and the push so the two can't drift apart.
        private const string ReRecordTitle = "إعادة تسجيل صوتي 🎙️";
        private const string ReRecordBody = "نعتذر منك، نحتاج منك إعادة تسجيل المقطع الصوتي الخاص بك بوضوح أكبر.";

        public async Task<Response<string>> ChangeUserStatusAsync(Guid userId, UserStatus newStatus, Guid adminId, bool isBulk = false)
        {
            var user = await _userManager.FindByIdAsync(userId.ToString());
            if (user == null) return BadRequest<string>("User not found");

            if (!Enum.IsDefined(typeof(UserStatus), newStatus))
                return BadRequest<string>("Invalid status value.");

            if (user.Status == newStatus)
                return BadRequest<string>($"User is already {newStatus}");

            var oldStatus = user.Status;
            user.Status = newStatus;

            // Captured in the switch, consumed by the push below once the update succeeds.
            DateTimeOffset? banLockoutEnd = null;

            switch (newStatus)
            {
                case UserStatus.Active:
                    // Fully clear all lockout state to prevent ghost-bans.
                    await _userManager.SetLockoutEnabledAsync(user, false);
                    await _userManager.SetLockoutEndDateAsync(user, null);
                    await _userManager.ResetAccessFailedCountAsync(user);
                    _uploadVoice.DeleteVoice(user.VoiceVerificationPath);
                    user.VoiceVerificationPath = null;
                    break;

                case UserStatus.Banned:
                    await _userManager.SetLockoutEnabledAsync(user, true);
                    banLockoutEnd = DateTimeOffset.MaxValue;
                    await _userManager.SetLockoutEndDateAsync(user, banLockoutEnd);
                    _uploadVoice.DeleteVoice(user.VoiceVerificationPath);
                    user.VoiceVerificationPath = null;

                    // SECURITY: Invalidate refresh token to prevent session resurrection.
                    user.RefreshToken = null;
                    break;

                case UserStatus.Rejected:
                    _uploadVoice.DeleteVoice(user.VoiceVerificationPath);
                    user.VoiceVerificationPath = null;

                    // Invalidate refresh token so rejected user can't silently refresh.
                    user.RefreshToken = null;
                    break;

                case UserStatus.ReRecord:
                    _uploadVoice.DeleteVoice(user.VoiceVerificationPath);
                    user.VoiceVerificationPath = null;
                    break;

                case UserStatus.Pending:
                    break;
            }

            var result = await _userManager.UpdateAsync(user);

            if (result.Succeeded)
            {
                _eventTracker.Track(EventTypes.VoiceVerificationResult, user.Id, new { status = newStatus.ToString() });

                // AN-011: the only durable record of the transition, emitted after the update
                // succeeded so a failed write cannot produce a phantom transition.
                _eventTracker.Track(
                    EventTypes.UserStatusChanged,
                    user.Id,
                    new
                    {
                        fromStatus = oldStatus.ToString(),
                        toStatus = newStatus.ToString(),
                        changedByAdminId = adminId,
                        isBulkOperation = isBulk
                    });

                if (newStatus == UserStatus.Active)
                {
                    // AN-010: idempotency is enforced by UX_UserEvents_EventId, not by reading
                    // the table. The previous AnyAsync guard queried UserEvents while Track only
                    // ENQUEUES, so two concurrent activations both saw "not yet activated" and
                    // both emitted. A deterministic eventKey makes the duplicate impossible to
                    // persist and removes a database round-trip from the activation path.
                    _eventTracker.Track(
                        EventTypes.ActivationCompleted,
                        user.Id,
                        properties: null,
                        eventKey: $"{EventTypes.ActivationCompleted}:{user.Id}");
                }

                // Persist a Notification row so the decision survives a failed push and
                // can still be read back from GET api/Notifications/my-notifications.
                Notification? statusNotification = newStatus switch
                {
                    UserStatus.Active => new Notification
                    {
                        UserId = user.Id,
                        Title = "Account Verified ✅",
                        Message = "Your voice verification has been approved. You now have full access to Cocorra.",
                        Type = NotificationType.System,
                        IsRead = false
                    },
                    UserStatus.Banned => new Notification
                    {
                        UserId = user.Id,
                        Title = "Account Suspended",
                        Message = "Your account has been permanently suspended for violating community guidelines.",
                        Type = NotificationType.AdminWarning,
                        IsRead = false
                    },
                    UserStatus.Rejected => new Notification
                    {
                        UserId = user.Id,
                        Title = "Verification Rejected",
                        Message = "Your voice verification has been rejected. Please contact support for more information.",
                        Type = NotificationType.System,
                        IsRead = false
                    },
                    UserStatus.ReRecord => new Notification
                    {
                        UserId = user.Id,
                        Title = ReRecordTitle,
                        Message = ReRecordBody,
                        Type = NotificationType.System,
                        IsRead = false
                    },
                    _ => null
                };

                if (statusNotification != null)
                {
                    await _notificationRepo.AddAsync(statusNotification);
                }

                try
                {
                    await SendVerificationEmailAsync(user, newStatus);
                }
                catch { }

                // All pushes live here, after a successful UpdateAsync, so a user is never
                // told their status changed when the write actually failed.
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
                            var banData = new Dictionary<string, string>
                            {
                                { "type", "account_locked" }
                            };
                            if (banLockoutEnd.HasValue)
                            {
                                banData["lockout_end"] = banLockoutEnd.Value.ToString("o");
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

                // Clear FCM token AFTER the notification is dispatched so future pushes
                // are not routed to a stale or reassigned device.
                if (newStatus == UserStatus.Banned || newStatus == UserStatus.Rejected)
                {
                    user.FcmToken = null;
                    await _userManager.UpdateAsync(user);
                }

                return Success($"User status changed from {oldStatus} to {newStatus}");
            }

            return BadRequest<string>("Failed to change status");
        }

        // Hard cap so a single request can't fan out into thousands of UserManager
        // updates + emails + push notifications and starve the thread pool.
        private const int MaxBulkStatusBatch = 200;

        public async Task<Response<BulkChangeStatusResultDto>> BulkChangeUserStatusAsync(BulkChangeStatusDto model, Guid adminId)
        {
            if (model?.UserIds == null || model.UserIds.Count == 0)
                return BadRequest<BulkChangeStatusResultDto>("At least one user id is required.");

            if (!Enum.IsDefined(typeof(UserStatus), model.NewStatus))
                return BadRequest<BulkChangeStatusResultDto>("Invalid status value.");

            // De-duplicate ids so a repeated id isn't processed twice.
            var distinctIds = model.UserIds.Distinct().ToList();

            if (distinctIds.Count > MaxBulkStatusBatch)
                return BadRequest<BulkChangeStatusResultDto>(
                    $"Too many users in one request. Maximum is {MaxBulkStatusBatch}.");

            var result = new BulkChangeStatusResultDto { TotalRequested = distinctIds.Count };

            foreach (var userId in distinctIds)
            {
                // Guard: an admin cannot change their own status, even in bulk.
                if (userId == adminId)
                {
                    result.Results.Add(new BulkItemResultDto
                    {
                        UserId = userId,
                        Succeeded = false,
                        Message = "You cannot change your own status."
                    });
                    continue;
                }

                // Reuse the single-user path so all side effects (lockout, token
                // invalidation, voice cleanup, email, push, event tracking) apply.
                var single = await ChangeUserStatusAsync(userId, model.NewStatus, adminId, isBulk: true);
                result.Results.Add(new BulkItemResultDto
                {
                    UserId = userId,
                    Succeeded = single.Succeeded,
                    Message = single.Message
                });
            }

            result.SucceededCount = result.Results.Count(r => r.Succeeded);
            result.FailedCount = result.Results.Count(r => !r.Succeeded);

            var message = result.FailedCount == 0
                ? $"All {result.SucceededCount} user(s) updated to {model.NewStatus}."
                : $"{result.SucceededCount} succeeded, {result.FailedCount} failed.";

            // The batch endpoint itself executed successfully even on partial failure;
            // the caller inspects per-item Results. Always 200 here.
            return Success(result, message: message);
        }

        private async Task SendVerificationEmailAsync(ApplicationUser user, UserStatus newStatus)
        {
            if (string.IsNullOrEmpty(user.Email)) return;

            switch (newStatus)
            {
                case UserStatus.Pending:
                    await _emailService.SendEmailAsync(
                        user.Email,
                        "Cocorra — Voice Verification Received",
                        $"<h2>Hi {user.FirstName},</h2>" +
                        "<p>Thank you for submitting your voice verification. Your request has been received and is currently under review by our team.</p>" +
                        "<p>We'll notify you once a decision has been made. This usually takes 24–48 hours.</p>" +
                        "<br><p>— The Cocorra Team</p>");
                    break;

                case UserStatus.Active:
                    await _emailService.SendEmailAsync(
                        user.Email,
                        "Cocorra — Welcome! You're Verified ✅",
                        $"<h2>Welcome, {user.FirstName}!</h2>" +
                        "<p>Your voice verification has been approved. You now have full access to Cocorra — explore rooms, join conversations, and connect with the community.</p>" +
                        "<p>We're excited to have you on board!</p>" +
                        "<br><p>— The Cocorra Team</p>");
                    break;

                case UserStatus.ReRecord:
                    await _emailService.SendEmailAsync(
                        user.Email,
                        "Cocorra — Action Required: New Voice Sample Needed",
                        $"<h2>Hi {user.FirstName},</h2>" +
                        "<p>We reviewed your voice verification but unfortunately couldn't approve it. This could be due to poor audio quality, background noise, or an incomplete recording.</p>" +
                        "<p><strong>Please open the app and submit a new voice sample</strong> so we can complete your verification.</p>" +
                        "<br><p>— The Cocorra Team</p>");
                    break;

                default:
                    break;
            }
        }

        public async Task<PagedResponse<UserDto>> GetAllUsersAsync(string? search, int page = 1, int pageSize = 10)
        {
            var (totalCount, users) = await _userRepository.GetPaginatedUsersWithRolesAsync(search, page, pageSize, _baseUrl);

            return Paginated(users, totalCount, page, pageSize);
        }

        public async Task<Response<UserDto>> GetUserByIdAsync(Guid userId)
        {
            var user = await _userManager.FindByIdAsync(userId.ToString());

            if (user == null)
                return BadRequest<UserDto>("User not found");

            var roles = await _userManager.GetRolesAsync(user);

            var userDto = new UserDto
            {
                Id = user.Id.ToString(),
                FullName = $"{user.FirstName} {user.LastName}",
                Email = user.Email!,
                Age = user.Age,
                MBTI = user.MBTI ?? "N/A",
                Status = user.Status.ToString(),
                CreatedAt = user.CreatedAt,
                VoicePath = BuildFullUrl(user.VoiceVerificationPath),
                Roles = roles
            };

            return Success(userDto);
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

        public async Task<Response<string>> BlockDeviceAndEmailAsync(BlockDeviceAndEmailDto model)
        {
            var user = await _userManager.FindByEmailAsync(model.Email);
            if (user == null)
            {
                return NotFound<string>("User not found with the provided email.");
            }

            // 1. Change user status to Banned and lockout
            user.Status = UserStatus.Banned;
            await _userManager.SetLockoutEnabledAsync(user, true);
            await _userManager.SetLockoutEndDateAsync(user, DateTimeOffset.MaxValue);
            
            // SECURITY: Invalidate refresh token and clear FCM token to prevent session resurrection or stale pushes.
            user.RefreshToken = null;
            user.RefreshTokenExpiryTime = DateTime.UtcNow;
            user.FcmToken = null;

            await _userManager.UpdateAsync(user);

            // 2. Add device to BlockedDevices table
            var existingDevice = await _blockedDevicesRepository.GetByDeviceIdAsync(model.DeviceId);
            if (existingDevice != null)
            {
                if (!existingDevice.IsBlocked)
                {
                    existingDevice.IsBlocked = true;
                    await _blockedDevicesRepository.UpdateBlockedDeviceAsync(existingDevice);
                }
            }
            else
            {
                var blockedDevice = new Cocorra.DAL.Models.BlockedDevices
                {
                    DeviceId = model.DeviceId,
                    DeviceName = model.DeviceName,
                    DeviceModel = model.DeviceModel,
                    DeviceType = model.DeviceType,
                    DeviceOs = model.DeviceOs,
                    IsBlocked = true,
                    ApplicationUserId = user.Id
                };
                await _blockedDevicesRepository.AddBlockedDeviceAsync(blockedDevice);
            }

            return Success("User email and device have been permanently blocked.");
        }
    }
}