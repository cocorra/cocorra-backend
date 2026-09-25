using Cocorra.DAL.Data;
using Cocorra.DAL.DTOS.RoomInviteDto;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;
using Cocorra.DAL.Repository.GenericRepository;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Cocorra.DAL.Repository.RoomInviteRepository
{
    public class RoomInviteRepository : GenericRepositoryAsync<RoomInvite>, IRoomInviteRepository
    {
        private const int MaxCodeAttempts = 3;

        public RoomInviteRepository(AppDbContext dbContext) : base(dbContext)
        {
        }

        public async Task<RoomInvite?> GetByCodeAsync(string inviteCode)
        {
            return await _dbContext.RoomInvites
                .AsNoTracking()
                .FirstOrDefaultAsync(i => i.InviteCode == inviteCode);
        }

        public async Task<int> CountUsableAsync(Guid inviterUserId, Guid roomId, DateTime nowUtc)
        {
            return await _dbContext.RoomInvites
                .AsNoTracking()
                .CountAsync(i => i.InviterUserId == inviterUserId
                              && i.RoomId == roomId
                              && i.Status == RoomInviteStatus.Active
                              && i.ExpiresAt > nowUtc);
        }

        public async Task<RoomInvite> AddWithUniqueCodeAsync(RoomInvite invite, Func<string> generateCode)
        {
            for (var attempt = 1; ; attempt++)
            {
                invite.InviteCode = generateCode();
                await _dbContext.RoomInvites.AddAsync(invite);
                try
                {
                    await _dbContext.SaveChangesAsync();
                    return invite;
                }
                catch (DbUpdateException) when (attempt < MaxCodeAttempts)
                {
                    // A 128-bit collision is practically impossible, so this is belt and braces
                    // for the unique code index. Drop the failed insert and try a fresh code.
                    _dbContext.Entry(invite).State = EntityState.Detached;
                }
            }
        }

        public async Task<T> ExecuteInTransactionAsync<T>(Func<Task<(bool Commit, T Result)>> work)
        {
            // EnableRetryOnFailure is on in production, and the retrying strategy refuses a
            // user-initiated transaction unless the whole unit runs through it.
            var strategy = _dbContext.Database.CreateExecutionStrategy();
            return await strategy.ExecuteAsync(async () =>
            {
                await using var transaction = await _dbContext.Database.BeginTransactionAsync();
                try
                {
                    var (commit, result) = await work();
                    if (commit)
                    {
                        await transaction.CommitAsync();
                    }
                    else
                    {
                        await transaction.RollbackAsync();
                        _dbContext.ChangeTracker.Clear();
                    }
                    return result;
                }
                catch
                {
                    // Also covers a retry by the execution strategy: it must not start over
                    // with entities still tracked from the attempt that was rolled back.
                    _dbContext.ChangeTracker.Clear();
                    throw;
                }
            });
        }

        public async Task<bool> TryConsumeAsync(Guid inviteId, Guid userId, DateTime nowUtc)
        {
            // A single conditional UPDATE: the row lock it takes is held until the surrounding
            // transaction ends, so a concurrent accept blocks here and then re-evaluates the
            // predicate — finding Status = Used and updating nothing.
            var rows = await _dbContext.RoomInvites
                .Where(i => i.Id == inviteId
                         && i.Status == RoomInviteStatus.Active
                         && i.ExpiresAt > nowUtc)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(i => i.Status, RoomInviteStatus.Used)
                    .SetProperty(i => i.UsedAt, (DateTime?)nowUtc)
                    .SetProperty(i => i.UsedByUserId, (Guid?)userId)
                    .SetProperty(i => i.UpdatedAt, (DateTime?)nowUtc));

            return rows == 1;
        }

        public async Task<bool> TryRevokeAsync(Guid inviteId, Guid revokedByUserId, DateTime nowUtc)
        {
            var rows = await _dbContext.RoomInvites
                .Where(i => i.Id == inviteId && i.Status == RoomInviteStatus.Active)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(i => i.Status, RoomInviteStatus.Revoked)
                    .SetProperty(i => i.RevokedAt, (DateTime?)nowUtc)
                    .SetProperty(i => i.RevokedByUserId, (Guid?)revokedByUserId)
                    .SetProperty(i => i.UpdatedAt, (DateTime?)nowUtc));

            return rows == 1;
        }

        public async Task<RoomInviteStatsDto> GetLifecycleStatsAsync(DateTime? fromUtc, DateTime? toUtc, Guid? roomId, Guid? inviterUserId, DateTime nowUtc)
        {
            var invites = _dbContext.RoomInvites.AsNoTracking().AsQueryable();

            if (fromUtc.HasValue)
                invites = invites.Where(i => i.CreatedAt >= fromUtc.Value);
            if (toUtc.HasValue)
                invites = invites.Where(i => i.CreatedAt <= toUtc.Value);
            if (roomId.HasValue)
                invites = invites.Where(i => i.RoomId == roomId.Value);
            if (inviterUserId.HasValue)
                invites = invites.Where(i => i.InviterUserId == inviterUserId.Value);

            // Effective status, same rule as the service: an Active invite counts as Expired
            // once its time is up or its room is no longer joinable. Invites of deleted rooms
            // went with the room (cascade), so the inner join drops nothing.
            var counts = await invites
                .Join(_dbContext.Rooms, i => i.RoomId, r => r.Id, (i, r) => new
                {
                    Effective = i.Status == RoomInviteStatus.Active
                             && (i.ExpiresAt <= nowUtc
                              || r.Status == RoomStatus.Ended
                              || r.Status == RoomStatus.Cancelled)
                        ? RoomInviteStatus.Expired
                        : i.Status
                })
                .GroupBy(x => x.Effective)
                .Select(g => new { Status = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.Status, x => x.Count);

            var uniqueAcceptedUsers = await invites
                .Where(i => i.Status == RoomInviteStatus.Used && i.UsedByUserId != null)
                .Select(i => i.UsedByUserId)
                .Distinct()
                .CountAsync();

            int Get(RoomInviteStatus s) => counts.TryGetValue(s, out var c) ? c : 0;

            return new RoomInviteStatsDto
            {
                TotalCreated = counts.Values.Sum(),
                Active = Get(RoomInviteStatus.Active),
                Used = Get(RoomInviteStatus.Used),
                Expired = Get(RoomInviteStatus.Expired),
                Revoked = Get(RoomInviteStatus.Revoked),
                SuccessfulAcceptances = Get(RoomInviteStatus.Used),
                UniqueAcceptedUsers = uniqueAcceptedUsers
            };
        }

        public async Task<Dictionary<string, long>> CountEventsAsync(IReadOnlyCollection<string> eventTypes, DateTime? fromUtc, DateTime? toUtc, Guid? roomId)
        {
            // Inclusive on both ends, matching AnalyticsRepository's window semantics.
            var events = _dbContext.UserEvents
                .AsNoTracking()
                .Where(e => eventTypes.Contains(e.EventType));

            if (fromUtc.HasValue)
                events = events.Where(e => e.OccurredAtUtc >= fromUtc.Value);
            if (toUtc.HasValue)
                events = events.Where(e => e.OccurredAtUtc <= toUtc.Value);
            if (roomId.HasValue)
                events = events.Where(e => e.RoomId == roomId.Value);

            return await events
                .GroupBy(e => e.EventType)
                .Select(g => new { EventType = g.Key, Count = g.LongCount() })
                .ToDictionaryAsync(x => x.EventType, x => x.Count);
        }
    }
}
