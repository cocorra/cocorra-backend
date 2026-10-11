using Cocorra.DAL.Data;
using Cocorra.DAL.DTOS.LogsDto;
using Cocorra.DAL.Models;
using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Cocorra.DAL.Repository.SystemLogRepository
{
    public class SystemLogRepository : ISystemLogRepository
    {
        private readonly AppDbContext _context;

        public SystemLogRepository(AppDbContext context)
        {
            _context = context;
        }

        public async Task<(int TotalCount, List<SystemLog> Items)> GetPagedAsync(SystemLogQueryDto query)
        {
            var logs = _context.SystemLogs.AsNoTracking().AsQueryable();

            if (!string.IsNullOrEmpty(query.Level))
                logs = logs.Where(l => l.Level == query.Level);

            var search = query.Search;
            if (!string.IsNullOrEmpty(search))
                logs = logs.Where(l => l.Message.Contains(search) || l.Category.Contains(search));

            if (query.From.HasValue)
                logs = logs.Where(l => l.TimestampUtc >= query.From.Value);

            if (query.To.HasValue)
                logs = logs.Where(l => l.TimestampUtc < query.To.Value);

            var totalCount = await logs.CountAsync();

            var items = await logs
                .OrderByDescending(l => l.TimestampUtc)
                .ThenByDescending(l => l.Id)
                .Skip((query.PageNumber - 1) * query.PageSize)
                .Take(query.PageSize)
                .ToListAsync();

            return (totalCount, items);
        }
    }
}
