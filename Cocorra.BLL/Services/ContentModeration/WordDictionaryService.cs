using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Cocorra.BLL.Base;
using Cocorra.BLL.Services.AuditLogs;
using Cocorra.DAL.AppMetaData;
using Cocorra.DAL.Data;
using Cocorra.DAL.DTOS.WordDictionaryDto;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;
using Microsoft.EntityFrameworkCore;

namespace Cocorra.BLL.Services.ContentModeration
{
    public sealed class WordDictionaryService : ResponseHandler, IWordDictionaryService
    {
        private const int MaxNotesLength = 200;
        private const int MaxPageSize = 200;

        private readonly AppDbContext _db;
        private readonly IWordFilter _wordFilter;
        private readonly IAuditLogService _auditLog;
        private readonly TimeProvider _time;

        public WordDictionaryService(
            AppDbContext db,
            IWordFilter wordFilter,
            IAuditLogService auditLog,
            TimeProvider time)
        {
            _db = db;
            _wordFilter = wordFilter;
            _auditLog = auditLog;
            _time = time;
        }

        public async Task<PagedResponse<WordDictionaryEntryDto>> GetPagedAsync(WordDictionaryQueryDto query)
        {
            var pageNumber = Math.Max(1, query.PageNumber);
            var pageSize = Math.Clamp(query.PageSize, 1, MaxPageSize);

            var filtered = ApplyFilters(_db.WordDictionaryEntries.AsNoTracking(), query);
            var totalCount = await filtered.CountAsync();
            var entries = await filtered
                .OrderBy(e => e.Term)
                .ThenBy(e => e.Id)
                .Skip((pageNumber - 1) * pageSize)
                .Take(pageSize)
                .Select(e => new WordDictionaryEntryDto
                {
                    Id = e.Id,
                    Term = e.Term,
                    NormalizedTerm = e.NormalizedTerm,
                    MatchType = e.MatchType,
                    Category = e.Category,
                    Severity = e.Severity,
                    Action = e.Action,
                    IsActive = e.IsActive,
                    Notes = e.Notes,
                    CreatedAt = e.CreatedAt,
                    UpdatedAt = e.UpdatedAt
                })
                .ToListAsync();

            return Paginated(entries, totalCount, pageNumber, pageSize);
        }

        public async Task<Response<WordDictionaryEntryDto>> CreateAsync(WordDictionaryUpsertDto dto, StaffActor actor)
        {
            var (validated, validationError) = ValidateUpsert(dto);
            if (validated is null)
                return BadRequest<WordDictionaryEntryDto>(validationError!);

            var duplicate = await _db.WordDictionaryEntries
                .AsNoTracking()
                .AnyAsync(e => e.NormalizedTerm == validated.NormalizedTerm && e.MatchType == validated.MatchType);

            if (duplicate)
                return Conflict<WordDictionaryEntryDto>("A dictionary entry with this term and match type already exists.");

            var entry = CreateEntity(validated, actor.UserId, _time.GetUtcNow().UtcDateTime);
            _db.WordDictionaryEntries.Add(entry);

            try
            {
                await _db.SaveChangesAsync();
            }
            catch (DbUpdateException ex) when (DbConstraintChecks.IsUniqueViolation(ex))
            {
                return Conflict<WordDictionaryEntryDto>("A dictionary entry with this term and match type already exists.");
            }

            _wordFilter.InvalidateCache();
            await RecordEntryAuditAsync(actor, entry, fromState: null, toState: "Created");

            return Created(ToDto(entry));
        }

        public async Task<Response<WordDictionaryEntryDto>> UpdateAsync(Guid id, WordDictionaryUpsertDto dto, StaffActor actor)
        {
            var (validated, validationError) = ValidateUpsert(dto);
            if (validated is null)
                return BadRequest<WordDictionaryEntryDto>(validationError!);

            var entry = await _db.WordDictionaryEntries.FirstOrDefaultAsync(e => e.Id == id);
            if (entry is null)
                return NotFound<WordDictionaryEntryDto>("Dictionary entry not found.");

            var duplicate = await _db.WordDictionaryEntries
                .AsNoTracking()
                .AnyAsync(e => e.Id != id && e.NormalizedTerm == validated.NormalizedTerm && e.MatchType == validated.MatchType);

            if (duplicate)
                return Conflict<WordDictionaryEntryDto>("A dictionary entry with this term and match type already exists.");

            ApplyChanges(entry, validated, actor.UserId, _time.GetUtcNow().UtcDateTime);

            try
            {
                await _db.SaveChangesAsync();
            }
            catch (DbUpdateException ex) when (DbConstraintChecks.IsUniqueViolation(ex))
            {
                return Conflict<WordDictionaryEntryDto>("A dictionary entry with this term and match type already exists.");
            }

            _wordFilter.InvalidateCache();
            await RecordEntryAuditAsync(actor, entry, fromState: null, toState: "Updated");

            return Success(ToDto(entry));
        }

