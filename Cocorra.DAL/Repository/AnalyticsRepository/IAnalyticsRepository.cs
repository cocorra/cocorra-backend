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
        Task<UserGrowthDto> GetUserGrowthAsync(
            string granularity,
            DateTime from,
            DateTime to);

        /// <summary>
        /// Room creation, category breakdown and top-room rankings.
        /// </summary>
        Task<RoomAnalyticsDto> GetRoomAnalyticsAsync(
            DateTime from,
            DateTime to,
            int topN = 10);

        /// <summary>
        /// Participation metrics: spoken time, distinct speakers, peak hours.
        /// Host-excluded (AN-005). Top speakers and hand-raise counts are removed, not zeroed.
        /// </summary>
        Task<ParticipationStatsDto> GetParticipationStatsAsync(
            DateTime from,
            DateTime to);

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
        /// <summary>
        /// AN-020 / M-200..M-204: supply-side health — active hosts, host retention,
        /// concentration and schedule. Entirely from Rooms, which is never purged.
        /// </summary>
        Task<SupplyHealthDto> GetSupplyHealthAsync(string granularity, DateTime fromUtc, DateTime toUtc);

        /// <summary>AN-021 / M-301: reports per 1,000 joins, by room category.</summary>
        Task<ReportRateInsightsDto> GetReportRateByCategoryAsync(DateTime fromUtc, DateTime toUtc);

        /// <summary>AN-022 / M-302: voice-verification review latency percentiles. No mean.</summary>
        Task<ReviewLatencyDto> GetReviewLatencyAsync(DateTime fromUtc, DateTime toUtc);

        /// <summary>AN-029 / M-701: social graph reciprocity and message volume.</summary>
        Task<SocialGraphDto> GetSocialGraphAsync(DateTime fromUtc, DateTime toUtc);

        /// <summary>AN-037 / M-702: MBTI tested as four dichotomies, not sixteen types.</summary>
        Task<MbtiAnalysisDto> GetMbtiDichotomyAnalysisAsync(DateTime fromUtc, DateTime toUtc);

        /// <summary>AN-038 / M-103: weekly cohort retention grid. Needs ~8 weeks to be readable.</summary>
        Task<CohortGridDto> GetCohortGridAsync(DateTime fromUtc, DateTime toUtc);

        /// <summary>AN-023 / M-601: support ticket and chat analytics.</summary>
        Task<SupportAnalyticsDto> GetSupportAnalyticsAsync(DateTime fromUtc, DateTime toUtc);

        /// <summary>
        /// AN-007 / M-507: sequential activation funnel with median and p90 elapsed time
        /// between consecutive steps.
        /// </summary>
        Task<ActivationFunnelDto> GetActivationFunnelAsync(
            string[] steps,
            DateTime fromUtc,
            DateTime toUtc);

        /// <summary>
        /// AN-006 / M-102: of non-host users who joined a room in week N, the share who joined
        /// again in any later week. Server-authoritative; reads no session_started rows.
        /// </summary>
        Task<WeeklyReturnRateDto> GetWeeklyReturnRateAsync(
            DateTime fromUtc,
            DateTime toUtc);

        /// <summary>
        /// DEPRECATED (M-102-LEGACY, graded UNRELIABLE): exact-day cohort matching over a
        /// caller-supplied activity signal that defaults to the cookie-derived session_started.
        /// Retained until the dashboard cuts over to GetWeeklyReturnRateAsync.
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
