using Cocorra.BLL.Services.AuditLogs;
using Cocorra.BLL.Services.SystemLogs;
using Cocorra.DAL.AppMetaData;
using Cocorra.DAL.DTOS.LogsDto;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Threading.Tasks;

namespace Cocorra.API.Controllers
{
    [ApiController]
    [Authorize(Roles = "Admin")]
    public class AdminLogsController : ControllerBase
    {
        private const int MaxPageSize = 50;

        private readonly ISystemLogService _systemLogService;
        private readonly IAuditLogService _auditLogService;

        public AdminLogsController(ISystemLogService systemLogService, IAuditLogService auditLogService)
        {
            _systemLogService = systemLogService;
            _auditLogService = auditLogService;
        }

        [HttpGet(Router.AdminRouting.SystemLogs)]
        public async Task<IActionResult> GetSystemLogs([FromQuery] SystemLogQueryDto query)
        {
            query.PageNumber = Math.Max(1, query.PageNumber);
            query.PageSize = Math.Clamp(query.PageSize, 1, MaxPageSize);

            var result = await _systemLogService.GetSystemLogsAsync(query);
            return StatusCode((int)result.StatusCode, result);
        }

        [HttpGet(Router.AdminRouting.AuditLogs)]
        public async Task<IActionResult> GetAuditLogs([FromQuery] AuditLogQueryDto query)
        {
            query.PageNumber = Math.Max(1, query.PageNumber);
            query.PageSize = Math.Clamp(query.PageSize, 1, MaxPageSize);

            var result = await _auditLogService.GetAuditLogsAsync(query);
            return StatusCode((int)result.StatusCode, result);
        }
    }
}