        public async Task<Response<string>> DeleteAsync(Guid id, StaffActor actor)
        {
            var entry = await _db.WordDictionaryEntries.FirstOrDefaultAsync(e => e.Id == id);
            if (entry is null)
                return NotFound<string>("Dictionary entry not found.");

            _db.WordDictionaryEntries.Remove(entry);
            await _db.SaveChangesAsync();

            _wordFilter.InvalidateCache();
            await RecordEntryAuditAsync(actor, entry, fromState: null, toState: "Deleted");

            return Success("Dictionary entry deleted.");
        }

        public async Task<Response<WordDictionaryEntryDto>> SetActiveAsync(Guid id, bool isActive, StaffActor actor)
        {
            var entry = await _db.WordDictionaryEntries.FirstOrDefaultAsync(e => e.Id == id);
            if (entry is null)
                return NotFound<WordDictionaryEntryDto>("Dictionary entry not found.");

            var fromState = entry.IsActive ? "Activated" : "Deactivated";
            entry.IsActive = isActive;
            entry.UpdatedById = actor.UserId;
            entry.UpdatedAt = _time.GetUtcNow().UtcDateTime;

            await _db.SaveChangesAsync();

            _wordFilter.InvalidateCache();
            await RecordEntryAuditAsync(actor, entry, fromState, toState: isActive ? "Activated" : "Deactivated");

            return Success(ToDto(entry));
        }

        public async Task<Response<WordDictionaryBulkResultDto>> BulkAddAsync(WordDictionaryBulkAddDto dto, StaffActor actor)
        {
            var entries = dto.Entries ?? new List<WordDictionaryUpsertDto>();
            var terms = dto.Terms ?? new List<string>();
            var totalItems = entries.Count + terms.Count;

            if (totalItems == 0)
                return BadRequest<WordDictionaryBulkResultDto>("At least one term or entry is required.");

            if (totalItems > TextRoomLimits.DictionaryBulkMax)
                return BadRequest<WordDictionaryBulkResultDto>($"At most {TextRoomLimits.DictionaryBulkMax} items can be added at once.");

            if (terms.Count > 0 && !AreEnumsValid(dto.MatchType, dto.Category, dto.Severity, dto.Action))
                return BadRequest<WordDictionaryBulkResultDto>("Invalid match type, category, severity, or action.");

            var bulkResult = new WordDictionaryBulkResultDto();
            var validCandidates = ValidateBulkCandidates(ExpandBulkCandidates(dto, entries, terms), bulkResult.Invalid);
            var (toInsert, skippedDuplicates) = await DeduplicateCandidatesAsync(validCandidates, actor.UserId);
            bulkResult.SkippedDuplicates = skippedDuplicates;

            if (toInsert.Count > 0)
            {
                _db.WordDictionaryEntries.AddRange(toInsert);
                try
                {
                    await _db.SaveChangesAsync();
                }
                catch (DbUpdateException ex) when (DbConstraintChecks.IsUniqueViolation(ex))
                {
                    return Conflict<WordDictionaryBulkResultDto>("One or more terms conflicted with concurrent dictionary changes.");
                }

                bulkResult.Added = toInsert.Count;
            }

            _wordFilter.InvalidateCache();
            await RecordBulkAuditAsync(actor, bulkResult);

            return Success(bulkResult);
        }

        public async Task<Response<WordFilterTestResultDto>> TestAsync(string text)
        {
            var input = text ?? string.Empty;
            var normalized = TextNormalizer.Normalize(input).Value;
            var evaluation = await _wordFilter.EvaluateAsync(input);

            return Success(new WordFilterTestResultDto
            {
                NormalizedText = normalized,
                ResultContent = evaluation.Content,
                IsBlocked = evaluation.IsBlocked,
                WasMasked = evaluation.WasMasked,
                RequiresAutoReport = evaluation.RequiresAutoReport,
                MaxSeverity = evaluation.MaxSeverity,
                Hits = evaluation.Hits.Select(h => new WordFilterHitDto
                {
                    EntryId = h.EntryId,
                    Term = h.Term,
                    Category = h.Category,
                    Severity = h.Severity,
                    Action = h.Action
                }).ToList()
            });
        }

