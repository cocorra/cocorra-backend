using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using Cocorra.BLL.Base;
using Cocorra.BLL.Services.SystemLogs;
using Cocorra.DAL.DTOS.LogsDto;
using Cocorra.DAL.Models;
using Cocorra.DAL.Repository.AuditLogRepository;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Cocorra.BLL.Services.AuditLogs
{
    public class AuditLogService : ResponseHandler, IAuditLogService
    {
        private const int MaxEmailLength = 256;
        private const int MaxRoleLength = 64;
        private const int MaxTargetDisplayLength = 256;
        private const int MaxIpAddressLength = 64;
        private const int MaxUserAgentLength = 512;

        // The dashboard reads details keys as-is, so they are stored in the API's casing.
        private static readonly JsonSerializerOptions DetailsJsonOptions = new(JsonSerializerDefaults.Web);

        private readonly IAuditLogRepository _auditLogRepo;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly ILogger<AuditLogService> _logger;

        public AuditLogService(
            IAuditLogRepository auditLogRepo,
            IHttpContextAccessor httpContextAccessor,
            ILogger<AuditLogService> logger)
        {
            _auditLogRepo = auditLogRepo;
            _httpContextAccessor = httpContextAccessor;
            _logger = logger;
        }

        public async Task RecordAsync(AuditEntry entry)
        {
            try
            {
                var auditLog = new AuditLog
                {
                    OccurredAtUtc = DateTime.UtcNow,
                    Action = entry.Action,
                    ActorUserId = NullIfEmpty(entry.ActorUserId),
                    TargetUserId = NullIfEmpty(entry.TargetUserId),
                    TargetDisplay = LogText.Truncate(entry.TargetDisplay, MaxTargetDisplayLength),
                    DetailsJson = entry.Details is null ? null : JsonSerializer.Serialize(entry.Details, DetailsJsonOptions)
                };
                ApplyRequestContext(auditLog);

                await _auditLogRepo.AddAsync(auditLog);
            }
            catch (Exception ex)
            {
                // The audited change is already committed. Failing the request now would report an
                // error for something that happened and invite a retry of it, so the gap in the
                // trail is surfaced through the error log instead.
                _logger.LogError(ex,
                    "Failed to write audit log {Action} by {ActorUserId} on {TargetUserId}.",
                    entry.Action, entry.ActorUserId, entry.TargetUserId);
            }
        }

        public async Task<Response<IEnumerable<AuditLogDto>>> GetAuditLogsAsync(AuditLogQueryDto query)
        {
            if (!string.IsNullOrEmpty(query.Action) && !AuditActions.All.Contains(query.Action))
                return BadRequest<IEnumerable<AuditLogDto>>(
                    $"Invalid action. Allowed values: {string.Join(", ", AuditActions.All)}.");

            var from = LogText.ToUtc(query.From);
            var to = LogText.ToUtc(query.To);
            if (from > to)
                return BadRequest<IEnumerable<AuditLogDto>>("'from' must not be later than 'to'.");

            var (totalCount, items) = await _auditLogRepo.GetPagedAsync(new AuditLogQueryDto
            {
                Action = string.IsNullOrEmpty(query.Action) ? null : query.Action,
                ActorUserId = query.ActorUserId,
                TargetUserId = query.TargetUserId,
                Search = LogText.NormalizeSearch(query.Search),
                From = from,
                To = to,
                PageNumber = query.PageNumber,
                PageSize = query.PageSize
            });

            return Paginated(items.Select(ToDto).ToList(), totalCount, query.PageNumber, query.PageSize);
        }

        // An empty Guid means the caller could not resolve the user, not an identity worth storing.
        private static Guid? NullIfEmpty(Guid? id) => id == Guid.Empty ? null : id;

        private void ApplyRequestContext(AuditLog auditLog)
        {
            var httpContext = _httpContextAccessor.HttpContext;
            if (httpContext is null)
            {
                return;
            }

            var user = httpContext.User;
            var roles = string.Join(",", user.FindAll(ClaimTypes.Role).Select(c => c.Value));

            auditLog.ActorEmail = LogText.Truncate(user.FindFirst(ClaimTypes.Email)?.Value, MaxEmailLength);
            auditLog.ActorRole = roles.Length == 0 ? null : LogText.Truncate(roles, MaxRoleLength);
            // Already the real client address: UseForwardedHeaders runs first in the pipeline.
            auditLog.IpAddress = LogText.Truncate(httpContext.Connection.RemoteIpAddress?.ToString(), MaxIpAddressLength);

            var userAgent = httpContext.Request.Headers.UserAgent.ToString();
            auditLog.UserAgent = userAgent.Length == 0 ? null : LogText.Truncate(userAgent, MaxUserAgentLength);
        }

        private AuditLogDto ToDto(AuditLog log) => new()
        {
            Id = log.Id,
            OccurredAtUtc = LogText.AsUtc(log.OccurredAtUtc),
            Action = log.Action,
            ActorUserId = log.ActorUserId,
            ActorEmail = log.ActorEmail,
            ActorRole = log.ActorRole,
            TargetUserId = log.TargetUserId,
            TargetDisplay = log.TargetDisplay,
            Details = ParseDetails(log),
            IpAddress = log.IpAddress,
            UserAgent = log.UserAgent
        };

        private JsonElement? ParseDetails(AuditLog log)
        {
            if (string.IsNullOrEmpty(log.DetailsJson))
            {
                return null;
            }

            try
            {
                using var document = JsonDocument.Parse(log.DetailsJson);
                return document.RootElement.Clone();
            }
            catch (JsonException ex)
            {
                // One corrupt row must not fail the whole page; it is listed without details.
                _logger.LogWarning(ex, "Audit log {AuditLogId} has malformed DetailsJson.", log.Id);
                return null;
            }
        }
    }
}
