using Cocorra.DAL.DTOS.LogsDto;
using Cocorra.DAL.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Cocorra.DAL.Repository.AuditLogRepository
{
    public interface IAuditLogRepository
    {
        /// <summary>
        /// Saves the entry. On failure the entry is detached before rethrowing, so it cannot ride
        /// along on a later SaveChanges of the caller's shared, scoped context.
        /// </summary>
        Task AddAsync(AuditLog auditLog);

        /// <summary>Newest first. Expects a query whose filters the caller has already validated.</summary>
        Task<(int TotalCount, List<AuditLog> Items)> GetPagedAsync(AuditLogQueryDto query);
    }
}
