using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Cocorra.DAL.Models;

namespace Cocorra.DAL.Repository.BlockedDevicesRepository
{
    public interface IBlockedDevicesRepository
    {
        Task<BlockedDevices?> GetByDeviceIdAsync(string deviceId);
        Task<bool> IsDeviceBlockedAsync(string deviceId);
        Task<bool> AddBlockedDeviceAsync(BlockedDevices device);
        Task<bool> RemoveBlockedDeviceAsync(string deviceId);
        Task<bool> UpdateBlockedDeviceAsync(BlockedDevices device);
        Task<List<BlockedDevices>> GetBlockedDevicesByUserAsync(Guid userId);
    }
}