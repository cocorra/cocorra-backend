using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Cocorra.DAL.DTOS.Auth;
using Cocorra.DAL.DTOS.BlockedDevicesDto;
using Cocorra.DAL.Repository.BlockedDevicesRepository;
using Microsoft.Extensions.Logging;

namespace Cocorra.BLL.Services.BlockedDevicesService
{
    public class BlockedDevicesService : IBlockedDevicesService
    {
        private readonly IBlockedDevicesRepository _blockedDevicesRepository;
        private readonly ILogger<BlockedDevicesService> _logger;

        public BlockedDevicesService(
            IBlockedDevicesRepository blockedDevicesRepository,
            ILogger<BlockedDevicesService> logger)
        {
            _blockedDevicesRepository = blockedDevicesRepository;
            _logger = logger;
        }

        public async Task<bool> BlockDeviceAsync(BlockedDevicesDto device)
        {
            if (device == null || string.IsNullOrWhiteSpace(device.DeviceId))
                return false;

            var existingDevice = await _blockedDevicesRepository
                .GetByUserAndDeviceIdAsync(device.ApplicationUserId, device.DeviceId);

            if (existingDevice != null)
            {
                if (existingDevice.IsBlocked)
                    return true;

                existingDevice.IsBlocked = true;
                existingDevice.BlockedAt = DateTime.UtcNow;
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
                BlockedAt = DateTime.UtcNow,
                ApplicationUserId = device.ApplicationUserId
            };

            return await _blockedDevicesRepository.AddBlockedDeviceAsync(blockedDevice);
        }

        public async Task<bool> RegisterDeviceAsync(Guid userId, DeviceInfoDto? device)
        {
            if (device == null || userId == Guid.Empty || string.IsNullOrWhiteSpace(device.DeviceId))
                return false;

            try
            {
                return await _blockedDevicesRepository.RegisterDeviceAsync(userId, device);
            }
            catch (Exception ex)
            {
                // Deliberately swallowed: this runs inside the login/refresh path and its
                // only job is bookkeeping for a future ban. Log it and let auth succeed.
                _logger.LogWarning(ex,
                    "Failed to register device {DeviceId} for user {UserId}", device.DeviceId, userId);
                return false;
            }
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
                // Rows blocked before BlockedAt existed fall back to CreatedAt, which for
                // those rows *was* the block time.
                BlockedAt = d.BlockedAt ?? d.CreatedAt,
                LastSeenAt = d.LastSeenAt
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

            return await _blockedDevicesRepository.UnblockDeviceAsync(deviceId);
        }
    }
}
