using Cocorra.DAL.Data;
using Cocorra.DAL.DTOS.LogsDto;
using Cocorra.DAL.Models;
using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Cocorra.DAL.Repository.AuditLogRepository
{
    public class AuditLogRepository : IAuditLogRepository
    {
        private readonly AppDbContext _context;

        public AuditLogRepository(AppDbContext context)
        {
            _context = context;
        }

        public async Task AddAsync(AuditLog auditLog)
        {
            var entry = _context.AuditLogs.Add(auditLog);
            try
            {
                await _context.SaveChangesAsync();
            }
            catch
            {
                entry.State = EntityState.Detached;
                throw;
            }
        }

        public async Task<(int TotalCount, List<AuditLog> Items)> GetPagedAsync(AuditLogQueryDto query)
        {
            var logs = _context.AuditLogs.AsNoTracking().AsQueryable();

            if (!string.IsNullOrEmpty(query.Action))
                logs = logs.Where(a => a.Action == query.Action);

            if (query.ActorUserId.HasValue)
                logs = logs.Where(a => a.ActorUserId == query.ActorUserId.Value);

            if (query.TargetUserId.HasValue)
                logs = logs.Where(a => a.TargetUserId == query.TargetUserId.Value);

            var search = query.Search;
            if (!string.IsNullOrEmpty(search))
                logs = logs.Where(a =>
                    (a.ActorEmail != null && a.ActorEmail.Contains(search))
                    || (a.TargetDisplay != null && a.TargetDisplay.Contains(search)));

            if (query.From.HasValue)
                logs = logs.Where(a => a.OccurredAtUtc >= query.From.Value);

            if (query.To.HasValue)
                logs = logs.Where(a => a.OccurredAtUtc < query.To.Value);

            var totalCount = await logs.CountAsync();

            var items = await logs
                .OrderByDescending(a => a.OccurredAtUtc)
                .ThenByDescending(a => a.Id)
                .Skip((query.PageNumber - 1) * query.PageSize)
                .Take(query.PageSize)
                .ToListAsync();

            return (totalCount, items);
        }
    }
}
