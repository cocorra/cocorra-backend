using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Cocorra.BLL.Base;
using Cocorra.DAL.DTOS.LogsDto;
using Cocorra.DAL.Models;
using Cocorra.DAL.Repository.SystemLogRepository;
using Microsoft.Extensions.Logging;

namespace Cocorra.BLL.Services.SystemLogs
{
    public class SystemLogService : ResponseHandler, ISystemLogService
    {
        // Only levels the sink can persist; anything lower never reaches the table.
        private static readonly string[] FilterableLevels =
        {
            nameof(LogLevel.Warning),
            nameof(LogLevel.Error),
            nameof(LogLevel.Critical)
        };

        private readonly ISystemLogRepository _systemLogRepo;

        public SystemLogService(ISystemLogRepository systemLogRepo)
        {
            _systemLogRepo = systemLogRepo;
        }

        public async Task<Response<IEnumerable<SystemLogDto>>> GetSystemLogsAsync(SystemLogQueryDto query)
        {
            if (!TryResolveLevel(query.Level, out var level))
                return BadRequest<IEnumerable<SystemLogDto>>(
                    $"Invalid level. Allowed values: {string.Join(", ", FilterableLevels)}.");

            var from = LogText.ToUtc(query.From);
            var to = LogText.ToUtc(query.To);
            if (from > to)
                return BadRequest<IEnumerable<SystemLogDto>>("'from' must not be later than 'to'.");

            var (totalCount, items) = await _systemLogRepo.GetPagedAsync(new SystemLogQueryDto
            {
                Level = level,
                Search = LogText.NormalizeSearch(query.Search),
                From = from,
                To = to,
                PageNumber = query.PageNumber,
                PageSize = query.PageSize
            });

            return Paginated(items.Select(ToDto).ToList(), totalCount, query.PageNumber, query.PageSize);
        }

        /// <summary>Maps the requested level onto its stored casing. An absent level is valid and means "all".</summary>
        private static bool TryResolveLevel(string? requested, out string? level)
        {
            level = null;
            if (string.IsNullOrEmpty(requested))
                return true;

            level = FilterableLevels.FirstOrDefault(l => l.Equals(requested, StringComparison.OrdinalIgnoreCase));
            return level is not null;
        }

        private static SystemLogDto ToDto(SystemLog log) => new()
        {
            Id = log.Id,
            TimestampUtc = LogText.AsUtc(log.TimestampUtc),
            Level = log.Level,
            Category = log.Category,
            EventId = log.EventId,
            Message = log.Message,
            Exception = log.Exception,
            TraceId = log.TraceId,
            RequestMethod = log.RequestMethod,
            RequestPath = log.RequestPath,
            UserId = log.UserId
        };
    }
}
