using System;
using System.Linq;
using System.Threading.Tasks;
using Cocorra.BLL.Base;
using Cocorra.BLL.Services.AuditLogs;
using Cocorra.DAL.Data;
using Cocorra.DAL.DTOS.NotificationDto;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cocorra.BLL.Services.NotificationService
{
    public class BroadcastNotificationService : ResponseHandler, IBroadcastNotificationService
    {
        internal const int MaxTitleLength = 100;
        internal const int MaxMessageLength = 500;

        /// <summary>Rows per SaveChanges, so a large user base is not one huge tracked batch.</summary>
        internal const int InsertBatchSize = 1000;

        private readonly AppDbContext _context;
        private readonly IBroadcastPushQueue _pushQueue;
        private readonly ILogger<BroadcastNotificationService> _logger;
        private readonly IAuditLogService? _auditLog;

        public BroadcastNotificationService(
            AppDbContext context,
            IBroadcastPushQueue pushQueue,
            ILogger<BroadcastNotificationService> logger,
            IAuditLogService? auditLog = null)
        {
            _context = context;
            _pushQueue = pushQueue;
            _logger = logger;
            _auditLog = auditLog;
        }

        public async Task<Response<BroadcastNotificationResultDto>> BroadcastAsync(BroadcastNotificationDto dto, Guid adminId)
        {
            // Trimmed before checking: [Required] accepts whitespace, which would push a blank alert.
            var title = dto.Title?.Trim() ?? string.Empty;
            var message = dto.Message?.Trim() ?? string.Empty;

            if (title.Length == 0 || message.Length == 0)
                return BadRequest<BroadcastNotificationResultDto>("Title and message are required.");

            if (title.Length > MaxTitleLength || message.Length > MaxMessageLength)
                return BadRequest<BroadcastNotificationResultDto>(
                    $"Title must be at most {MaxTitleLength} characters and message at most {MaxMessageLength}.");

            // Active only: pending, rejected and banned accounts cannot use the app, and a ban
            // clears the user's token anyway.
            var recipients = await _context.Users
                .AsNoTracking()
                .Where(u => u.Status == UserStatus.Active)
                .Select(u => new { u.Id, u.FcmToken })
                .ToListAsync();

            var broadcastId = Guid.NewGuid();
            var createdAt = DateTime.UtcNow;

            foreach (var batch in recipients.Chunk(InsertBatchSize))
            {
                _context.Notifications.AddRange(batch.Select(r => new Notification
                {
                    UserId = r.Id,
                    Title = title,
                    Message = message,
                    Type = NotificationType.System,
                    ReferenceId = broadcastId,
                    CreatedAt = createdAt
                }));

                await _context.SaveChangesAsync();
                _context.ChangeTracker.Clear();
            }

            var pushTargets = recipients
                .Where(r => !string.IsNullOrWhiteSpace(r.FcmToken))
                .Select(r => new PushRecipient(r.Id, r.FcmToken!))
                .ToList();

            var pushQueued = pushTargets.Count > 0
                && _pushQueue.TryEnqueue(new BroadcastPushJob(broadcastId, title, message, pushTargets));

            if (pushTargets.Count > 0 && !pushQueued)
            {
                _logger.LogWarning(
                    "Broadcast {BroadcastId}: push queue full, {PushTargetCount} push(es) not sent. In-app notifications were saved.",
                    broadcastId, pushTargets.Count);
            }

            if (_auditLog is not null)
            {
                await _auditLog.RecordAsync(new AuditEntry(
                    AuditActions.NotificationBroadcast,
                    adminId,
                    TargetUserId: null,
                    TargetDisplay: "All active users",
                    Details: new { broadcastId, title, recipientCount = recipients.Count, pushTargetCount = pushTargets.Count, pushQueued }));
            }

            var result = new BroadcastNotificationResultDto
            {
                BroadcastId = broadcastId,
                RecipientCount = recipients.Count,
                PushTargetCount = pushTargets.Count,
                PushQueued = pushQueued
            };

            var responseMessage = pushQueued
                ? "Notification saved for all active users; push notifications are being sent."
                : pushTargets.Count == 0
                    ? "Notification saved for all active users; no devices are registered for push."
                    : "Notification saved for all active users, but the push queue is full and no push was sent.";

            return Success(result, message: responseMessage);
        }
    }
}
