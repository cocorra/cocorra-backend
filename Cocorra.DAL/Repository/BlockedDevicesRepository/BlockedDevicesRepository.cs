using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Cocorra.DAL.Data;
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
            // التعديل هنا: استخدام FirstOrDefaultAsync بدلاً من FindAsync
            return await _context.BlockedDevices
                .FirstOrDefaultAsync(d => d.DeviceId == deviceId);
        }

        public async Task<bool> IsDeviceBlockedAsync(string deviceId)
        {
            // التعديل هنا: التأكد من أن الجهاز موجود وحالته "محظور"
            return await _context.BlockedDevices
                .AnyAsync(d => d.DeviceId == deviceId && d.IsBlocked);
        }

        public async Task<bool> AddBlockedDeviceAsync(BlockedDevices device)
        {
            await _context.BlockedDevices.AddAsync(device);
            await _context.SaveChangesAsync();
            return true;
        }

        public async Task<bool> RemoveBlockedDeviceAsync(string deviceId)
        {
            var device = await GetByDeviceIdAsync(deviceId);
            if (device != null)
            {
                _context.BlockedDevices.Remove(device);
                await _context.SaveChangesAsync();
                return true;
            }
            return false;
        }

        public async Task<bool> UpdateBlockedDeviceAsync(BlockedDevices device)
        {
            _context.BlockedDevices.Update(device);
            await _context.SaveChangesAsync();
            return true;
        }

        public async Task<List<BlockedDevices>> GetBlockedDevicesByUserAsync(Guid userId)
        {
            // إضافة AsNoTracking لتحسين الأداء لأننا نقرأ البيانات فقط ولن نعدلها هنا
            return await _context.BlockedDevices
                .AsNoTracking()
                .Where(d => d.ApplicationUserId == userId)
                .ToListAsync();
        }
    }
}