using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Cocorra.DAL.DTOS.Auth;
using Cocorra.DAL.Models;

namespace Cocorra.DAL.Repository.BlockedDevicesRepository
{
    public interface IBlockedDevicesRepository
    {
        Task<BlockedDevices?> GetByDeviceIdAsync(string deviceId);
        Task<BlockedDevices?> GetByUserAndDeviceIdAsync(Guid userId, string deviceId);
        Task<bool> IsDeviceBlockedAsync(string deviceId);
        Task<bool> AddBlockedDeviceAsync(BlockedDevices device);
        Task<bool> UpdateBlockedDeviceAsync(BlockedDevices device);

        /// <summary>
        /// Records that <paramref name="userId"/> authenticated from this device, creating the
        /// registry row on first sight and refreshing LastSeenAt/metadata after that.
        /// Never promotes a row to blocked, and never clears an existing block.
        /// </summary>
        Task<bool> RegisterDeviceAsync(Guid userId, DeviceInfoDto device);

        /// <summary>
        /// Blocks every device registered to the user. Returns how many devices are blocked
        /// for that user once the call completes (already-blocked rows included), so the
        /// caller can report a truthful count.
        /// </summary>
        Task<int> BlockAllDevicesForUserAsync(Guid userId);

        /// <summary>Demotes every row carrying this device id back to a plain registration.</summary>
        Task<bool> UnblockDeviceAsync(string deviceId);

        /// <summary>Blocked devices only — for the admin-facing "blocked devices" view.</summary>
        Task<List<BlockedDevices>> GetBlockedDevicesByUserAsync(Guid userId);

        /// <summary>Every device seen for this user, blocked or not.</summary>
        Task<List<BlockedDevices>> GetDevicesByUserAsync(Guid userId);
    }
}
