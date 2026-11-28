using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Cocorra.BLL.Base;
using Cocorra.BLL.Services.ContentModeration;
using Cocorra.DAL.DTOS.WordDictionaryDto;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;
using Cocorra.Tests.Helpers;
using Xunit;

namespace Cocorra.Tests.ContentModeration;

public class WordFilterTests : IDisposable
{
    private readonly TextRoomTestHost _host = new();

    public void Dispose() => _host.Dispose();

    private async Task<StaffActor> CreateAdminActorAsync()
    {
        var admin = await _host.CreateUserAsync(AppRoles.Admin, "Site", "Admin");
        return TextRoomTestHost.AdminActor(admin);
    }

    private async Task<WordDictionaryEntryDto> AddRuleAsync(
        StaffActor admin,
        string term,
        WordMatchType matchType = WordMatchType.WholeWord,
        WordAction action = WordAction.MaskWord,
        ModerationSeverity severity = ModerationSeverity.Medium,
        WordCategory category = WordCategory.Profanity,
        bool isActive = true)
    {
        var created = await _host.With<IWordDictionaryService, Response<WordDictionaryEntryDto>>(s =>
            s.CreateAsync(new WordDictionaryUpsertDto
            {
                Term = term,
                MatchType = matchType,
                Action = action,
                Severity = severity,
                Category = category,
                IsActive = isActive
            }, admin));

        Assert.True(created.Succeeded, $"Failed to seed dictionary term '{term}': {created.Message}");
        return created.Data!;
    }

    [Theory]
    [InlineData(WordMatchType.WholeWord, "ass", true)]
    [InlineData(WordMatchType.WholeWord, "what an ass!", true)]
    [InlineData(WordMatchType.WholeWord, "class", false)]
    [InlineData(WordMatchType.WholeWord, "assessment", false)]
    [InlineData(WordMatchType.WholeWord, "ass1", false)]
    [InlineData(WordMatchType.WholeWord, "as", false)]
    [InlineData(WordMatchType.Contains, "class", true)]
    [InlineData(WordMatchType.Contains, "assessment", true)]
    [InlineData(WordMatchType.Contains, "ass", true)]
    [InlineData(WordMatchType.Contains, "as", false)]
    public async Task EvaluateAsync_WholeWordVsContains_RespectsBoundariesAndMinimumCharRuns(
        WordMatchType matchType, string input, bool shouldHit)
    {
        var admin = await CreateAdminActorAsync();
        await AddRuleAsync(admin, "ass", matchType, WordAction.BlockMessage);

        var result = await _host.With<IWordFilter, WordFilterResult>(f => f.EvaluateAsync(input));

        Assert.Equal(shouldHit, result.IsBlocked);
        Assert.Equal(shouldHit, result.Hits.Count > 0);
    }

    [Theory]
    [InlineData("fuck", "fuuuuck")]
    [InlineData("fuck", "ALLCAPS FUCK HERE")]
    [InlineData("حلو", "حلووووو")]
    [InlineData("حلو", "حُـلْـ\u200Dـو")]
    [InlineData("احمق", "أَحْـمَـق")]
    [InlineData("bad word", "this is bad   \t word!")]
    public async Task EvaluateAsync_ElongationObfuscationAndCase_MatchesPlainDictionaryTerm(string term, string input)
    {
        var admin = await CreateAdminActorAsync();
        await AddRuleAsync(admin, term, WordMatchType.WholeWord, WordAction.BlockMessage);

        var result = await _host.With<IWordFilter, WordFilterResult>(f => f.EvaluateAsync(input));

        Assert.True(result.IsBlocked);
        Assert.NotEmpty(result.Hits);
    }

