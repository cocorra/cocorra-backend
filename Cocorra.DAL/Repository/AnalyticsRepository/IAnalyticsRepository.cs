using Cocorra.DAL.DTOS.AnalyticsDto;

namespace Cocorra.DAL.Repository.AnalyticsRepository
{
    public interface IAnalyticsRepository
    {
        /// <summary>
        /// User registration and status growth bucketed by day or month.
        /// </summary>
        /// <param name="granularity">"daily" | "monthly"</param>
        /// <param name="from">Start date (UTC). Defaults to 30 days ago.</param>
        /// <param name="to">End date (UTC). Defaults to UtcNow.</param>
        /// <param name="topN">Number of top entries to return for sub-lists.</param>
        Task<UserGrowthDto> GetUserGrowthAsync(
            string granularity,
            DateTime from,
            DateTime to,
            int topN = 10);

        /// <summary>
        /// Room creation, category breakdown and top-room rankings.
        /// </summary>
        Task<RoomAnalyticsDto> GetRoomAnalyticsAsync(
            DateTime from,
            DateTime to,
            int topN = 10);

        /// <summary>
        /// Participation metrics: spoken time, top speakers, peak hours.
        /// </summary>
        Task<ParticipationStatsDto> GetParticipationStatsAsync(
            DateTime from,
            DateTime to,
            int topN = 10);

        /// <summary>
        /// Report summary: categories, status breakdown, most reported users.
        /// </summary>
        Task<ReportInsightsDto> GetReportInsightsAsync(
            DateTime from,
            DateTime to,
            int topN = 10);

        /// <summary>
        /// Returns the count of unique users who completed each event in the funnel steps.
        /// </summary>
        Task<Dictionary<string, int>> GetFunnelAsync(
            string[] steps,
            DateTime fromUtc,
            DateTime toUtc);

        /// <summary>
        /// Computes user retention metrics for a cohort defined by a cohort event and return events.
        /// </summary>
        Task<Dictionary<int, double>> GetRetentionCohortAsync(
            string cohortEvent,
            string activeEvent,
            DateTime cohortStartUtc,
            DateTime cohortEndUtc);

        /// <summary>
        /// Rooms ranked by number of room_joined events in the window (most active first).
        /// </summary>
        Task<List<TopActiveRoomDto>> GetMostActiveRoomsAsync(
            DateTime from,
            DateTime to,
            int topN = 10);

        /// <summary>
        /// Event activity bucketed by UTC hour-of-day (0–23), gaps filled with zeros.
        /// </summary>
        Task<List<HourlyActivityDto>> GetPeakActiveHoursAsync(
            DateTime from,
            DateTime to);

        /// <summary>
        /// Voice-verification drop-off: distinct users who submitted vs. completed activation.
        /// </summary>
        Task<VoiceVerificationFunnelDto> GetVoiceVerificationDropOffAsync(
            DateTime from,
            DateTime to);

        /// <summary>
        /// Active (took the mic) vs passive (join-only) split of room participants.
        /// </summary>
        Task<ParticipationModeDto> GetActiveVsPassiveRateAsync(
            DateTime from,
            DateTime to);
    }
}
