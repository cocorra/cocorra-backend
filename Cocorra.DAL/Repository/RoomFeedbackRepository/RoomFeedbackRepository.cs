using Cocorra.DAL.Data;
using Cocorra.DAL.Models;
using Cocorra.DAL.Repository.GenericRepository;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Cocorra.DAL.Repository.RoomFeedbackRepository
{
    public class RoomFeedbackRepository : GenericRepositoryAsync<RoomFeedback>, IRoomFeedbackRepository
    {
        public RoomFeedbackRepository(AppDbContext dbContext) : base(dbContext)
        {
        }

        public async Task<RoomFeedback?> GetByRoomAndUserAsync(Guid roomId, Guid userId)
        {
            return await _dbContext.RoomFeedbacks
                .AsNoTracking()
                .FirstOrDefaultAsync(f => f.RoomId == roomId && f.UserId == userId);
        }

        public async Task<(RoomFeedback Feedback, bool IsUpdate)> UpsertAsync(Guid roomId, Guid userId, int rating, string? comment)
        {
            var existing = await _dbContext.RoomFeedbacks
                .FirstOrDefaultAsync(f => f.RoomId == roomId && f.UserId == userId);

            if (existing != null)
            {
                await ApplyUpdateAsync(existing, rating, comment);
                return (existing, true);
            }

            var feedback = new RoomFeedback
            {
                RoomId = roomId,
                UserId = userId,
                Rating = rating,
                Comment = comment,
                CreatedAt = DateTime.UtcNow
            };

            await _dbContext.RoomFeedbacks.AddAsync(feedback);
            try
            {
                await _dbContext.SaveChangesAsync();
                return (feedback, false);
            }
            catch (DbUpdateException)
            {
                // A concurrent submit (double-tap) inserted the row between the lookup and the
                // save, tripping the unique (RoomId, UserId) index. Drop our insert and apply
                // this submission as an update to the row that won.
                _dbContext.Entry(feedback).State = EntityState.Detached;

                var winner = await _dbContext.RoomFeedbacks
                    .FirstOrDefaultAsync(f => f.RoomId == roomId && f.UserId == userId);
                if (winner == null) throw; // Not a uniqueness race — surface the real failure.

                await ApplyUpdateAsync(winner, rating, comment);
                return (winner, true);
            }
        }

        private async Task ApplyUpdateAsync(RoomFeedback feedback, int rating, string? comment)
        {
            feedback.Rating = rating;
            feedback.Comment = comment;
            feedback.UpdatedAt = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync();
        }

        public async Task<Dictionary<int, int>> GetRatingCountsAsync(Guid roomId)
        {
            return await _dbContext.RoomFeedbacks
                .AsNoTracking()
                .Where(f => f.RoomId == roomId)
                .GroupBy(f => f.Rating)
                .Select(g => new { Rating = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.Rating, x => x.Count);
        }

        public async Task<(int TotalCount, List<RoomFeedback> Items)> GetPagedAsync(Guid? roomId, int? rating, int pageNumber, int pageSize)
        {
            var query = _dbContext.RoomFeedbacks.AsNoTracking().AsQueryable();

            if (roomId.HasValue)
                query = query.Where(f => f.RoomId == roomId.Value);

            if (rating.HasValue)
                query = query.Where(f => f.Rating == rating.Value);

            var totalCount = await query.CountAsync();

            var items = await query
                .Include(f => f.Room)
                .Include(f => f.User)
                .OrderByDescending(f => f.CreatedAt)
                .ThenByDescending(f => f.Id)
                .Skip((pageNumber - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            return (totalCount, items);
        }
    }
}