    [Fact]
    public async Task EvaluateAsync_Masking_KeepsLengthMasksOriginalSpanWithTashkeelMergesOverlapsAndPreservesWhitespace()
    {
        var admin = await CreateAdminActorAsync();
        await AddRuleAsync(admin, "حلو", WordMatchType.WholeWord, WordAction.MaskWord);
        await AddRuleAsync(admin, "abcd", WordMatchType.Contains, WordAction.MaskWord);
        await AddRuleAsync(admin, "cdef", WordMatchType.Contains, WordAction.MaskWord);
        await AddRuleAsync(admin, "bad word", WordMatchType.WholeWord, WordAction.MaskWord);

        const string arabicWithTashkeel = "ok ح\u064F\u0640ل\u0652و ok";
        var arabicResult = await _host.With<IWordFilter, WordFilterResult>(f => f.EvaluateAsync(arabicWithTashkeel));
        Assert.False(arabicResult.IsBlocked);
        Assert.True(arabicResult.WasMasked);
        Assert.Equal(arabicWithTashkeel.Length, arabicResult.Content.Length);
        Assert.Equal("ok ****** ok", arabicResult.Content);

        const string overlappingInput = "xabcdefy";
        var overlapResult = await _host.With<IWordFilter, WordFilterResult>(f => f.EvaluateAsync(overlappingInput));
        Assert.True(overlapResult.WasMasked);
        Assert.Equal(overlappingInput.Length, overlapResult.Content.Length);
        Assert.Equal("x******y", overlapResult.Content);
        Assert.Equal(2, overlapResult.Hits.Count);

        const string spacedInput = "hi bad   word bye";
        var spacedResult = await _host.With<IWordFilter, WordFilterResult>(f => f.EvaluateAsync(spacedInput));
        Assert.True(spacedResult.WasMasked);
        Assert.Equal(spacedInput.Length, spacedResult.Content.Length);
        Assert.Equal("hi ***   **** bye", spacedResult.Content);
    }

    [Fact]
    public async Task EvaluateAsync_BlockAndMaskInOneMessage_BlocksAndLeavesOriginalContentUnchanged()
    {
        var admin = await CreateAdminActorAsync();
        await AddRuleAsync(admin, "maskme", WordMatchType.WholeWord, WordAction.MaskWord, ModerationSeverity.Low);
        await AddRuleAsync(admin, "blockme", WordMatchType.WholeWord, WordAction.BlockMessage, ModerationSeverity.Critical);

        const string original = "maskme and blockme together";
        var result = await _host.With<IWordFilter, WordFilterResult>(f => f.EvaluateAsync(original));

        Assert.True(result.IsBlocked);
        Assert.False(result.WasMasked);
        Assert.Equal(original, result.Content);
        Assert.Equal(ModerationSeverity.Critical, result.MaxSeverity);
        Assert.Equal(2, result.Hits.Count);
    }

    [Fact]
    public async Task EvaluateAsync_MaskAndMultipleAutoReports_MasksAndPicksHighestSeverityAutoReportHit()
    {
        var admin = await CreateAdminActorAsync();
        await AddRuleAsync(admin, "swear", WordMatchType.WholeWord, WordAction.MaskWord, ModerationSeverity.Critical);
        await AddRuleAsync(admin, "lowauto", WordMatchType.WholeWord, WordAction.AllowAndAutoReport, ModerationSeverity.Low, WordCategory.Spam);
        await AddRuleAsync(admin, "highauto", WordMatchType.WholeWord, WordAction.AllowAndAutoReport, ModerationSeverity.High, WordCategory.Harassment);
        await AddRuleAsync(admin, "medauto", WordMatchType.WholeWord, WordAction.AllowAndAutoReport, ModerationSeverity.Medium, WordCategory.ContactLeak);

        var result = await _host.With<IWordFilter, WordFilterResult>(f =>
            f.EvaluateAsync("swear lowauto highauto medauto"));

        Assert.False(result.IsBlocked);
        Assert.True(result.WasMasked);
        Assert.True(result.RequiresAutoReport);
        Assert.Equal("***** lowauto highauto medauto", result.Content);
        Assert.Equal(ModerationSeverity.Critical, result.MaxSeverity);
        Assert.NotNull(result.TopAutoReportHit);
        Assert.Equal(WordAction.AllowAndAutoReport, result.TopAutoReportHit!.Action);
        Assert.Equal(ModerationSeverity.High, result.TopAutoReportHit.Severity);
        Assert.Equal("highauto", result.TopAutoReportHit.Term);
    }