        private static IQueryable<WordDictionaryEntry> ApplyFilters(
            IQueryable<WordDictionaryEntry> entries,
            WordDictionaryQueryDto query)
        {
            if (!string.IsNullOrWhiteSpace(query.Search))
            {
                var rawSearch = query.Search.Trim().ToLowerInvariant();
                var normalizedSearch = TextNormalizer.NormalizeTerm(query.Search);

                entries = string.IsNullOrEmpty(normalizedSearch)
                    ? entries.Where(e => e.Term.ToLower().Contains(rawSearch))
                    : entries.Where(e => e.Term.ToLower().Contains(rawSearch)
                        || e.NormalizedTerm.Contains(normalizedSearch));
            }

            if (query.Category.HasValue)
                entries = entries.Where(e => e.Category == query.Category.Value);

            if (query.Severity.HasValue)
                entries = entries.Where(e => e.Severity == query.Severity.Value);

            if (query.Action.HasValue)
                entries = entries.Where(e => e.Action == query.Action.Value);

            if (query.IsActive.HasValue)
                entries = entries.Where(e => e.IsActive == query.IsActive.Value);

            return entries;
        }

        private async Task<(List<WordDictionaryEntry> ToInsert, int SkippedDuplicates)> DeduplicateCandidatesAsync(
            List<ValidatedEntry> validCandidates,
            Guid actorUserId)
        {
            if (validCandidates.Count == 0)
                return (new List<WordDictionaryEntry>(), 0);

            var normalizedTerms = validCandidates.Select(c => c.NormalizedTerm).Distinct().ToList();
            var existing = await _db.WordDictionaryEntries
                .AsNoTracking()
                .Where(e => normalizedTerms.Contains(e.NormalizedTerm))
                .Select(e => new { e.NormalizedTerm, e.MatchType })
                .ToListAsync();

            var seen = existing
                .Select(x => (x.NormalizedTerm, x.MatchType))
                .ToHashSet();

            var now = _time.GetUtcNow().UtcDateTime;
            var toInsert = new List<WordDictionaryEntry>(validCandidates.Count);
            var skippedDuplicates = 0;

            foreach (var candidate in validCandidates)
            {
                if (!seen.Add((candidate.NormalizedTerm, candidate.MatchType)))
                {
                    skippedDuplicates++;
                    continue;
                }

                toInsert.Add(CreateEntity(candidate, actorUserId, now));
            }

            return (toInsert, skippedDuplicates);
        }

        private static List<ValidatedEntry> ValidateBulkCandidates(
            List<WordDictionaryUpsertDto?> candidates,
            List<string> invalidTerms)
        {
            var validCandidates = new List<ValidatedEntry>(candidates.Count);
            foreach (var candidate in candidates)
            {
                if (candidate is null)
                {
                    invalidTerms.Add(string.Empty);
                    continue;
                }

                var (validated, _) = ValidateUpsert(candidate);
                if (validated is null)
                    invalidTerms.Add(candidate.Term ?? string.Empty);
                else
                    validCandidates.Add(validated);
            }

            return validCandidates;
        }

        private static List<WordDictionaryUpsertDto?> ExpandBulkCandidates(
            WordDictionaryBulkAddDto dto,
            List<WordDictionaryUpsertDto> entries,
            List<string> terms)
        {
            var candidates = new List<WordDictionaryUpsertDto?>(entries.Count + terms.Count);
            candidates.AddRange(entries);

            foreach (var term in terms)
            {
                candidates.Add(new WordDictionaryUpsertDto
                {
                    Term = term ?? string.Empty,
                    MatchType = dto.MatchType,
                    Category = dto.Category,
                    Severity = dto.Severity,
                    Action = dto.Action,
                    IsActive = true
                });
            }

            return candidates;
        }

