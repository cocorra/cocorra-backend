using Cocorra.DAL.DTOS.AdminDto;
using Cocorra.DAL.Enums;
using Cocorra.BLL.Base;


namespace Cocorra.BLL.Services.AdminService
{
    public interface IAdminService
    {
        Task<PagedResponse<UserDto>> GetAllUsersAsync(string? search, int page = 1, int pageSize = 10);
        Task<Response<UserDto>> GetUserByIdAsync(Guid userId);
        Task<Response<DashboardStatsDto>> GetDashboardStatsAsync();
        /// <summary>
        /// AN-011: adminId and isBulk are required so user_status_changed can record WHO made
        /// the change. The acting admin's identity already exists in the controller; before this
        /// it was dropped at exactly this boundary and recorded nowhere.
        /// </summary>
        Task<Response<string>> ChangeUserStatusAsync(Guid userId, UserStatus newStatus, Guid adminId, bool isBulk = false);
        Task<Response<BulkChangeStatusResultDto>> BulkChangeUserStatusAsync(BulkChangeStatusDto model, Guid adminId);
        /// <summary>
        /// Hard ban: bans the account behind the email and blocks every device the registry
        /// has seen that account authenticate from. Takes no device input by design — see
        /// <see cref="BlockDeviceAndEmailDto"/>. adminId is required for attribution (AN-011).
        /// </summary>
        Task<Response<BlockDeviceAndEmailResultDto>> BlockDeviceAndEmailAsync(BlockDeviceAndEmailDto model, Guid adminId);
    }
}
