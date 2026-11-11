using System;
using System.Globalization;
using Cocorra.DAL.Time;
using Xunit;

namespace Cocorra.Tests;

public class CairoTimeTests
{
    private static DateTime Utc(string value) =>
        DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    [Theory]
    [InlineData("2026-11-10T21:59:59Z", 2026, 11, 10, 23)]
    [InlineData("2026-11-10T22:00:00Z", 2026, 11, 11, 0)]
    [InlineData("2026-07-01T20:59:59Z", 2026, 7, 1, 23)]
    [InlineData("2026-07-01T21:00:00Z", 2026, 7, 2, 0)]
    [InlineData("2026-04-23T21:59:59Z", 2026, 4, 23, 23)]
    [InlineData("2026-04-23T22:00:00Z", 2026, 4, 24, 1)]
    [InlineData("2026-10-29T20:59:59Z", 2026, 10, 29, 23)]
    [InlineData("2026-10-29T21:00:00Z", 2026, 10, 29, 23)]
    [InlineData("2026-10-29T22:00:00Z", 2026, 10, 30, 0)]
    [InlineData("2026-01-15T18:10:00Z", 2026, 1, 15, 20)]
    [InlineData("2026-07-15T18:10:00Z", 2026, 7, 15, 21)]
    [InlineData("2026-07-15T17:10:00Z", 2026, 7, 15, 20)]
    public void DateOfAndHourOf_MapInstantToCairoCalendarDayAndHour(
        string utc, int year, int month, int day, int hour)
    {
        var instant = Utc(utc);

        Assert.Equal(new DateTime(year, month, day), CairoTime.DateOf(instant));
        Assert.Equal(hour, CairoTime.HourOf(instant));
    }

    [Theory]
    [InlineData(2026, 11, 11, "2026-11-10T22:00:00Z", "2026-11-11T22:00:00Z", 24)]
    [InlineData(2026, 7, 2, "2026-07-01T21:00:00Z", "2026-07-02T21:00:00Z", 24)]
    [InlineData(2026, 4, 24, "2026-04-23T22:00:00Z", "2026-04-24T21:00:00Z", 23)]
    [InlineData(2026, 10, 29, "2026-10-28T21:00:00Z", "2026-10-29T22:00:00Z", 25)]
    public void DayRangeUtc_CoversTheCairoDay_WithItsRealLength(
        int year, int month, int day, string expectedStart, string expectedEnd, int expectedHours)
    {
        var (start, end) = CairoTime.DayRangeUtc(new DateTime(year, month, day));

        Assert.Equal(Utc(expectedStart), start);
        Assert.Equal(Utc(expectedEnd), end);
        Assert.Equal(expectedHours, (end - start).TotalHours);
    }

    [Fact]
    public void OffsetPeriods_WithoutDstChange_ReturnsOnePeriodEndingOneTickAfterTo()
    {
        var from = Utc("2026-11-01T00:00:00Z");
        var to = Utc("2026-11-05T00:00:00Z");

        var periods = CairoTime.OffsetPeriods(from, to);

        var period = Assert.Single(periods);
        Assert.Equal(from, period.StartUtc);
        Assert.Equal(to.AddTicks(1), period.EndUtc);
        Assert.Equal(TimeSpan.FromHours(2), period.Offset);
    }

    [Fact]
    public void OffsetPeriods_AcrossSpringForward_SplitsAtTheOffsetChange()
    {
        var from = Utc("2026-04-23T12:00:00Z");
        var to = Utc("2026-04-25T00:00:00Z");
        var transition = Utc("2026-04-23T22:00:00Z");

        var periods = CairoTime.OffsetPeriods(from, to);

        Assert.Equal(2, periods.Count);
        Assert.Equal(from, periods[0].StartUtc);
        Assert.Equal(transition, periods[0].EndUtc);
        Assert.Equal(TimeSpan.FromHours(2), periods[0].Offset);
        Assert.Equal(transition, periods[1].StartUtc);
        Assert.Equal(to.AddTicks(1), periods[1].EndUtc);
        Assert.Equal(TimeSpan.FromHours(3), periods[1].Offset);
    }

