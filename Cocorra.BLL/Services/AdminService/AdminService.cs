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

        public AdminService(UserManager<ApplicationUser> userManager, IUploadVoice uploadVoice, IConfiguration configuration, IEmailService emailService, IUserRepository userRepository, IPushNotificationService pushService , IBlockedDevicesRepository blockedDevicesRepository)
        {
            _blockedDevicesRepository = blockedDevicesRepository;
            _userManager = userManager;
            _uploadVoice = uploadVoice;
            _baseUrl = configuration["AppSettings:BaseUrl"]?.TrimEnd('/') ?? "";
            _emailService = emailService;
            _userRepository = userRepository;
            _pushService = pushService;
        }

        private string? BuildFullUrl(string? relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
                return null;
            return $"{_baseUrl}/{relativePath.Replace("\\", "/").TrimStart('/')}";
        }

        public async Task<Response<string>> ChangeUserStatusAsync(Guid userId, UserStatus newStatus)
        {
            var user = await _userManager.FindByIdAsync(userId.ToString());
            if (user == null) return BadRequest<string>("User not found");

            if (!Enum.IsDefined(typeof(UserStatus), newStatus))
                return BadRequest<string>("Invalid status value.");

            if (user.Status == newStatus)
                return BadRequest<string>($"User is already {newStatus}");

            var oldStatus = user.Status;
            user.Status = newStatus;
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
                    var lockoutEndDate = DateTimeOffset.MaxValue;
                    await _userManager.SetLockoutEndDateAsync(user, lockoutEndDate);
                    _uploadVoice.DeleteVoice(user.VoiceVerificationPath);
                    user.VoiceVerificationPath = null;
                    
                    // SECURITY: Invalidate refresh token to prevent session resurrection.
                    user.RefreshToken = null;
                    
                    if (!string.IsNullOrEmpty(user.FcmToken))
                    {
                        var banData = new Dictionary<string, string>
                        {
                            { "type", "account_locked" },
                            { "lockout_end", lockoutEndDate.ToString("o") }
                        };
                        try { await _pushService.SendPushNotificationAsync(user.FcmToken, "", "", banData); } catch { }
                    }
                    break;

                case UserStatus.Rejected:
                    _uploadVoice.DeleteVoice(user.VoiceVerificationPath);
                    user.VoiceVerificationPath = null;

                    // Invalidate refresh token so rejected user can't silently refresh.
                    user.RefreshToken = null;

                    if (!string.IsNullOrEmpty(user.FcmToken))
                    {
                        var rejectData = new Dictionary<string, string>
                        {
                            { "type", "account_rejected" }
                        };
                        try { await _pushService.SendPushNotificationAsync(user.FcmToken, "", "", rejectData); } catch { }
                    }
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
                try
                {
                    await SendVerificationEmailAsync(user, newStatus);
                }
                catch { }

                if (newStatus == UserStatus.ReRecord && !string.IsNullOrEmpty(user.FcmToken))
                {
                    var data = new Dictionary<string, string> { { "type", "reRecord" } };
                    try { await _pushService.SendPushNotificationAsync(user.FcmToken, "إعادة تسجيل صوتي 🎙️", "نعتذر منك، نحتاج منك إعادة تسجيل المقطع الصوتي الخاص بك بوضوح أكبر.", data); } catch { }
                }

                return Success($"User status changed from {oldStatus} to {newStatus}");
            }

            return BadRequest<string>("Failed to change status");
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

        public async Task<Response<IEnumerable<UserDto>>> GetAllUsersAsync(string? search, int page = 1, int pageSize = 10)
        {
            var (totalCount, users) = await _userRepository.GetPaginatedUsersWithRolesAsync(search, page, pageSize, _baseUrl);

            var response = Success(users);
            response.Meta = new { TotalCount = totalCount, CurrentPage = page, PageSize = pageSize };

            return response;
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
            
            // SECURITY: Invalidate refresh token to prevent session resurrection.
            user.RefreshToken = null;
            user.RefreshTokenExpiryTime = DateTime.UtcNow;

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