using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Cocorra.DAL.DTOS.Auth;
using Cocorra.DAL.DTOS.BlockedDevicesDto;

namespace Cocorra.BLL.Services.BlockedDevicesService
{
    public interface IBlockedDevicesService
    {
        Task<bool> IsDeviceBlockedAsync(string deviceId);
        Task<bool> BlockDeviceAsync(BlockedDevicesDto device);
        Task<bool> UnblockDeviceAsync(string deviceId);
        Task<List<BlockedDevicesDto>> GetUserBlockedDevicesAsync(Guid userId);

        /// <summary>
        /// Records the device a user just authenticated from, so a later email-only ban has
        /// something to block. Best-effort: returns false instead of throwing, because a
        /// bookkeeping failure must never cost the user their login.
        /// </summary>
        Task<bool> RegisterDeviceAsync(Guid userId, DeviceInfoDto? device);
    }
}