    [Fact]
    public async Task EvaluateAsync_InactiveEntriesAndBlankInput_AreIgnored()
    {
        var admin = await CreateAdminActorAsync();
        await AddRuleAsync(admin, "sleeping", WordMatchType.WholeWord, WordAction.BlockMessage, isActive: false);

        var inactiveHit = await _host.With<IWordFilter, WordFilterResult>(f => f.EvaluateAsync("sleeping"));
        Assert.False(inactiveHit.IsBlocked);
        Assert.Empty(inactiveHit.Hits);

        var blankResult = await _host.With<IWordFilter, WordFilterResult>(f => f.EvaluateAsync("   "));
        Assert.False(blankResult.IsBlocked);
        Assert.False(blankResult.WasMasked);
        Assert.Equal("   ", blankResult.Content);
        Assert.Empty(blankResult.Hits);
    }

    [Fact]
    public async Task EvaluateAsync_CacheInvalidatesImmediatelyOnDictionaryCreateAndDeactivate()
    {
        var admin = await CreateAdminActorAsync();

        var initial = await _host.With<IWordFilter, WordFilterResult>(f => f.EvaluateAsync("freshbad"));
        Assert.False(initial.IsBlocked);

        var created = await AddRuleAsync(admin, "freshbad", WordMatchType.WholeWord, WordAction.BlockMessage);

        var afterCreate = await _host.With<IWordFilter, WordFilterResult>(f => f.EvaluateAsync("freshbad"));
        Assert.True(afterCreate.IsBlocked);

        var deactivated = await _host.With<IWordDictionaryService, Response<WordDictionaryEntryDto>>(s =>
            s.SetActiveAsync(created.Id, isActive: false, admin));
        Assert.True(deactivated.Succeeded);

        var afterDeactivate = await _host.With<IWordFilter, WordFilterResult>(f => f.EvaluateAsync("freshbad"));
        Assert.False(afterDeactivate.IsBlocked);
        Assert.Empty(afterDeactivate.Hits);
    }

    [Fact]
    public async Task EvaluateAsync_RegexHostileTermsOn4000CharInput_DoesNotThrowAndFinishesUnderOneSecond()
    {
        var admin = await CreateAdminActorAsync();
        await AddRuleAsync(admin, "(bad)[*]\\test+?", WordMatchType.Contains, WordAction.BlockMessage);
        await AddRuleAsync(admin, "[a-z]+^$.", WordMatchType.Contains, WordAction.MaskWord);
        await AddRuleAsync(admin, "(a+)+$", WordMatchType.Contains, WordAction.AllowAndAutoReport);

        var payloadWithMatch = new string('a', 3970) + " (bad)[*]\\test+? " + new string('(', 13);
        Assert.Equal(4000, payloadWithMatch.Length);
        var pureMetaPayload = new string('(', 2000) + new string('\\', 1000) + new string('*', 1000);

        var sw = Stopwatch.StartNew();
        var matchResult = await _host.With<IWordFilter, WordFilterResult>(f => f.EvaluateAsync(payloadWithMatch));
        var metaResult = await _host.With<IWordFilter, WordFilterResult>(f => f.EvaluateAsync(pureMetaPayload));
        sw.Stop();

        Assert.True(matchResult.IsBlocked);
        Assert.False(metaResult.IsBlocked);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), $"Filter evaluation took {sw.Elapsed.TotalMilliseconds:F0} ms");
    }
}
