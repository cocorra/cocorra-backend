using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Cocorra.DAL.DTOS.BlockedDevicesDto;
using Cocorra.DAL.Repository.BlockedDevicesRepository;

namespace Cocorra.BLL.Services.BlockedDevicesService
{
    public class BlockedDevicesService : IBlockedDevicesService
    {
        private readonly IBlockedDevicesRepository _blockedDevicesRepository;

        public BlockedDevicesService(IBlockedDevicesRepository blockedDevicesRepository)
        {
            _blockedDevicesRepository = blockedDevicesRepository;
        }

        public async Task<bool> BlockDeviceAsync(BlockedDevicesDto device)
        {
            if (device == null || string.IsNullOrWhiteSpace(device.DeviceId))
                return false;

            var existingDevice = await _blockedDevicesRepository.GetByDeviceIdAsync(device.DeviceId);

            if (existingDevice != null)
            {
                // إذا كان الجهاز مسجلاً ومحظوراً بالفعل، العملية تعتبر ناجحة
                if (existingDevice.IsBlocked)
                    return true;

                // إذا كان مسجلاً ولكنه غير محظور، يجب تحديث حالته
                existingDevice.IsBlocked = true;
                return await _blockedDevicesRepository.UpdateBlockedDeviceAsync(existingDevice);
            }

            var blockedDevice = new DAL.Models.BlockedDevices
            {
                DeviceId = device.DeviceId,
                DeviceName = device.DeviceName,
                DeviceModel = device.DeviceModel,
                DeviceType = device.DeviceType,
                DeviceOs = device.DeviceOs,
                IsBlocked = true,
                ApplicationUserId = device.ApplicationUserId 
            };

            return await _blockedDevicesRepository.AddBlockedDeviceAsync(blockedDevice);
        }

        public async Task<List<BlockedDevicesDto>> GetUserBlockedDevicesAsync(Guid userId)
        {
            if (userId == Guid.Empty)
                return new List<BlockedDevicesDto>();

            var blockedDevices = await _blockedDevicesRepository.GetBlockedDevicesByUserAsync(userId);

            return blockedDevices.Select(d => new BlockedDevicesDto
            {
                DeviceId = d.DeviceId ?? string.Empty,
                DeviceName = d.DeviceName ?? string.Empty,
                DeviceModel = d.DeviceModel ?? string.Empty,
                DeviceType = d.DeviceType ?? string.Empty,
                DeviceOs = d.DeviceOs ?? string.Empty,
                ApplicationUserId = d.ApplicationUserId,
                BlockedAt = d.CreatedAt
            }).ToList();
        }

        public async Task<bool> IsDeviceBlockedAsync(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId))
                return false;

            return await _blockedDevicesRepository.IsDeviceBlockedAsync(deviceId);
        }

        public async Task<bool> UnblockDeviceAsync(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId))
                return false;

            return await _blockedDevicesRepository.RemoveBlockedDeviceAsync(deviceId);
        }
    }
}