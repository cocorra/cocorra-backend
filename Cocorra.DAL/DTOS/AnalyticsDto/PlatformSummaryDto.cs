namespace Cocorra.DAL.DTOS.AnalyticsDto
{
    /// <summary>
    /// One-stop-shop snapshot of the entire Cocorra platform.
    /// Returned by GET /Api/V1/Analytics/Summary.
    /// </summary>
    public class PlatformSummaryDto
    {
        public UserGrowthDto Users { get; set; } = new();
        public RoomAnalyticsDto Rooms { get; set; } = new();
        public ParticipationStatsDto Participation { get; set; } = new();
        public ReportInsightsDto Reports { get; set; } = new();

        /// <summary>UTC timestamp when this snapshot was generated.</summary>
        public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
    }
}
