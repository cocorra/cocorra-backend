using System.Collections.Generic;
using System.Threading.Tasks;
using Cocorra.BLL.Base;
using Cocorra.DAL.DTOS.LogsDto;

namespace Cocorra.BLL.Services.SystemLogs
{
    public interface ISystemLogService
    {
        /// <summary>A <see cref="PagedResponse{T}"/> on success; a BadRequest response for invalid filters.</summary>
        Task<Response<IEnumerable<SystemLogDto>>> GetSystemLogsAsync(SystemLogQueryDto query);
    }
}
