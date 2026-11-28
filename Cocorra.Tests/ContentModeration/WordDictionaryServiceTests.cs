using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Cocorra.BLL.Base;
using Cocorra.BLL.Services.ContentModeration;
using Cocorra.DAL.AppMetaData;
using Cocorra.DAL.DTOS.WordDictionaryDto;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;
using Cocorra.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Cocorra.Tests.ContentModeration;

public class WordDictionaryServiceTests : IDisposable
{
    private readonly TextRoomTestHost _host = new();

    public void Dispose() => _host.Dispose();

    private async Task<StaffActor> CreateAdminActorAsync()
    {
        var admin = await _host.CreateUserAsync(AppRoles.Admin, "Dict", "Admin");
        return TextRoomTestHost.AdminActor(admin);
    }

    [Fact]
    public async Task Mutations_EachRecordExactlyOneWordDictionaryChangedAuditRowWithExpectedState()
    {
        var admin = await CreateAdminActorAsync();

        var created = await _host.With<IWordDictionaryService, Response<WordDictionaryEntryDto>>(s =>
            s.CreateAsync(new WordDictionaryUpsertDto
            {
                Term = "spamterm",
                MatchType = WordMatchType.WholeWord,
                Category = WordCategory.Spam,
                Severity = ModerationSeverity.Low,
                Action = WordAction.MaskWord
            }, admin));
        Assert.True(created.Succeeded);
        var entryId = created.Data!.Id;

        var updated = await _host.With<IWordDictionaryService, Response<WordDictionaryEntryDto>>(s =>
            s.UpdateAsync(entryId, new WordDictionaryUpsertDto
            {
                Term = "spamterm",
                MatchType = WordMatchType.Contains,
                Category = WordCategory.Harassment,
                Severity = ModerationSeverity.High,
                Action = WordAction.BlockMessage,
                IsActive = true
            }, admin));
        Assert.True(updated.Succeeded);

        var deactivated = await _host.With<IWordDictionaryService, Response<WordDictionaryEntryDto>>(s =>
            s.SetActiveAsync(entryId, isActive: false, admin));
        Assert.True(deactivated.Succeeded);

        var activated = await _host.With<IWordDictionaryService, Response<WordDictionaryEntryDto>>(s =>
            s.SetActiveAsync(entryId, isActive: true, admin));
        Assert.True(activated.Succeeded);

        var deleted = await _host.With<IWordDictionaryService, Response<string>>(s =>
            s.DeleteAsync(entryId, admin));
        Assert.True(deleted.Succeeded);

        var bulk = await _host.With<IWordDictionaryService, Response<WordDictionaryBulkResultDto>>(s =>
            s.BulkAddAsync(new WordDictionaryBulkAddDto
            {
                Terms = new List<string> { "bulkone", "bulktwo" },
                MatchType = WordMatchType.WholeWord,
                Category = WordCategory.Custom,
                Severity = ModerationSeverity.Medium,
                Action = WordAction.MaskWord
            }, admin));
        Assert.True(bulk.Succeeded);

        var audits = _host.Audit.ByAction(AuditActions.WordDictionaryChanged);
        Assert.Equal(6, audits.Count);
        Assert.Equal(6, _host.Audit.Entries.Count);
        Assert.All(audits, a =>
        {
            Assert.Equal(AuditCategories.ContentPolicy, a.Category);
            Assert.Equal(AuditEntityTypes.WordDictionaryEntry, a.EntityType);
            Assert.Equal(admin.UserId, a.ActorUserId);
        });

        Assert.Equal(
            new[] { "Created", "Updated", "Deactivated", "Activated", "Deleted", "BulkAdded" },
            audits.Select(a => a.ToState).ToArray());

        for (var i = 0; i < 5; i++)
            Assert.Equal(entryId.ToString(), audits[i].EntityId);
        Assert.Null(audits[5].EntityId);
    }

    [Fact]
    public async Task FailedCallsAndDryRunTest_WriteZeroAuditRows()
    {
        var admin = await CreateAdminActorAsync();
        var missingId = Guid.NewGuid();

        var badCreate = await _host.With<IWordDictionaryService, Response<WordDictionaryEntryDto>>(s =>
            s.CreateAsync(new WordDictionaryUpsertDto { Term = "   " }, admin));
        Assert.Equal(HttpStatusCode.BadRequest, badCreate.StatusCode);

        var missingUpdate = await _host.With<IWordDictionaryService, Response<WordDictionaryEntryDto>>(s =>
            s.UpdateAsync(missingId, new WordDictionaryUpsertDto { Term = "valid" }, admin));
        Assert.Equal(HttpStatusCode.NotFound, missingUpdate.StatusCode);

        var missingDelete = await _host.With<IWordDictionaryService, Response<string>>(s =>
            s.DeleteAsync(missingId, admin));
        Assert.Equal(HttpStatusCode.NotFound, missingDelete.StatusCode);

        var missingToggle = await _host.With<IWordDictionaryService, Response<WordDictionaryEntryDto>>(s =>
            s.SetActiveAsync(missingId, false, admin));
        Assert.Equal(HttpStatusCode.NotFound, missingToggle.StatusCode);

        var dryRun = await _host.With<IWordDictionaryService, Response<WordFilterTestResultDto>>(s =>
            s.TestAsync("مَرْحَبًا Hello"));
        Assert.True(dryRun.Succeeded);
        Assert.Equal("مرحبا hello", dryRun.Data!.NormalizedText);

        Assert.Empty(_host.Audit.Entries);
    }

