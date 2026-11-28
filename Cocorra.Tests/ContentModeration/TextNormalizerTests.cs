using System;
using Cocorra.BLL.Services.ContentModeration;
using Xunit;

namespace Cocorra.Tests.ContentModeration;

public class TextNormalizerTests
{
    [Theory]
    [InlineData("مَرْحَبًا", "مرحبا")]
    [InlineData("مُحَمَّدٌ", "محمد")]
    [InlineData("كِتَابُ\u0670", "كتاب")]
    [InlineData("س\u06D6ل\u06DFا\u06EDم", "سلام")]
    [InlineData("مـــرحبا", "مرحبا")]
    [InlineData("ح\u0640\u0640\u0640لو", "حلو")]
    [InlineData("مر\u200Dح\u200Cب\u200Bا", "مرحبا")]
    [InlineData("\u200Fمرحبا\u200E", "مرحبا")]
    [InlineData("\u202Aabc\u202B\u202E\u2066def\u2069\uFEFF", "abcdef")]
    public void Normalize_StripsTashkeelTatweelZeroWidthAndBidiMarks(string input, string expected)
    {
        var result = TextNormalizer.Normalize(input);

        Assert.Equal(expected, result.Value);
        Assert.Equal(expected.Length, result.OriginalIndex.Length);
    }

    [Theory]
    [InlineData("أحمد", "احمد")]
    [InlineData("إسلام", "اسلام")]
    [InlineData("آمال", "امال")]
    [InlineData("ٱلله", "الله")]
    [InlineData("مدرسة", "مدرسه")]
    [InlineData("على", "علي")]
    [InlineData("کتاب", "كتاب")]
    [InlineData("فارسی", "فارسي")]
    [InlineData("HeLLo WORLD", "hello world")]
    [InlineData("TITLE", "title")]
    [InlineData("أهلاً Hello 123 🔥", "اهلا hello 123 🔥")]
    [InlineData("fuuuuck حلوووو", "fuuuuck حلوووو")]
    public void Normalize_MapsArabicPersianCaseFoldsAndPreservesMixedContent(string input, string expected)
    {
        var result = TextNormalizer.Normalize(input);

        Assert.Equal(expected, result.Value);
        Assert.Equal(expected.Length, result.OriginalIndex.Length);
    }

    public static TheoryData<string, string, int[]> OriginalIndexCases => new()
    {
        // Tashkeel + tatweel between Arabic letters.
        { "م\u064E\u0640\u0640ر\u0652ح\u064Eب\u064Bا", "مرحبا", new[] { 0, 4, 6, 8, 10 } },
        // Alef variant + ZWJ + RLM + tatweel + uppercase Latin.
        { "أ\u200D\u200Fح\u0640B", "احb", new[] { 0, 3, 5 } },
        // Digits + ZWSP + UTF-16 surrogate pair emoji.
        { "A1\u200B🔥", "a1🔥", new[] { 0, 1, 3, 4 } }
    };

    [Theory]
    [MemberData(nameof(OriginalIndexCases))]
    public void Normalize_DroppedAndMappedCharacters_MapsEveryOutputCharToExactOriginalIndex(
        string input, string expectedValue, int[] expectedOriginalIndex)
    {
        var result = TextNormalizer.Normalize(input);

        Assert.Equal(expectedValue, result.Value);
        Assert.Equal(expectedOriginalIndex, result.OriginalIndex);
    }

    [Fact]
    public void Normalize_EmptyString_ReturnsEmptyValueAndEmptyIndexArray()
    {
        var result = TextNormalizer.Normalize(string.Empty);

        Assert.Equal(string.Empty, result.Value);
        Assert.Empty(result.OriginalIndex);
    }

    [Fact]
    public void NormalizeAndNormalizeTerm_NullInput_ReturnEmptySafely()
    {
        var normalized = TextNormalizer.Normalize(null!);
        Assert.Equal(string.Empty, normalized.Value);
        Assert.Empty(normalized.OriginalIndex);

        var term = TextNormalizer.NormalizeTerm(null!);
        Assert.Equal(string.Empty, term);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("   \t\r\n  ", "")]
    [InlineData("\u064E\u064B\u0640\u200B", "")]
    [InlineData("  Hello   World  ", "hello world")]
    [InlineData("\tBAD\r\n   \t WORD\n", "bad word")]
    [InlineData("  أَحْـمَـق   جِدّاً  ", "احمق جدا")]
    [InlineData("  کَـلِمَـة   أُخْرَى  ", "كلمه اخري")]
    public void NormalizeTerm_TrimsAndCollapsesInternalWhitespaceRuns(string term, string expected)
    {
        Assert.Equal(expected, TextNormalizer.NormalizeTerm(term));
    }
}
