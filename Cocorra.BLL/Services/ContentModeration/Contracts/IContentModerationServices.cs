using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Cocorra.BLL.Base;
using Cocorra.DAL.DTOS.ReportDto;
using Cocorra.DAL.DTOS.WordDictionaryDto;
using Cocorra.DAL.Models;

namespace Cocorra.BLL.Services.ContentModeration
{
    /// <summary>
    /// Admin-only dictionary CRUD. Every write calls IWordFilter.InvalidateCache and records one
    /// AuditActions.WordDictionaryChanged row (AuditCategories.ContentPolicy). Terms are trimmed;
    /// a term whose normalized form is empty or longer than DictionaryTermMax is BadRequest;
    /// a duplicate (NormalizedTerm, MatchType) is Conflict.
    /// </summary>
    public interface IWordDictionaryService
    {
        /// <summary>Search matches Term or NormalizedTerm. PageSize clamped to [1, 200]. Ordered by Term.</summary>
        Task<PagedResponse<WordDictionaryEntryDto>> GetPagedAsync(WordDictionaryQueryDto query);
        Task<Response<WordDictionaryEntryDto>> CreateAsync(WordDictionaryUpsertDto dto, StaffActor actor);
        Task<Response<WordDictionaryEntryDto>> UpdateAsync(Guid id, WordDictionaryUpsertDto dto, StaffActor actor);
        Task<Response<string>> DeleteAsync(Guid id, StaffActor actor);
        Task<Response<WordDictionaryEntryDto>> SetActiveAsync(Guid id, bool isActive, StaffActor actor);

        /// <summary>
        /// At most DictionaryBulkMax items in total (BadRequest above). Duplicates — against the
        /// table and within the batch — are skipped and counted, invalid terms listed. One audit row.
        /// </summary>
        Task<Response<WordDictionaryBulkResultDto>> BulkAddAsync(WordDictionaryBulkAddDto dto, StaffActor actor);

        /// <summary>Dry run of the filter against the current active dictionary. No audit, no persistence.</summary>
        Task<Response<WordFilterTestResultDto>> TestAsync(string text);
    }

    /// <summary>Severity-routed reports on text-room messages, with an immutable ±N context snapshot.</summary>
    public interface ITextRoomReportService
    {
        /// <summary>
        /// NotFound when the message is missing or deleted. BadRequest when reporting your own
        /// message. Forbidden unless the reporter has a participant row in the message's room.
        /// Conflict when this reporter already reported this message. Creates a Report
        /// (Source User, ReportedUserId = sender, ReportedTextRoomId, ReportedTextRoomMessageId,
        /// severity from <see cref="ReportSeverityPolicy"/>), marks the message IsReported,
        /// captures context, and re-applies escalated severity to every Open report on the same
        /// message.
        /// </summary>
        Task<Response<TextRoomReportResultDto>> ReportMessageAsync(Guid reporterId, long messageId, ReportTextRoomMessageDto dto);

        /// <summary>
        /// Source AutoModeration, ReporterId = ReportedUserId = sender (ReporterId is required),
        /// Category from the hit (Harassment → Harassment, Spam/ContactLeak → Spam, otherwise
        /// InappropriateContent), Severity = hit severity, MatchedTerm/MatchedDictionaryEntryId set.
        /// Marks the message IsReported and captures context. Returns the report id.
        /// </summary>
        Task<Guid> CreateAutoReportAsync(TextRoomMessage message, WordFilterHit hit, CancellationToken ct = default);

        /// <summary>
        /// For every report with ContextCompletedAt == null: append messages after the reported
        /// one (Id order) until ContextAfterRequested is reached, then seal. Also seals when the
        /// room is Closed or the report is older than ReportContextCaptureWindowMinutes. Returns
        /// the number of reports sealed.
        /// </summary>
        Task<int> CompletePendingContextsAsync(CancellationToken ct = default);

        /// <summary>
        /// NotFound when the report is missing, has no text-room message, or (for a Moderator)
        /// involves an Admin as reporter or reported user. The text room's creator is deliberately
        /// not considered: Admins create most text rooms without taking part. For a Moderator, context
        /// lines sent by an Admin come back with SenderId null and SenderName "Staff".
        /// Records AuditActions.ReportContextViewed (AuditCategories.Report, EntityType Report,
        /// EntityId report id, TargetUserId reported user) on every successful call.
        /// </summary>
        Task<Response<ReportMessageContextDto>> GetReportContextAsync(Guid reportId, StaffActor actor);
    }
}
