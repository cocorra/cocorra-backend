using Cocorra.DAL.DTOS.LogsDto;
using Cocorra.DAL.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Cocorra.DAL.Repository.SystemLogRepository
{
    public interface ISystemLogRepository
    {
        /// <summary>Newest first. Expects a query whose filters the caller has already validated.</summary>
        Task<(int TotalCount, List<SystemLog> Items)> GetPagedAsync(SystemLogQueryDto query);
    }
}
