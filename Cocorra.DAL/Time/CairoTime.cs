using System;
using System.Collections.Generic;

namespace Cocorra.DAL.Time
{
    /// <summary>
    /// The one place that knows the business calendar is Africa/Cairo. Instants are stored and
    /// compared in UTC everywhere; this class only answers "which Cairo day, week, month or hour
    /// does this instant belong to" and "where does a Cairo day start in UTC".
    ///
    /// Calendar values (days, weeks, months) are returned as midnight DateTimes with
    /// <see cref="DateTimeKind.Unspecified"/>, because they name a date, not an instant.
    /// </summary>
    public static class CairoTime
    {
        public const string ZoneId = "Africa/Cairo";

        private static readonly TimeSpan SpringForwardProbeStep = TimeSpan.FromMinutes(15);

        public static TimeZoneInfo Zone { get; } = TimeZoneInfo.FindSystemTimeZoneById(ZoneId);

        public static DateTime Today => DateOf(DateTime.UtcNow);

        public static DateTime ToLocal(DateTime utc) =>
            TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone);

        public static DateTime DateOf(DateTime utc) => ToLocal(utc).Date;

        /// <summary>
        /// Reads a caller-supplied calendar date. A value without an offset ("2026-11-11") is the
        /// Cairo date exactly as written; a value carrying an offset is an instant and is mapped
        /// to the Cairo date it falls on.
        /// </summary>
        public static DateTime CalendarDateOf(DateTime value) =>
            value.Kind == DateTimeKind.Unspecified ? value.Date : DateOf(value.ToUniversalTime());

        public static int HourOf(DateTime utc) => ToLocal(utc).Hour;

        public static DateTime WeekOf(DateTime utc) => StartOfWeek(DateOf(utc));

        public static DateTime MonthOf(DateTime utc) => StartOfMonth(DateOf(utc));

        /// <summary>Weeks start on Monday.</summary>
        public static DateTime StartOfWeek(DateTime localDate)
        {
            var date = localDate.Date;
            var daysSinceMonday = ((int)date.DayOfWeek + 6) % 7;
            return date.AddDays(-daysSinceMonday);
        }

        public static DateTime StartOfMonth(DateTime localDate) =>
            new(localDate.Year, localDate.Month, 1, 0, 0, 0, DateTimeKind.Unspecified);

        /// <summary>
        /// The UTC instant at which the given Cairo calendar date begins. Egypt moves its clocks
        /// forward at local midnight, so on that date 00:00 does not exist and the day begins at
        /// the first valid local time instead.
        /// </summary>
        public static DateTime StartOfDayUtc(DateTime localDate)
        {
            var localStart = DateTime.SpecifyKind(localDate.Date, DateTimeKind.Unspecified);
            while (Zone.IsInvalidTime(localStart))
            {
                localStart = localStart.Add(SpringForwardProbeStep);
            }

            return TimeZoneInfo.ConvertTimeToUtc(localStart, Zone);
        }

        /// <summary>
        /// Splits [fromUtc, toUtc] into half-open spans over which Cairo's UTC offset is constant,
        /// so a query can group by UTC hour inside a span and shift by that span's offset. The
        /// last span ends one tick after <paramref name="toUtc"/> to keep the inclusive bound.
        /// Offsets change only on whole UTC hours, so probing hour by hour finds every change.
        /// </summary>
        public static IReadOnlyList<(DateTime StartUtc, DateTime EndUtc, TimeSpan Offset)> OffsetPeriods(
            DateTime fromUtc,
            DateTime toUtc)
        {
            var periods = new List<(DateTime, DateTime, TimeSpan)>();
            var end = toUtc.AddTicks(1);
            var start = DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc);
            var offset = Zone.GetUtcOffset(start);

            for (var probe = new DateTime(start.Year, start.Month, start.Day, start.Hour, 0, 0, DateTimeKind.Utc).AddHours(1);
                 probe < end;
                 probe = probe.AddHours(1))
            {
                var probeOffset = Zone.GetUtcOffset(probe);
                if (probeOffset == offset)
                    continue;

                periods.Add((start, probe, offset));
                start = probe;
                offset = probeOffset;
            }

            periods.Add((start, end, offset));
            return periods;
        }

        /// <summary>Half-open UTC range [start, end) covering one Cairo calendar date (23–25 hours).</summary>
        public static (DateTime StartUtc, DateTime EndUtc) DayRangeUtc(DateTime localDate) =>
            (StartOfDayUtc(localDate), StartOfDayUtc(localDate.Date.AddDays(1)));
    }
}
