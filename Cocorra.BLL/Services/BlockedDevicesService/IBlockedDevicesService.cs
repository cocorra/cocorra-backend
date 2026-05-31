using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Cocorra.DAL.DTOS.BlockedDevicesDto;

namespace Cocorra.BLL.Services.BlockedDevicesService
{
    public interface IBlockedDevicesService
    {
        Task<bool> IsDeviceBlockedAsync(string deviceId);
        Task<bool> BlockDeviceAsync(BlockedDevicesDto device);
        Task<bool> UnblockDeviceAsync(string deviceId);
        Task<List<BlockedDevicesDto>> GetUserBlockedDevicesAsync(Guid userId);
    }
}