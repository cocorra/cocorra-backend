namespace Cocorra.DAL.DTOS.AnalyticsDto
{
    /// <summary>
    /// One weekly cohort for M-102 (AN-006).
    /// </summary>
    public class WeeklyReturnCohortDto
    {
        /// <summary>Monday (UTC) that opens the cohort week.</summary>
        public DateTime WeekStartUtc { get; set; }

        /// <summary>Distinct non-host users with a room_joined event in this week.</summary>
        public int CohortSize { get; set; }

        /// <summary>Of those, how many had a room_joined event in any LATER week.</summary>
        public int ReturnedInLaterWeek { get; set; }

        public double ReturnRatePercent { get; set; }

        /// <summary>
        /// False for the most recent week(s), where there is not yet a later week to return in.
        /// An incomplete cohort must not be charted as a collapse in retention.
        /// </summary>
        public bool IsComplete { get; set; }
    }

    public class WeeklyReturnRateDto
    {
        public DateTime From { get; set; }
        public DateTime To { get; set; }

        public IEnumerable<WeeklyReturnCohortDto> Cohorts { get; set; } = [];

        /// <summary>Earliest room_joined event backing this series.</summary>
        public DateTime? DataAvailableFromUtc { get; set; }
    }
}
