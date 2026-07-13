namespace Cocorra.DAL.DTOS.AnalyticsDto
{
    public class RoomCategoryStatDto
    {
        public string Category { get; set; } = string.Empty;
        public int Count { get; set; }
        public double Percentage { get; set; }
    }

    public class TopRoomDto
    {
        public Guid RoomId { get; set; }
        public string RoomTitle { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public int ParticipantCount { get; set; }
        public double DurationHours { get; set; }
    }

    public class RoomAnalyticsDto
    {
        public DateTime From { get; set; }
        public DateTime To { get; set; }

        public int TotalRooms { get; set; }
        public int ScheduledRooms { get; set; }
        public int ActiveRooms { get; set; }
        public int EndedRooms { get; set; }

        public int PrivateRooms { get; set; }
        public int PublicRooms { get; set; }

        public double AvgParticipantsPerRoom { get; set; }
        public double AvgDurationHours { get; set; }

        public IEnumerable<RoomCategoryStatDto> RoomsByCategory { get; set; } = [];

        /// <summary>Top 10 rooms by participant count in the period.</summary>
        public IEnumerable<TopRoomDto> TopRooms { get; set; } = [];
    }
}
