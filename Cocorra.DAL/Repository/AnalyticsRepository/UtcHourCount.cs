namespace Cocorra.DAL.Repository.AnalyticsRepository
{
    /// <summary>
    /// A row count for one UTC clock hour, grouped in SQL. Egypt's offset is always a whole
    /// number of hours, so every UTC hour falls inside exactly one Cairo hour and day; grouping
    /// by UTC hour in the database and mapping to Cairo afterwards is exact, and avoids
    /// time-zone SQL that the test providers cannot translate.
    /// </summary>
    internal sealed class UtcHourCount
    {
        public DateTime Date { get; init; }
        public int Hour { get; init; }
        public int Count { get; init; }

        public DateTime StartUtc => DateTime.SpecifyKind(Date.AddHours(Hour), DateTimeKind.Utc);
    }
}
