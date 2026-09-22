using Cocorra.DAL.Data;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;
using Cocorra.DAL.Repository.GenericRepository;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Cocorra.DAL.Repository.RoomRepository
{
    public class RoomRepository : GenericRepositoryAsync<Room>, IRoomRepository
    {
        public RoomRepository(AppDbContext dbContext) : base(dbContext)
        {
        }

        public async Task<RoomParticipant?> GetParticipantAsync(Guid roomId, Guid userId)
        {
            return await _dbContext.RoomParticipants
                                 .Include(p => p.User)
                                 .FirstOrDefaultAsync(p => p.RoomId == roomId && p.UserId == userId);
        }

        public async Task<List<RoomParticipant>> GetRoomParticipantsAsync(Guid roomId)
        {
            return await _dbContext.RoomParticipants
                                 .Where(p => p.RoomId == roomId)
                                 .Include(p => p.User)
                                 .OrderByDescending(p => p.IsOnStage)
                                 .ThenBy(p => p.JoinedAt)
                                 .ToListAsync();
        }
        public async Task<List<Room>> GetActiveRoomsAsync(RoomCategory? categoryId = null, int pageNumber = 1, int pageSize = 20)
        {
            var query = _dbContext.Rooms
                .AsNoTracking()
                .Include(r => r.Host)
                .Include(r => r.Participants)
                .Where(r => r.Status == RoomStatus.Live || r.Status == RoomStatus.Scheduled);

            if (categoryId.HasValue)
            {
                query = query.Where(r => r.Category == categoryId.Value);
            }

            return await query
                .OrderBy(r => r.Status == RoomStatus.Live ? 0 : 1)
                .ThenByDescending(r => r.Participants.Count(p => p.Status == ParticipantStatus.Active))
                .ThenBy(r => r.StartDate)
                .Skip((pageNumber - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();
        }

        public async Task<RoomReminder?> GetRoomReminderAsync(Guid roomId, Guid userId)
        {
            return await _dbContext.RoomReminders
                .FirstOrDefaultAsync(rr => rr.RoomId == roomId && rr.UserId == userId);
        }

        public async Task<int> GetRoomRemindersCountAsync(Guid roomId)
        {
            return await _dbContext.RoomReminders
                .CountAsync(rr => rr.RoomId == roomId);
        }
        public async Task<List<RoomReminder>> GetRemindersByRoomIdAsync(Guid roomId)
        {
            return await _dbContext.RoomReminders.Where(rr => rr.RoomId == roomId).ToListAsync();
        }

        public async Task RemoveRemindersAsync(IEnumerable<RoomReminder> reminders)
        {
            _dbContext.RoomReminders.RemoveRange(reminders);
            await Task.CompletedTask; // بنمسحهم بس عشان خلاص الروم بدأت
        }

        public async Task AddNotificationsAsync(IEnumerable<Notification> notifications)
        {
            await _dbContext.Notifications.AddRangeAsync(notifications);
        }
        public async Task AddRoomReminderAsync(RoomReminder reminder)
        {
            await _dbContext.RoomReminders.AddAsync(reminder);
            await _dbContext.SaveChangesAsync(); // ممكن تخلي الـ Save في الـ Service لو حابب
        }

        public async Task RemoveRoomReminderAsync(RoomReminder reminder)
        {
            _dbContext.RoomReminders.Remove(reminder);
            await _dbContext.SaveChangesAsync();
        }

        public async Task<List<RoomParticipant>> GetStageSpeakersAsync(Guid roomId)
        {
            return await _dbContext.RoomParticipants
                                 .Where(p => p.RoomId == roomId && p.IsOnStage == true)
                                 .Include(p => p.User)
                                 .ToListAsync();
        }

        // --- تنفيذ النواقص ---

        public async Task AddParticipantAsync(RoomParticipant participant)
        {
            await _dbContext.RoomParticipants.AddAsync(participant);
        }

        public async Task UpdateParticipantAsync(RoomParticipant participant)
        {
            _dbContext.RoomParticipants.Update(participant);
            await Task.CompletedTask;
        }

        public async Task RemoveParticipantAsync(RoomParticipant participant)
        {
            _dbContext.RoomParticipants.Remove(participant);
            await Task.CompletedTask;
        }

        public async Task<List<Room>> GetEndedRoomsAsync(int pageNumber = 1, int pageSize = 20)
        {
            return await _dbContext.Rooms
                .AsNoTracking()
                .Include(r => r.Host)
                .Include(r => r.Participants)
                .Where(r => r.Status == RoomStatus.Ended)
                .OrderByDescending(r => r.UpdatedAt)
                .Skip((pageNumber - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();
        }

        public async Task<List<Room>> GetRoomsWithExpiredHostGraceAsync(DateTime disconnectedBefore)
        {
            // Tracked, not AsNoTracking: the caller ends these rooms through the same context.
            return await _dbContext.Rooms
                .Where(r => r.Status == RoomStatus.Live
                            && r.HostDisconnectedAt != null
                            && r.HostDisconnectedAt < disconnectedBefore)
                .OrderBy(r => r.HostDisconnectedAt)
                .ToListAsync();
        }

        public async Task<List<Room>> GetLiveRoomsStartedBeforeAsync(DateTime wentLiveBefore)
        {
            // Tracked, not AsNoTracking: the caller ends these rooms through the same context.
            return await _dbContext.Rooms
                .Where(r => r.Status == RoomStatus.Live
                            && r.WentLiveAt != null
                            && r.WentLiveAt < wentLiveBefore)
                .OrderBy(r => r.WentLiveAt)
                .ToListAsync();
        }
    }
}