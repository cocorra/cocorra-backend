using Cocorra.BLL.Services.ContentModeration;
using Cocorra.DAL.Enums;
using Xunit;

namespace Cocorra.Tests.ContentModeration;

public class ReportSeverityPolicyTests
{
    [Theory]
    [InlineData(ReportCategory.Harassment, ModerationSeverity.High)]
    [InlineData(ReportCategory.InappropriateContent, ModerationSeverity.Medium)]
    [InlineData(ReportCategory.FakeIdentity, ModerationSeverity.Medium)]
    [InlineData(ReportCategory.Spam, ModerationSeverity.Low)]
    [InlineData(ReportCategory.Other, ModerationSeverity.Low)]
    public void BaseFor_AllReportCategories_ReturnsExpectedBaseSeverity(
        ReportCategory category, ModerationSeverity expected)
    {
        Assert.Equal(expected, ReportSeverityPolicy.BaseFor(category));
    }

    [Theory]
    [InlineData(ReportCategory.Spam, ModerationSeverity.High, ModerationSeverity.High)]
    [InlineData(ReportCategory.Other, ModerationSeverity.Critical, ModerationSeverity.Critical)]
    [InlineData(ReportCategory.Harassment, ModerationSeverity.Low, ModerationSeverity.High)]
    [InlineData(ReportCategory.Harassment, ModerationSeverity.Medium, ModerationSeverity.High)]
    [InlineData(ReportCategory.FakeIdentity, ModerationSeverity.Low, ModerationSeverity.Medium)]
    [InlineData(ReportCategory.InappropriateContent, null, ModerationSeverity.Medium)]
    public void Compute_DictionarySeverity_RaisesButNeverLowersBaseSeverity(
        ReportCategory category, ModerationSeverity? dictionarySeverity, ModerationSeverity expected)
    {
        var actual = ReportSeverityPolicy.Compute(
            category,
            dictionarySeverity,
            distinctReportersOnMessage: 1,
            openReportsAgainstSenderLast24h: 1);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(ReportCategory.Spam, 1, ModerationSeverity.Low)]
    [InlineData(ReportCategory.Spam, 2, ModerationSeverity.Low)]
    [InlineData(ReportCategory.Spam, 3, ModerationSeverity.Medium)]
    [InlineData(ReportCategory.Spam, 4, ModerationSeverity.Medium)]
    [InlineData(ReportCategory.Spam, 5, ModerationSeverity.Critical)]
    [InlineData(ReportCategory.Other, 6, ModerationSeverity.Critical)]
    [InlineData(ReportCategory.InappropriateContent, 2, ModerationSeverity.Medium)]
    [InlineData(ReportCategory.InappropriateContent, 3, ModerationSeverity.High)]
    [InlineData(ReportCategory.InappropriateContent, 5, ModerationSeverity.Critical)]
    [InlineData(ReportCategory.Harassment, 2, ModerationSeverity.High)]
    [InlineData(ReportCategory.Harassment, 3, ModerationSeverity.Critical)]
    public void Compute_DistinctReportersBoundaries_EscalatesAt3AndCriticalAt5(
        ReportCategory category, int distinctReporters, ModerationSeverity expected)
    {
        var actual = ReportSeverityPolicy.Compute(
            category,
            dictionarySeverity: null,
            distinctReportersOnMessage: distinctReporters,
            openReportsAgainstSenderLast24h: 1);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(ReportCategory.Spam, 1, 2, ModerationSeverity.Low)]
    [InlineData(ReportCategory.Spam, 1, 3, ModerationSeverity.Medium)]
    [InlineData(ReportCategory.InappropriateContent, 1, 3, ModerationSeverity.High)]
    [InlineData(ReportCategory.Spam, 3, 3, ModerationSeverity.High)]
    [InlineData(ReportCategory.InappropriateContent, 3, 3, ModerationSeverity.Critical)]
    [InlineData(ReportCategory.Harassment, 4, 10, ModerationSeverity.Critical)]
    public void Compute_OpenReportsAgainstSenderAndCombinedEscalation_RaisesAndCapsAtCritical(
        ReportCategory category, int distinctReporters, int openReportsAgainstSender, ModerationSeverity expected)
    {
        var actual = ReportSeverityPolicy.Compute(
            category,
            dictionarySeverity: null,
            distinctReportersOnMessage: distinctReporters,
            openReportsAgainstSenderLast24h: openReportsAgainstSender);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Compute_CriticalDictionaryWithMaxReportersAndOpenReports_NeverExceedsCritical()
    {
        var actual = ReportSeverityPolicy.Compute(
            ReportCategory.Harassment,
            dictionarySeverity: ModerationSeverity.Critical,
            distinctReportersOnMessage: 10,
            openReportsAgainstSenderLast24h: 10);

        Assert.Equal(ModerationSeverity.Critical, actual);
    }
}
