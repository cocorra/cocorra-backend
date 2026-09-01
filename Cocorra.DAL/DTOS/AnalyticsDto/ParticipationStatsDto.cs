namespace Cocorra.DAL.DTOS.AnalyticsDto
{
    public class PeakHourDto
    {
        /// <summary>UTC hour (0–23).</summary>
        public int Hour { get; set; }
        public int JoinCount { get; set; }
    }

    public class ParticipationStatsDto
    {
        public DateTime From { get; set; }
        public DateTime To { get; set; }

        public int TotalParticipations { get; set; }
        public double AvgSpokenSecondsPerParticipant { get; set; }
        public double TotalSpokenHours { get; set; }

        /// <summary>
        /// Distinct non-host users with a mic_activated event in the window (M-101).
        /// Derived from the event, not from TotalSpokenSeconds: a participant on stage is
        /// unmuted by default, so accrued seconds include idle open-mic time.
        /// </summary>
        public int UsersWhoSpoke { get; set; }

        /// <summary>Join counts broken down by UTC hour (0–23).</summary>
        public IEnumerable<PeakHourDto> PeakHours { get; set; } = [];

        // AN-005 / R-8: TopSpeakers and UsersWhoRaisedHand are removed rather than zeroed.
        // TopSpeakers ranked hosts by room length because a host's mic opens with the room;
        // UsersWhoRaisedHand read a transient live-state flag that is reset on approval.
        // Returning 0 or [] would read as "nobody raised a hand", which is a false statement
        // about the platform rather than an admission that it is not measured. A caller that
        // needs either must wait for the events in AN-018.
    }
}
