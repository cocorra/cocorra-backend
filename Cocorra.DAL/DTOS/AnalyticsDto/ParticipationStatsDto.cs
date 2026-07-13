namespace Cocorra.DAL.DTOS.AnalyticsDto
{
    public class TopSpeakerDto
    {
        public Guid UserId { get; set; }
        public string FullName { get; set; } = string.Empty;
        public double TotalSpokenSeconds { get; set; }
        public int RoomsParticipatedIn { get; set; }
    }

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

        public int UsersWhoSpoke { get; set; }
        public int UsersWhoRaisedHand { get; set; }

        /// <summary>Top 10 speakers by total spoken time.</summary>
        public IEnumerable<TopSpeakerDto> TopSpeakers { get; set; } = [];

        /// <summary>Join counts broken down by UTC hour (0–23).</summary>
        public IEnumerable<PeakHourDto> PeakHours { get; set; } = [];
    }
}