        private static (ValidatedEntry? Entry, string? Error) ValidateUpsert(WordDictionaryUpsertDto dto)
        {
            var trimmedTerm = (dto.Term ?? string.Empty).Trim();
            var normalizedTerm = TextNormalizer.NormalizeTerm(trimmedTerm);
            var trimmedNotes = string.IsNullOrWhiteSpace(dto.Notes) ? null : dto.Notes.Trim();

            if (string.IsNullOrEmpty(normalizedTerm))
                return (null, "Term must contain at least one valid character.");

            if (trimmedTerm.Length > TextRoomLimits.DictionaryTermMax || normalizedTerm.Length > TextRoomLimits.DictionaryTermMax)
                return (null, $"Term must not exceed {TextRoomLimits.DictionaryTermMax} characters.");

            if (!AreEnumsValid(dto.MatchType, dto.Category, dto.Severity, dto.Action))
                return (null, "Invalid match type, category, severity, or action.");

            if (trimmedNotes is not null && trimmedNotes.Length > MaxNotesLength)
                return (null, $"Notes must not exceed {MaxNotesLength} characters.");

            return (new ValidatedEntry(
                trimmedTerm,
                normalizedTerm,
                dto.MatchType,
                dto.Category,
                dto.Severity,
                dto.Action,
                dto.IsActive,
                trimmedNotes), null);
        }

        private static WordDictionaryEntry CreateEntity(ValidatedEntry validated, Guid actorUserId, DateTime createdAt) => new()
        {
            Id = Guid.NewGuid(),
            Term = validated.Term,
            NormalizedTerm = validated.NormalizedTerm,
            MatchType = validated.MatchType,
            Category = validated.Category,
            Severity = validated.Severity,
            Action = validated.Action,
            IsActive = validated.IsActive,
            Notes = validated.Notes,
            CreatedById = actorUserId,
            CreatedAt = createdAt
        };

        private static void ApplyChanges(
            WordDictionaryEntry entry,
            ValidatedEntry validated,
            Guid actorUserId,
            DateTime updatedAt)
        {
            entry.Term = validated.Term;
            entry.NormalizedTerm = validated.NormalizedTerm;
            entry.MatchType = validated.MatchType;
            entry.Category = validated.Category;
            entry.Severity = validated.Severity;
            entry.Action = validated.Action;
            entry.IsActive = validated.IsActive;
            entry.Notes = validated.Notes;
            entry.UpdatedById = actorUserId;
            entry.UpdatedAt = updatedAt;
        }

        private static bool AreEnumsValid(
            WordMatchType matchType,
            WordCategory category,
            ModerationSeverity severity,
            WordAction action) =>
            Enum.IsDefined(matchType)
            && Enum.IsDefined(category)
            && Enum.IsDefined(severity)
            && Enum.IsDefined(action);

        private Task RecordEntryAuditAsync(StaffActor actor, WordDictionaryEntry entry, string? fromState, string toState) =>
            _auditLog.RecordAsync(new AuditEntry(
                AuditActions.WordDictionaryChanged,
                actor.UserId,
                null,
                entry.Term,
                new
                {
                    term = entry.Term,
                    normalizedTerm = entry.NormalizedTerm,
                    matchType = entry.MatchType.ToString(),
                    category = entry.Category.ToString(),
                    severity = entry.Severity.ToString(),
                    action = entry.Action.ToString(),
                    isActive = entry.IsActive
                })
            {
                Category = AuditCategories.ContentPolicy,
                EntityType = AuditEntityTypes.WordDictionaryEntry,
                EntityId = entry.Id.ToString(),
                FromState = fromState,
                ToState = toState,
                ActorRole = actor.Roles
            });

        private Task RecordBulkAuditAsync(StaffActor actor, WordDictionaryBulkResultDto bulkResult) =>
            _auditLog.RecordAsync(new AuditEntry(
                AuditActions.WordDictionaryChanged,
                actor.UserId,
                null,
                null,
                new
                {
                    added = bulkResult.Added,
                    skippedDuplicates = bulkResult.SkippedDuplicates,
                    invalidCount = bulkResult.Invalid.Count
                })
            {
                Category = AuditCategories.ContentPolicy,
                EntityType = AuditEntityTypes.WordDictionaryEntry,
                EntityId = null,
                ToState = "BulkAdded",
                ActorRole = actor.Roles
            });

        private static WordDictionaryEntryDto ToDto(WordDictionaryEntry entry) => new()
        {
            Id = entry.Id,
            Term = entry.Term,
            NormalizedTerm = entry.NormalizedTerm,
            MatchType = entry.MatchType,
            Category = entry.Category,
            Severity = entry.Severity,
            Action = entry.Action,
            IsActive = entry.IsActive,
            Notes = entry.Notes,
            CreatedAt = entry.CreatedAt,
            UpdatedAt = entry.UpdatedAt
        };

        private sealed record ValidatedEntry(
            string Term,
            string NormalizedTerm,
            WordMatchType MatchType,
            WordCategory Category,
            ModerationSeverity Severity,
            WordAction Action,
            bool IsActive,
            string? Notes);
    }
}
