using System;
using System.Globalization;
using Cocorra.BLL.Services.Analytics;
using Xunit;

namespace Cocorra.Tests;

public class StateSnapshotScheduleTests
{
    private static DateTime Utc(string value) =>
        DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    [Theory]
    [InlineData("2026-11-11T10:00:00Z", "2026-11-11T22:15:00Z")] // midday Cairo, UTC+2; tonight's 00:15 Nov 12
    [InlineData("2026-11-11T21:59:59Z", "2026-11-11T22:15:00Z")] // 23:59:59 Cairo Nov 11
    [InlineData("2026-11-11T22:14:59Z", "2026-11-11T22:15:00Z")] // 00:14:59 Cairo Nov 12: today's run is still ahead
    [InlineData("2026-11-11T22:15:00Z", "2026-11-12T22:15:00Z")] // exactly at the run: strictly after, so next day
    [InlineData("2026-07-14T12:00:00Z", "2026-07-14T21:15:00Z")] // summer, UTC+3: 00:15 Cairo Jul 15
    [InlineData("2026-04-23T12:00:00Z", "2026-04-23T22:15:00Z")] // spring-forward: Apr 24 starts at 01:00 local = 22:00Z, run at 01:15 local
    [InlineData("2026-10-29T12:00:00Z", "2026-10-29T22:15:00Z")] // fall-back day Oct 29 is 25h long; Oct 30 starts 22:00Z
    [InlineData("2026-10-29T21:30:00Z", "2026-10-29T22:15:00Z")] // 23:30 Cairo in the repeated hour after fall-back
    public void NextScheduledRunUtc_IsFifteenMinutesAfterTheNextCairoDayStart(
        string nowUtc, string expectedUtc)
    {
        var result = StateSnapshotService.NextScheduledRunUtc(Utc(nowUtc));

        Assert.Equal(Utc(expectedUtc), result);
        Assert.Equal(DateTimeKind.Utc, result.Kind);
    }
}