    [Fact]
    public async Task CreateAndUpdate_DuplicateNormalizedTermSameMatchType_ReturnsConflict_DifferentMatchTypeSucceeds()
    {
        var admin = await CreateAdminActorAsync();

        var first = await _host.With<IWordDictionaryService, Response<WordDictionaryEntryDto>>(s =>
            s.CreateAsync(new WordDictionaryUpsertDto
            {
                Term = "أحمق",
                MatchType = WordMatchType.WholeWord
            }, admin));
        Assert.True(first.Succeeded);

        var auditCountBeforeConflicts = _host.Audit.Entries.Count;

        var normalizedDup = await _host.With<IWordDictionaryService, Response<WordDictionaryEntryDto>>(s =>
            s.CreateAsync(new WordDictionaryUpsertDto
            {
                Term = "احمق",
                MatchType = WordMatchType.WholeWord
            }, admin));
        Assert.False(normalizedDup.Succeeded);
        Assert.Equal(HttpStatusCode.Conflict, normalizedDup.StatusCode);

        var diacriticsDup = await _host.With<IWordDictionaryService, Response<WordDictionaryEntryDto>>(s =>
            s.CreateAsync(new WordDictionaryUpsertDto
            {
                Term = "أَحْـمَـق",
                MatchType = WordMatchType.WholeWord
            }, admin));
        Assert.False(diacriticsDup.Succeeded);
        Assert.Equal(HttpStatusCode.Conflict, diacriticsDup.StatusCode);
        Assert.Equal(auditCountBeforeConflicts, _host.Audit.Entries.Count);

        var differentMatchType = await _host.With<IWordDictionaryService, Response<WordDictionaryEntryDto>>(s =>
            s.CreateAsync(new WordDictionaryUpsertDto
            {
                Term = "احمق",
                MatchType = WordMatchType.Contains
            }, admin));
        Assert.True(differentMatchType.Succeeded);

        var updateCollision = await _host.With<IWordDictionaryService, Response<WordDictionaryEntryDto>>(s =>
            s.UpdateAsync(differentMatchType.Data!.Id, new WordDictionaryUpsertDto
            {
                Term = "احمق",
                MatchType = WordMatchType.WholeWord
            }, admin));
        Assert.False(updateCollision.Succeeded);
        Assert.Equal(HttpStatusCode.Conflict, updateCollision.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("    ")]
    [InlineData("\u064E\u064B\u0640\u200B")]
    [InlineData("OVER_100")]
    public async Task CreateAsync_InvalidTerm_ReturnsBadRequestWithNoAudit(string rawTerm)
    {
        var term = rawTerm == "OVER_100" ? new string('a', TextRoomLimits.DictionaryTermMax + 1) : rawTerm;
        var admin = await CreateAdminActorAsync();

        var result = await _host.With<IWordDictionaryService, Response<WordDictionaryEntryDto>>(s =>
            s.CreateAsync(new WordDictionaryUpsertDto { Term = term }, admin));

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        Assert.Empty(_host.Audit.Entries);
    }

    [Fact]
    public async Task CreateAsync_ExactMaxLength100Chars_Succeeds()
    {
        var admin = await CreateAdminActorAsync();
        var term = new string('a', TextRoomLimits.DictionaryTermMax);

        var result = await _host.With<IWordDictionaryService, Response<WordDictionaryEntryDto>>(s =>
            s.CreateAsync(new WordDictionaryUpsertDto { Term = term }, admin));

        Assert.True(result.Succeeded);
        Assert.Equal(term, result.Data!.Term);
    }

    [Fact]
    public async Task BulkAddAsync_501Items_ReturnsBadRequestWithoutPersistingOrAuditing()
    {
        var admin = await CreateAdminActorAsync();
        var terms = Enumerable.Range(1, TextRoomLimits.DictionaryBulkMax + 1)
            .Select(i => $"word{i}")
            .ToList();

        var result = await _host.With<IWordDictionaryService, Response<WordDictionaryBulkResultDto>>(s =>
            s.BulkAddAsync(new WordDictionaryBulkAddDto { Terms = terms }, admin));

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        Assert.Empty(_host.Audit.Entries);
        var count = await _host.WithDb(db => db.WordDictionaryEntries.CountAsync());
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task BulkAddAsync_CountsTableAndBatchDuplicatesListsInvalidAndPersistsExactAdded()
    {
        var admin = await CreateAdminActorAsync();
        await _host.With<IWordDictionaryService, Response<WordDictionaryEntryDto>>(s =>
            s.CreateAsync(new WordDictionaryUpsertDto
            {
                Term = "أحمق",
                MatchType = WordMatchType.WholeWord
            }, admin));

        var auditBeforeBulk = _host.Audit.Entries.Count;
        var tooLong = new string('z', TextRoomLimits.DictionaryTermMax + 1);

        var bulkResult = await _host.With<IWordDictionaryService, Response<WordDictionaryBulkResultDto>>(s =>
            s.BulkAddAsync(new WordDictionaryBulkAddDto
            {
                MatchType = WordMatchType.WholeWord,
                Category = WordCategory.Profanity,
                Severity = ModerationSeverity.High,
                Action = WordAction.BlockMessage,
                Terms = new List<string>
                {
                    "احمق",
                    "alpha",
                    "ALPHA",
                    "beta",
                    "   ",
                    "\u064E\u0650",
                    tooLong,
                    "gamma"
                }
            }, admin));

        Assert.True(bulkResult.Succeeded);
        Assert.Equal(3, bulkResult.Data!.Added);
        Assert.Equal(2, bulkResult.Data.SkippedDuplicates);
        Assert.Equal(3, bulkResult.Data.Invalid.Count);

        var persistedCount = await _host.WithDb(db => db.WordDictionaryEntries.CountAsync());
        Assert.Equal(4, persistedCount);
        Assert.Equal(auditBeforeBulk + 1, _host.Audit.Entries.Count);
        Assert.Equal("BulkAdded", _host.Audit.Entries.Last().ToState);
    }

    [Fact]
    public async Task GetPagedAsync_ClampsPageSizeTo1Through200AndCombinesFiltersWithAnd()
    {
        var admin = await CreateAdminActorAsync();
        var entries = new[]
        {
            new WordDictionaryUpsertDto { Term = "أحمق", Category = WordCategory.Harassment, Severity = ModerationSeverity.High, Action = WordAction.BlockMessage, IsActive = true },
            new WordDictionaryUpsertDto { Term = "beta", Category = WordCategory.Harassment, Severity = ModerationSeverity.High, Action = WordAction.MaskWord, IsActive = true },
            new WordDictionaryUpsertDto { Term = "gamma", Category = WordCategory.Spam, Severity = ModerationSeverity.High, Action = WordAction.BlockMessage, IsActive = true },
            new WordDictionaryUpsertDto { Term = "delta", Category = WordCategory.Harassment, Severity = ModerationSeverity.Low, Action = WordAction.BlockMessage, IsActive = true },
            new WordDictionaryUpsertDto { Term = "epsilon", Category = WordCategory.Harassment, Severity = ModerationSeverity.High, Action = WordAction.BlockMessage, IsActive = false }
        };

        foreach (var dto in entries)
        {
            var created = await _host.With<IWordDictionaryService, Response<WordDictionaryEntryDto>>(s => s.CreateAsync(dto, admin));
            Assert.True(created.Succeeded);
        }

        var clampedLow = await _host.With<IWordDictionaryService, PagedResponse<WordDictionaryEntryDto>>(s =>
            s.GetPagedAsync(new WordDictionaryQueryDto { PageNumber = 0, PageSize = 0 }));
        Assert.Equal(1, clampedLow.CurrentPage);
        Assert.Equal(1, clampedLow.PageSize);
        Assert.Equal(5, clampedLow.TotalCount);
        Assert.Single(clampedLow.Data!);

        var clampedHigh = await _host.With<IWordDictionaryService, PagedResponse<WordDictionaryEntryDto>>(s =>
            s.GetPagedAsync(new WordDictionaryQueryDto { PageNumber = 1, PageSize = 1000 }));
        Assert.Equal(200, clampedHigh.PageSize);
        Assert.Equal(5, clampedHigh.Data!.Count());

        var combinedMatch = await _host.With<IWordDictionaryService, PagedResponse<WordDictionaryEntryDto>>(s =>
            s.GetPagedAsync(new WordDictionaryQueryDto
            {
                Category = WordCategory.Harassment,
                Severity = ModerationSeverity.High,
                Action = WordAction.BlockMessage,
                IsActive = true,
                Search = "احمق"
            }));
        var only = Assert.Single(combinedMatch.Data!);
        Assert.Equal("أحمق", only.Term);
        Assert.Equal(1, combinedMatch.TotalCount);

        var combinedMismatch = await _host.With<IWordDictionaryService, PagedResponse<WordDictionaryEntryDto>>(s =>
            s.GetPagedAsync(new WordDictionaryQueryDto
            {
                Category = WordCategory.Harassment,
                Severity = ModerationSeverity.High,
                Action = WordAction.BlockMessage,
                IsActive = false,
                Search = "احمق"
            }));
        Assert.Empty(combinedMismatch.Data!);
        Assert.Equal(0, combinedMismatch.TotalCount);
    }
}
