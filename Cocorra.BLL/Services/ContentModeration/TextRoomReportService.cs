using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cocorra.BLL.Base;
using Cocorra.BLL.Services.AuditLogs;
using Cocorra.BLL.Services.TextRooms;
using Cocorra.DAL.AppMetaData;
using Cocorra.DAL.Data;
using Cocorra.DAL.DTOS.ReportDto;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Cocorra.BLL.Services.ContentModeration
{
    public sealed class TextRoomReportService : ResponseHandler, ITextRoomReportService
    {
        private const int PendingContextBatchSize = 100;
        private const int MaxDescriptionLength = 1000;
        private const int MaxSenderDisplayNameLength = 128;
        private const int MaxMatchedTermLength = 100;

        private readonly AppDbContext _db;
        private readonly ITextRoomSettingsProvider _settings;
        private readonly IAuditLogService _auditLog;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly TimeProvider _time;

        public TextRoomReportService(
            AppDbContext db,
            ITextRoomSettingsProvider settings,
            IAuditLogService auditLog,
            UserManager<ApplicationUser> userManager,
            TimeProvider time)
        {
            _db = db;
            _settings = settings;
            _auditLog = auditLog;
            _userManager = userManager;
            _time = time;
        }

        public async Task<Response<TextRoomReportResultDto>> ReportMessageAsync(
            Guid reporterId,
            long messageId,
            ReportTextRoomMessageDto dto)
        {
            var (trimmedDescription, validationError) = ValidateReportRequest(dto);
            if (validationError is not null)
                return BadRequest<TextRoomReportResultDto>(validationError);

            var message = await _db.TextRoomMessages
                .Include(m => m.TextRoom)
                .Include(m => m.Sender)
                .FirstOrDefaultAsync(m => m.Id == messageId);

            if (message is null || message.IsDeleted || message.TextRoom is null)
                return NotFound<TextRoomReportResultDto>("Message not found.");

            if (message.SenderId == reporterId)
                return BadRequest<TextRoomReportResultDto>("You cannot report your own message.");

            var isParticipant = await _db.TextRoomParticipants
                .AsNoTracking()
                .AnyAsync(p => p.TextRoomId == message.TextRoomId && p.UserId == reporterId);

            if (!isParticipant)
                return Forbidden<TextRoomReportResultDto>("Only room participants can report messages in this room.");

            var alreadyReported = await _db.Reports
                .AsNoTracking()
                .AnyAsync(r => r.ReporterId == reporterId && r.ReportedTextRoomMessageId == messageId);

            if (alreadyReported)
                return Conflict<TextRoomReportResultDto>("You have already reported this message.");

            var now = _time.GetUtcNow().UtcDateTime;
            var inputs = await ReadSeverityInputsAsync(messageId, message.SenderId, now);
            var initialSeverity = ReportSeverityPolicy.Compute(
                dto.Category,
                inputs.DictionarySeverity,
                inputs.DistinctUserReporters + 1,
                inputs.OpenAgainstSenderLast24h + 1);

            var reportId = Guid.NewGuid();
            var context = await CaptureInitialContextAsync(reportId, message, message.TextRoom, CancellationToken.None);

            var report = new Report
            {
                Id = reportId,
                ReporterId = reporterId,
                ReportedUserId = message.SenderId,
                ReportedTextRoomId = message.TextRoomId,
                ReportedTextRoomMessageId = message.Id,
                Category = dto.Category,
                Description = string.IsNullOrEmpty(trimmedDescription) ? "Reported text-room message" : trimmedDescription,
                Status = "Open",
                StatusCode = ReportStatus.Open,
                Source = ReportSource.User,
                Severity = initialSeverity,
                CreatedAt = now,
                ContextBeforeRequested = context.BeforeRequested,
                ContextAfterRequested = context.AfterRequested,
                ContextCompletedAt = context.IsComplete ? now : null
            };

            _db.Reports.Add(report);
            _db.ReportMessageContextEntries.AddRange(context.Entries);
            message.IsReported = true;

            try
            {
                await _db.SaveChangesAsync();
            }
            catch (DbUpdateException ex) when (DbConstraintChecks.IsUniqueViolation(ex))
            {
                return Conflict<TextRoomReportResultDto>("You have already reported this message.");
            }

            var escalatedSeverity = await EscalateOpenReportsOnMessageAsync(dto.Category, messageId, message.SenderId, now);

            return Created(new TextRoomReportResultDto
            {
                ReportId = report.Id,
                Severity = escalatedSeverity,
                SeverityName = escalatedSeverity.ToString()
            });
        }

        public async Task<Guid> CreateAutoReportAsync(
            TextRoomMessage message,
            WordFilterHit hit,
            CancellationToken ct = default)
        {
            var now = _time.GetUtcNow().UtcDateTime;
            var room = message.TextRoom
                ?? await _db.TextRooms.AsNoTracking().FirstOrDefaultAsync(r => r.Id == message.TextRoomId, ct);

            var matchedTerm = hit.Term.Length <= MaxMatchedTermLength
                ? hit.Term
                : hit.Term[..MaxMatchedTermLength];

            var reportId = Guid.NewGuid();
            var context = await CaptureInitialContextAsync(reportId, message, room, ct);

            var report = new Report
            {
                Id = reportId,
                ReporterId = message.SenderId,
                ReportedUserId = message.SenderId,
                ReportedTextRoomId = message.TextRoomId,
                ReportedTextRoomMessageId = message.Id,
                Category = MapWordCategory(hit.Category),
                Description = $"Auto-moderation matched term '{matchedTerm}'",
                Status = "Open",
                StatusCode = ReportStatus.Open,
                Source = ReportSource.AutoModeration,
                Severity = hit.Severity,
                MatchedDictionaryEntryId = hit.EntryId,
                MatchedTerm = matchedTerm,
                CreatedAt = now,
                ContextBeforeRequested = context.BeforeRequested,
                ContextAfterRequested = context.AfterRequested,
                ContextCompletedAt = context.IsComplete ? now : null
            };

            _db.Reports.Add(report);
            _db.ReportMessageContextEntries.AddRange(context.Entries);

            var messageWasDetached = _db.Entry(message).State == EntityState.Detached;
            message.IsReported = true;

            await _db.SaveChangesAsync(ct);

            if (messageWasDetached)
            {
                await _db.TextRoomMessages
                    .Where(m => m.Id == message.Id && !m.IsReported)
                    .ExecuteUpdateAsync(s => s.SetProperty(m => m.IsReported, true), ct);
            }

            return report.Id;
        }

        public async Task<int> CompletePendingContextsAsync(CancellationToken ct = default)
        {
            var now = _time.GetUtcNow().UtcDateTime;

            var pendingReports = await _db.Reports
                .Include(r => r.ReportedTextRoom)
                .Where(r => r.ContextCompletedAt == null
                    && r.ReportedTextRoomId != null
                    && r.ReportedTextRoomMessageId != null)
                .OrderBy(r => r.CreatedAt)
                .Take(PendingContextBatchSize)
                .ToListAsync(ct);

            var sealedCount = 0;

            foreach (var report in pendingReports)
            {
                ct.ThrowIfCancellationRequested();

                if (await CompleteSingleReportContextAsync(report, now, ct))
                    sealedCount++;
            }

            return sealedCount;
        }

        public async Task<Response<ReportMessageContextDto>> GetReportContextAsync(Guid reportId, StaffActor actor)
        {
            var report = await _db.Reports
                .AsNoTracking()
                .Include(r => r.ReportedTextRoom)
                .Include(r => r.Reporter)
                .Include(r => r.ReportedUser)
                .FirstOrDefaultAsync(r => r.Id == reportId);

            if (report is null || report.ReportedTextRoomMessageId is null)
                return NotFound<ReportMessageContextDto>("Report message context not found.");

            if (!actor.IsAdmin && await ReportInvolvesAdminPartyAsync(report))
                return NotFound<ReportMessageContextDto>("Report message context not found.");

            var entries = await _db.ReportMessageContextEntries
                .AsNoTracking()
                .Where(e => e.ReportId == report.Id)
                .OrderBy(e => e.Offset)
                .ToListAsync();

            var adminSenderIds = !actor.IsAdmin && entries.Count > 0
                ? await GetAdminUserIdsAsync(entries.Select(e => e.SenderId).Distinct().ToList())
                : new HashSet<Guid>();

            var messages = entries
                .Select(entry => !actor.IsAdmin && adminSenderIds.Contains(entry.SenderId)
                    ? MapRedactedContextMessage(entry)
                    : MapContextMessage(entry))
                .ToList();

            var isComplete = report.ContextCompletedAt.HasValue;
            var contextDto = new ReportMessageContextDto
            {
                ReportId = report.Id,
                TextRoomId = report.ReportedTextRoomId,
                TextRoomTitle = report.ReportedTextRoom?.Title,
                ReportedMessageId = report.ReportedTextRoomMessageId.Value,
                BeforeRequested = report.ContextBeforeRequested ?? 0,
                AfterRequested = report.ContextAfterRequested ?? 0,
                CapturedBefore = entries.Count(e => e.Offset < 0),
                CapturedAfter = entries.Count(e => e.Offset > 0),
                IsComplete = isComplete,
                Messages = messages
            };

            await RecordContextViewedAuditAsync(actor, report, messages.Count, isComplete);
            return Success(contextDto);
        }

        private async Task<ModerationSeverity> EscalateOpenReportsOnMessageAsync(
            ReportCategory category,
            long messageId,
            Guid senderId,
            DateTime now)
        {
            var inputs = await ReadSeverityInputsAsync(messageId, senderId, now);
            var escalated = ReportSeverityPolicy.Compute(
                category,
                inputs.DictionarySeverity,
                inputs.DistinctUserReporters,
                inputs.OpenAgainstSenderLast24h);

            await _db.Reports
                .Where(r => r.ReportedTextRoomMessageId == messageId
                    && r.StatusCode == ReportStatus.Open
                    && (r.Severity == null || r.Severity < escalated))
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.Severity, escalated));

            return escalated;
        }

        private async Task<bool> CompleteSingleReportContextAsync(Report report, DateTime now, CancellationToken ct)
        {
            var afterRequested = Math.Max(0, report.ContextAfterRequested ?? 0);
            var roomId = report.ReportedTextRoomId!.Value;
            var reportedMessageId = report.ReportedTextRoomMessageId!.Value;
            var sealCutoff = now.AddMinutes(-TextRoomLimits.ReportContextCaptureWindowMinutes);

            var lastAfter = await _db.ReportMessageContextEntries
                .AsNoTracking()
                .Where(e => e.ReportId == report.Id && e.Offset > 0)
                .OrderByDescending(e => e.Offset)
                .Select(e => new { e.Offset, e.TextRoomMessageId })
                .FirstOrDefaultAsync(ct);

            var capturedAfter = lastAfter?.Offset ?? 0;
            var lastCapturedId = lastAfter?.TextRoomMessageId ?? reportedMessageId;
            var remaining = afterRequested - capturedAfter;

            var addedEntries = new List<ReportMessageContextEntry>();
            if (remaining > 0)
            {
                var tailMessages = await _db.TextRoomMessages
                    .AsNoTracking()
                    .Include(m => m.Sender)
                    .Where(m => m.TextRoomId == roomId && m.Id > lastCapturedId)
                    .OrderBy(m => m.Id)
                    .Take(remaining)
                    .ToListAsync(ct);

                foreach (var msg in tailMessages)
                {
                    capturedAfter++;
                    addedEntries.Add(CreateContextEntry(report.Id, msg, capturedAfter, FormatSenderDisplayName(msg.Sender)));
                }
            }

            var roomClosed = report.ReportedTextRoom is null || report.ReportedTextRoom.Status == TextRoomStatus.Closed;
            var shouldSeal = capturedAfter >= afterRequested || roomClosed || report.CreatedAt <= sealCutoff;

            if (addedEntries.Count == 0 && !shouldSeal)
                return false;

            if (addedEntries.Count > 0)
                _db.ReportMessageContextEntries.AddRange(addedEntries);

            if (shouldSeal)
                report.ContextCompletedAt = now;

            try
            {
                await _db.SaveChangesAsync(ct);
                return shouldSeal;
            }
            catch (DbUpdateException ex) when (DbConstraintChecks.IsUniqueViolation(ex))
            {
                foreach (var entry in addedEntries)
                    _db.Entry(entry).State = EntityState.Detached;

                if (shouldSeal)
                    await _db.Entry(report).ReloadAsync(ct);

                return false;
            }
        }

        private async Task<InitialContextSnapshot> CaptureInitialContextAsync(
            Guid reportId,
            TextRoomMessage message,
            TextRoom? room,
            CancellationToken ct)
        {
            var roomSettings = room?.Settings ?? new TextRoomSettings();
            var (before, after) = await _settings.GetReportContextWindowAsync(roomSettings, ct);

            var entries = new List<ReportMessageContextEntry>(before + after + 1);

            if (before > 0)
            {
                var beforeMessages = await _db.TextRoomMessages
                    .AsNoTracking()
                    .Include(m => m.Sender)
                    .Where(m => m.TextRoomId == message.TextRoomId && m.Id < message.Id)
                    .OrderByDescending(m => m.Id)
                    .Take(before)
                    .ToListAsync(ct);

                for (var i = 0; i < beforeMessages.Count; i++)
                    entries.Add(CreateContextEntry(reportId, beforeMessages[i], -(i + 1), FormatSenderDisplayName(beforeMessages[i].Sender)));
            }

            var reportedSenderName = await ResolveSenderDisplayNameAsync(message.SenderId, message.Sender, ct);
            entries.Add(CreateContextEntry(reportId, message, 0, reportedSenderName));

            var capturedAfter = 0;
            if (after > 0)
            {
                var afterMessages = await _db.TextRoomMessages
                    .AsNoTracking()
                    .Include(m => m.Sender)
                    .Where(m => m.TextRoomId == message.TextRoomId && m.Id > message.Id)
                    .OrderBy(m => m.Id)
                    .Take(after)
                    .ToListAsync(ct);

                capturedAfter = afterMessages.Count;
                for (var i = 0; i < afterMessages.Count; i++)
                    entries.Add(CreateContextEntry(reportId, afterMessages[i], i + 1, FormatSenderDisplayName(afterMessages[i].Sender)));
            }

            var isComplete = after == 0
                || capturedAfter >= after
                || room is null
                || room.Status == TextRoomStatus.Closed;

            return new InitialContextSnapshot(entries, before, after, isComplete);
        }

        private async Task<SeverityInputs> ReadSeverityInputsAsync(long messageId, Guid senderId, DateTime now)
        {
            var autoSeverities = await _db.Reports
                .AsNoTracking()
                .Where(r => r.ReportedTextRoomMessageId == messageId
                    && r.Source == ReportSource.AutoModeration
                    && r.Severity != null)
                .Select(r => r.Severity!.Value)
                .ToListAsync();

            ModerationSeverity? dictionarySeverity = autoSeverities.Count == 0
                ? null
                : autoSeverities.Max();

            var distinctUserReporters = await _db.Reports
                .AsNoTracking()
                .Where(r => r.ReportedTextRoomMessageId == messageId && r.Source == ReportSource.User)
                .Select(r => r.ReporterId)
                .Distinct()
                .CountAsync();

            var windowStart = now.AddHours(-24);
            var openAgainstSenderLast24h = await _db.Reports
                .AsNoTracking()
                .CountAsync(r => r.ReportedUserId == senderId
                    && r.StatusCode == ReportStatus.Open
                    && r.CreatedAt >= windowStart);

            return new SeverityInputs(dictionarySeverity, distinctUserReporters, openAgainstSenderLast24h);
        }

        private async Task<bool> ReportInvolvesAdminPartyAsync(Report report) =>
            await IsUserAdminAsync(report.ReporterId, report.Reporter)
            || await IsUserAdminAsync(report.ReportedUserId, report.ReportedUser);

        private async Task<bool> IsUserAdminAsync(Guid? userId, ApplicationUser? loadedUser)
        {
            if (!userId.HasValue)
                return false;

            var user = loadedUser ?? await _userManager.FindByIdAsync(userId.Value.ToString());
            if (user is null)
                return false;

            var roles = await _userManager.GetRolesAsync(user) ?? Array.Empty<string>();
            return roles.Any(r => string.Equals(r, AppRoles.Admin, StringComparison.OrdinalIgnoreCase));
        }

        private async Task<HashSet<Guid>> GetAdminUserIdsAsync(List<Guid> userIds)
        {
            var normalizedAdmin = AppRoles.Admin.ToUpperInvariant();
            var adminIds = await (
                from userRole in _db.UserRoles.AsNoTracking()
                join role in _db.Roles.AsNoTracking() on userRole.RoleId equals role.Id
                where userIds.Contains(userRole.UserId)
                    && (role.Name == AppRoles.Admin || role.NormalizedName == normalizedAdmin)
                select userRole.UserId)
                .Distinct()
                .ToListAsync();

            return adminIds.ToHashSet();
        }

        private async Task<string> ResolveSenderDisplayNameAsync(Guid senderId, ApplicationUser? loadedSender, CancellationToken ct)
        {
            if (loadedSender is not null)
                return FormatSenderDisplayName(loadedSender);

            var sender = await _db.Users
                .AsNoTracking()
                .Where(u => u.Id == senderId)
                .Select(u => new { u.FirstName, u.LastName })
                .FirstOrDefaultAsync(ct);

            return sender is null ? "User" : FormatName(sender.FirstName, sender.LastName);
        }

        private Task RecordContextViewedAuditAsync(StaffActor actor, Report report, int messageCount, bool isComplete) =>
            _auditLog.RecordAsync(new AuditEntry(
                AuditActions.ReportContextViewed,
                actor.UserId,
                report.ReportedUserId,
                report.ReportedUser is not null
                    ? FormatSenderDisplayName(report.ReportedUser)
                    : $"report {report.Id}",
                new
                {
                    messageCount,
                    isComplete
                })
            {
                Category = AuditCategories.Report,
                EntityType = AuditEntityTypes.Report,
                EntityId = report.Id.ToString(),
                ActorRole = actor.Roles
            });

        private static (string? TrimmedDescription, string? Error) ValidateReportRequest(ReportTextRoomMessageDto dto)
        {
            var trimmedDescription = dto.Description?.Trim();
            if (!Enum.IsDefined(dto.Category))
                return (null, "Invalid report category.");

            if (trimmedDescription is not null && trimmedDescription.Length > MaxDescriptionLength)
                return (null, $"Description must not exceed {MaxDescriptionLength} characters.");

            return (trimmedDescription, null);
        }

        private static ReportContextMessageDto MapContextMessage(ReportMessageContextEntry entry) => new()
        {
            MessageId = entry.TextRoomMessageId,
            Offset = entry.Offset,
            SenderId = entry.SenderId,
            SenderName = entry.SenderDisplayName,
            Content = entry.Content,
            SentAt = entry.SentAt,
            IsReportedMessage = entry.Offset == 0,
            WasDeleted = entry.WasDeleted
        };

        private static ReportContextMessageDto MapRedactedContextMessage(ReportMessageContextEntry entry) => new()
        {
            MessageId = entry.TextRoomMessageId,
            Offset = entry.Offset,
            SenderId = null,
            SenderName = "Staff",
            Content = entry.Content,
            SentAt = entry.SentAt,
            IsReportedMessage = entry.Offset == 0,
            WasDeleted = entry.WasDeleted
        };

        private static ReportMessageContextEntry CreateContextEntry(
            Guid reportId,
            TextRoomMessage message,
            int offset,
            string senderDisplayName) => new()
            {
                ReportId = reportId,
                TextRoomMessageId = message.Id,
                Offset = offset,
                SenderId = message.SenderId,
                SenderDisplayName = senderDisplayName,
                Content = message.Content,
                SentAt = message.CreatedAt,
                WasDeleted = message.IsDeleted
            };

        private static string FormatSenderDisplayName(ApplicationUser? user) =>
            user is null ? "User" : FormatName(user.FirstName, user.LastName);

        private static string FormatName(string? firstName, string? lastName)
        {
            var fullName = $"{firstName} {lastName}".Trim();
            if (string.IsNullOrWhiteSpace(fullName))
                return "User";

            return fullName.Length <= MaxSenderDisplayNameLength
                ? fullName
                : fullName[..MaxSenderDisplayNameLength];
        }

        private static ReportCategory MapWordCategory(WordCategory category) => category switch
        {
            WordCategory.Harassment => ReportCategory.Harassment,
            WordCategory.Spam or WordCategory.ContactLeak => ReportCategory.Spam,
            _ => ReportCategory.InappropriateContent
        };

        private sealed record InitialContextSnapshot(
            List<ReportMessageContextEntry> Entries,
            int BeforeRequested,
            int AfterRequested,
            bool IsComplete);

        private sealed record SeverityInputs(
            ModerationSeverity? DictionarySeverity,
            int DistinctUserReporters,
            int OpenAgainstSenderLast24h);
    }
}