    [Fact]
    public void OffsetPeriods_AcrossFallBack_SplitsAtTheOffsetChange()
    {
        var from = Utc("2026-10-29T12:00:00Z");
        var to = Utc("2026-10-30T12:00:00Z");
        var transition = Utc("2026-10-29T21:00:00Z");

        var periods = CairoTime.OffsetPeriods(from, to);

        Assert.Equal(2, periods.Count);
        Assert.Equal(from, periods[0].StartUtc);
        Assert.Equal(transition, periods[0].EndUtc);
        Assert.Equal(TimeSpan.FromHours(3), periods[0].Offset);
        Assert.Equal(transition, periods[1].StartUtc);
        Assert.Equal(to.AddTicks(1), periods[1].EndUtc);
        Assert.Equal(TimeSpan.FromHours(2), periods[1].Offset);
    }

    [Fact]
    public void CalendarDateOf_UnspecifiedValue_IsTakenExactlyAsWritten()
    {
        var input = new DateTime(2026, 11, 11, 23, 30, 0, DateTimeKind.Unspecified);

        var result = CairoTime.CalendarDateOf(input);

        Assert.Equal(new DateTime(2026, 11, 11), result);
        Assert.Equal(DateTimeKind.Unspecified, result.Kind);
    }

    [Fact]
    public void CalendarDateOf_UtcValue_IsMappedToItsCairoDate()
    {
        var result = CairoTime.CalendarDateOf(Utc("2026-11-10T22:30:00Z"));

        Assert.Equal(new DateTime(2026, 11, 11), result);
    }

    [Theory]
    [InlineData(2026, 11, 15, 2026, 11, 9)]
    [InlineData(2026, 11, 9, 2026, 11, 9)]
    public void StartOfWeek_StartsOnMonday(
        int year, int month, int day, int expectedYear, int expectedMonth, int expectedDay)
    {
        var result = CairoTime.StartOfWeek(new DateTime(year, month, day));

        Assert.Equal(new DateTime(expectedYear, expectedMonth, expectedDay), result);
    }

    [Fact]
    public void WeekOf_InstantThatIsCairoMonday_ReturnsTheSameMonday()
    {
        Assert.Equal(new DateTime(2026, 11, 16), CairoTime.WeekOf(Utc("2026-11-15T22:30:00Z")));
    }

    [Fact]
    public void MonthOf_InstantThatIsCairoDecemberFirst_ReturnsDecemberFirst()
    {
        Assert.Equal(new DateTime(2026, 12, 1), CairoTime.MonthOf(Utc("2026-11-30T22:30:00Z")));
    }

    [Fact]
    public void CalendarValues_AreReturnedAsUnspecified()
    {
        var instant = Utc("2026-11-15T22:30:00Z");

        Assert.Equal(DateTimeKind.Unspecified, CairoTime.DateOf(instant).Kind);
        Assert.Equal(DateTimeKind.Unspecified, CairoTime.WeekOf(instant).Kind);
        Assert.Equal(DateTimeKind.Unspecified, CairoTime.MonthOf(instant).Kind);
        Assert.Equal(DateTimeKind.Unspecified, CairoTime.StartOfWeek(new DateTime(2026, 11, 15)).Kind);
        Assert.Equal(DateTimeKind.Unspecified, CairoTime.StartOfMonth(new DateTime(2026, 11, 15)).Kind);
        Assert.Equal(
            DateTimeKind.Unspecified,
            CairoTime.CalendarDateOf(Utc("2026-11-10T22:30:00Z")).Kind);
    }

    [Fact]
    public void StartOfDayUtc_ReturnsUtcKind()
    {
        Assert.Equal(DateTimeKind.Utc, CairoTime.StartOfDayUtc(new DateTime(2026, 11, 11)).Kind);
    }
}
