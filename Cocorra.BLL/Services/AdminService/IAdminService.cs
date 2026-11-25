using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Cocorra.BLL.Base;
using Cocorra.DAL.DTOS.AdminDto;
using Cocorra.DAL.DTOS.StorageDto;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;

namespace Cocorra.BLL.Services.AdminService
{
    public interface IAdminService
    {
        Task<PagedResponse<UserDto>> GetAllUsersAsync(
            string? search,
            int page = 1,
            int pageSize = 10,
            bool callerIsAdmin = false);

        Task<Response<UserDto>> GetUserByIdAsync(Guid userId, bool callerIsAdmin = false);

        /// <summary>A fresh presigned URL to the user's voice recording; NotFound when there is none.</summary>
        Task<Response<PrivateFileUrlDto>> GetUserVoiceUrlAsync(Guid userId);

        Task<Response<DashboardStatsDto>> GetDashboardStatsAsync();

        Task<StatusChangeResponse> ChangeUserStatusAsync(
            Guid userId,
            UserStatus newStatus,
            string? reason,
            Guid actorUserId,
            bool callerIsAdmin,
            bool isBulk = false);

        /// <summary>
        /// Performs the status transition and all non-audit side effects (lockout, voice cleanup,
        /// security stamp rotation, events, notifications, email, push) so callers such as
        /// VoiceReviewService can write their own domain-specific audit entry.
        /// </summary>
        Task<StatusTransitionOutcome> ApplyStatusTransitionAsync(
            ApplicationUser user,
            UserStatus newStatus,
            IEnumerable<string> targetRoles,
            Guid actorUserId,
            bool isBulk = false);

        Task<Response<BulkChangeStatusResultDto>> BulkChangeUserStatusAsync(BulkChangeStatusDto model, Guid adminId);

        Task<Response<BlockDeviceAndEmailResultDto>> BlockDeviceAndEmailAsync(BlockDeviceAndEmailDto model, Guid adminId);

        Task<Response<BroadcastNotificationResultDto>> BroadcastNotificationAsync(SendBroadcastNotificationDto model, Guid adminId);
    }
}
