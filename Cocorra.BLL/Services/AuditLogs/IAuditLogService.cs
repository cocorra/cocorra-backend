using System.Collections.Generic;
using System.Threading.Tasks;
using Cocorra.BLL.Base;
using Cocorra.DAL.DTOS.LogsDto;

namespace Cocorra.BLL.Services.AuditLogs
{
    public interface IAuditLogService
    {
        /// <summary>
        /// Records a successful, already-persisted action, enriched with the acting request's
        /// identity and client details. Never throws: a failure is logged, not propagated.
        /// </summary>
        Task RecordAsync(AuditEntry entry);

        /// <summary>A <see cref="PagedResponse{T}"/> on success; a BadRequest response for invalid filters.</summary>
        Task<Response<IEnumerable<AuditLogDto>>> GetAuditLogsAsync(AuditLogQueryDto query);
    }
}
