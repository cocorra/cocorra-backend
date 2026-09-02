namespace Cocorra.DAL.DTOS.AnalyticsDto
{
    /// <summary>
    /// AN-036 — display context for analytics that are computed in UTC.
    ///
    /// Every stored timestamp, every bucket boundary and every hour-of-day grouping in this
    /// system is UTC, and that is not negotiable: a server that buckets in local time produces
    /// series that shift under it twice a year. But Cocorra's user base is predominantly
    /// UTC+2/+3, so a peak-hour chart rendered in raw UTC points a coach at a slot two to three
    /// hours away from the real one — a wrong answer delivered confidently, which is worse than
    /// no answer.
    ///
    /// The resolution is that the server keeps computing in UTC and additionally states the
    /// offset a reader should apply. Clients convert for display only; they must never send
    /// local times back.
    /// </summary>
    public static class AnalyticsDisplayDefaults
    {
        /// <summary>
        /// Minutes to add to a UTC hour to reach the audience's local hour. Defaults to +180
        /// (UTC+3). Override with <c>Analytics:DisplayTimeZoneOffsetMinutes</c> if the user base
        /// moves; a single constant here is what keeps the supply-health heatmap and the
        /// peak-hours chart from disagreeing about what "8pm" means.
        /// </summary>
        public const int TimeZoneOffsetMinutes = 180;
    }
}
