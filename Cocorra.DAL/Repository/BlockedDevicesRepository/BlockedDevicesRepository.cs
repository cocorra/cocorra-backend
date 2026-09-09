using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Cocorra.DAL.Data;
using Cocorra.DAL.DTOS.Auth;
using Cocorra.DAL.Models;
using Microsoft.EntityFrameworkCore;

namespace Cocorra.DAL.Repository.BlockedDevicesRepository
{
    public class BlockedDevicesRepository : IBlockedDevicesRepository
    {
        private readonly AppDbContext _context;

        public BlockedDevicesRepository(AppDbContext context)
        {
            _context = context;
        }

        public async Task<BlockedDevices?> GetByDeviceIdAsync(string deviceId)
        {
            // A device id can appear under several users (shared handset, ban evasion),
            // so this returns an arbitrary one of them. Prefer GetByUserAndDeviceIdAsync
            // when the user is known, and IsDeviceBlockedAsync when asking about enforcement.
            return await _context.BlockedDevices
                .FirstOrDefaultAsync(d => d.DeviceId == deviceId);
        }

        public async Task<BlockedDevices?> GetByUserAndDeviceIdAsync(Guid userId, string deviceId)
        {
            return await _context.BlockedDevices
                .FirstOrDefaultAsync(d => d.ApplicationUserId == userId && d.DeviceId == deviceId);
        }

        public async Task<bool> IsDeviceBlockedAsync(string deviceId)
        {
            // Any blocked row for this device id blocks it, whichever account it hangs off.
            return await _context.BlockedDevices
                .AnyAsync(d => d.DeviceId == deviceId && d.IsBlocked);
        }

        public async Task<bool> AddBlockedDeviceAsync(BlockedDevices device)
        {
            await _context.BlockedDevices.AddAsync(device);
            await _context.SaveChangesAsync();
            return true;
        }

        public async Task<bool> UpdateBlockedDeviceAsync(BlockedDevices device)
        {
            device.UpdatedAt = DateTime.UtcNow;
            _context.BlockedDevices.Update(device);
            await _context.SaveChangesAsync();
            return true;
        }

        public async Task<bool> RegisterDeviceAsync(Guid userId, DeviceInfoDto device)
        {
            if (userId == Guid.Empty || string.IsNullOrWhiteSpace(device.DeviceId))
                return false;

            var existing = await GetByUserAndDeviceIdAsync(userId, device.DeviceId);
            if (existing != null)
            {
                Touch(existing, device);
                await _context.SaveChangesAsync();
                return true;
            }

            var row = new BlockedDevices
            {
                ApplicationUserId = userId,
                DeviceId = device.DeviceId,
                DeviceName = device.DeviceName,
                DeviceModel = device.DeviceModel,
                DeviceType = device.DeviceType,
                DeviceOs = device.DeviceOs,
                IsBlocked = false,
                LastSeenAt = DateTime.UtcNow
            };

            try
            {
                await _context.BlockedDevices.AddAsync(row);
                await _context.SaveChangesAsync();
                return true;
            }
            catch (DbUpdateException)
            {
                // Concurrent logins from the same device raced us to the unique
                // (ApplicationUserId, DeviceId) index. Drop our insert and update theirs.
                _context.Entry(row).State = EntityState.Detached;

                var winner = await GetByUserAndDeviceIdAsync(userId, device.DeviceId);
                if (winner == null) return false;

                Touch(winner, device);
                await _context.SaveChangesAsync();
                return true;
            }
        }

        /// <summary>
        /// Refreshes last-seen and device metadata. Never touches IsBlocked/BlockedAt —
        /// a banned user logging back in must not clear their own block.
        /// </summary>
        private static void Touch(BlockedDevices row, DeviceInfoDto device)
        {
            row.LastSeenAt = DateTime.UtcNow;
            row.UpdatedAt = DateTime.UtcNow;

            // Only overwrite metadata when the client actually sent it, so a request with
            // just X-Device-Id doesn't blank out a model/OS we already learned.
            if (!string.IsNullOrWhiteSpace(device.DeviceName)) row.DeviceName = device.DeviceName;
            if (!string.IsNullOrWhiteSpace(device.DeviceModel)) row.DeviceModel = device.DeviceModel;
            if (!string.IsNullOrWhiteSpace(device.DeviceType)) row.DeviceType = device.DeviceType;
            if (!string.IsNullOrWhiteSpace(device.DeviceOs)) row.DeviceOs = device.DeviceOs;
        }

        public async Task<int> BlockAllDevicesForUserAsync(Guid userId)
        {
            var devices = await _context.BlockedDevices
                .Where(d => d.ApplicationUserId == userId)
                .ToListAsync();

            var now = DateTime.UtcNow;
            foreach (var device in devices.Where(d => !d.IsBlocked))
            {
                device.IsBlocked = true;
                device.BlockedAt = now;      // preserved on already-blocked rows
                device.UpdatedAt = now;
            }

            await _context.SaveChangesAsync();
            return devices.Count(d => d.IsBlocked);
        }

        public async Task<bool> UnblockDeviceAsync(string deviceId)
        {
            var devices = await _context.BlockedDevices
                .Where(d => d.DeviceId == deviceId && d.IsBlocked)
                .ToListAsync();

            if (devices.Count == 0) return false;

            // Demote rather than delete: the row is still a valid registration, and
            // dropping it would lose the device history for this user.
            foreach (var device in devices)
            {
                device.IsBlocked = false;
                device.BlockedAt = null;
                device.UpdatedAt = DateTime.UtcNow;
            }

            await _context.SaveChangesAsync();
            return true;
        }

        public async Task<List<BlockedDevices>> GetBlockedDevicesByUserAsync(Guid userId)
        {
            return await _context.BlockedDevices
                .AsNoTracking()
                .Where(d => d.ApplicationUserId == userId && d.IsBlocked)
                .ToListAsync();
        }

        public async Task<List<BlockedDevices>> GetDevicesByUserAsync(Guid userId)
        {
            return await _context.BlockedDevices
                .AsNoTracking()
                .Where(d => d.ApplicationUserId == userId)
                .OrderByDescending(d => d.LastSeenAt)
                .ToListAsync();
        }
    }
}
