namespace Cocorra.DAL.DTOS.AnalyticsDto
{
    /// <summary>A room ranked by join activity (from room_joined events).</summary>
    public class TopActiveRoomDto
    {
        public Guid RoomId { get; set; }
        public string RoomTitle { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;

        /// <summary>Total room_joined events (includes rejoins).</summary>
        public int JoinEvents { get; set; }

        /// <summary>Distinct users who joined — a large gap vs. JoinEvents hints at a churny room.</summary>
        public int UniqueJoiners { get; set; }
    }

    /// <summary>Activity in a single UTC hour-of-day bucket (0–23).</summary>
    public class HourlyActivityDto
    {
        public int Hour { get; set; }
        public int EventCount { get; set; }
        public int ActiveUsers { get; set; }
    }

    /// <summary>Voice-verification funnel: how many start vs. complete activation.</summary>
    public class VoiceVerificationFunnelDto
    {
        public DateTime From { get; set; }
        public DateTime To { get; set; }

        public int Started { get; set; }
        public int Completed { get; set; }

        /// <summary>1 − (Completed / Started), as a percentage.</summary>
        public double DropOffRate { get; set; }

        /// <summary>Completed / Started, as a percentage.</summary>
        public double CompletionRate { get; set; }
    }

    /// <summary>Active (speakers) vs passive (listeners) split of room participants.</summary>
    public class ParticipationModeDto
    {
        public DateTime From { get; set; }
        public DateTime To { get; set; }

        public int TotalParticipants { get; set; }
        public int ActiveSpeakers { get; set; }
        public int PassiveListeners { get; set; }

        /// <summary>ActiveSpeakers / TotalParticipants, as a percentage.</summary>
        public double ActiveRate { get; set; }
    }
}
