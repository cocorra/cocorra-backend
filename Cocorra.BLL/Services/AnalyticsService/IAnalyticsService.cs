using Cocorra.BLL.Base;
using Cocorra.DAL.DTOS.AnalyticsDto;

namespace Cocorra.BLL.Services.AnalyticsService
{
    public interface IAnalyticsService
    {
        /// <summary>
        /// Full platform snapshot: users + rooms + participation + reports.
        /// </summary>
        Task<Response<PlatformSummaryDto>> GetPlatformSummaryAsync(
            DateTime? from = null,
            DateTime? to = null);

        /// <summary>
        /// User registration and status growth over time.
        /// </summary>
        /// <param name="granularity">"daily" | "monthly" (default: "monthly")</param>
        Task<Response<UserGrowthDto>> GetUserGrowthAsync(
            string granularity = "monthly",
            DateTime? from = null,
            DateTime? to = null);

        Task<Response<RoomAnalyticsDto>> GetRoomAnalyticsAsync(
            DateTime? from = null,
            DateTime? to = null,
            int topN = 10);

        Task<Response<ParticipationStatsDto>> GetParticipationStatsAsync(
            DateTime? from = null,
            DateTime? to = null);

        Task<Response<ReportInsightsDto>> GetReportInsightsAsync(
            DateTime? from = null,
            DateTime? to = null,
            int topN = 10);

        Task<Response<Dictionary<string, int>>> GetFunnelAsync(
            string[] steps,
            DateTime? from = null,
            DateTime? to = null);

        Task<Response<SocialGraphDto>> GetSocialGraphAsync(DateTime? from = null, DateTime? to = null);

        Task<Response<MbtiAnalysisDto>> GetMbtiAnalysisAsync(DateTime? from = null, DateTime? to = null);

        Task<Response<CohortGridDto>> GetCohortGridAsync(DateTime? from = null, DateTime? to = null);

        Task<Response<SupplyHealthDto>> GetSupplyHealthAsync(
            string granularity = "monthly",
            DateTime? from = null,
            DateTime? to = null);

        Task<Response<ReportRateInsightsDto>> GetReportRateByCategoryAsync(
            DateTime? from = null,
            DateTime? to = null);

        Task<Response<ReviewLatencyDto>> GetReviewLatencyAsync(
            DateTime? from = null,
            DateTime? to = null);

        Task<Response<SupportAnalyticsDto>> GetSupportAnalyticsAsync(
            DateTime? from = null,
            DateTime? to = null);

        Task<Response<ActivationFunnelDto>> GetActivationFunnelAsync(
            string[]? steps = null,
            DateTime? from = null,
            DateTime? to = null);

        /// <summary>
        /// A-1: Platform Health — the north star (M-100) and its supporting inputs in one call,
        /// with an equal-length preceding window for comparison. Default range is a rolling
        /// 7 days, matching the north star's own window.
        /// </summary>
        Task<Response<PlatformHealthDto>> GetPlatformHealthAsync(
            DateTime? from = null,
            DateTime? to = null,
            string? compareTo = "previous_period");

        /// <summary>
        /// AN-027 / M-400: the stage participation funnel. Steps are fixed by the product flow
        /// rather than caller-supplied, unlike the activation funnel — the sequence
        /// join → raise → promote → unmute is the thing being measured, and letting a caller
        /// reorder it would produce a chart labelled M-400 that is not M-400.
        /// </summary>
        Task<Response<StageFunnelDto>> GetStageFunnelAsync(
            DateTime? from = null,
            DateTime? to = null);

        Task<Response<WeeklyReturnRateDto>> GetWeeklyReturnRateAsync(
            DateTime? from = null,
            DateTime? to = null);

        /// <summary>DEPRECATED — superseded by GetWeeklyReturnRateAsync (M-102).</summary>
        Task<Response<Dictionary<int, double>>> GetRetentionCohortAsync(
            string cohortEvent,
            string activeEvent,
            DateTime? cohortStart = null,
            DateTime? cohortEnd = null);

        /// <summary>Rooms ranked by join activity (most active first).</summary>
        Task<Response<List<TopActiveRoomDto>>> GetMostActiveRoomsAsync(
            DateTime? from = null,
            DateTime? to = null,
            int topN = 10);

        /// <summary>Event activity by UTC hour-of-day (0–23).</summary>
        Task<Response<List<HourlyActivityDto>>> GetPeakActiveHoursAsync(
            DateTime? from = null,
            DateTime? to = null);

        /// <summary>Voice-verification drop-off (submitted vs. completed).</summary>
        Task<Response<VoiceVerificationFunnelDto>> GetVoiceVerificationDropOffAsync(
            DateTime? from = null,
            DateTime? to = null);

        /// <summary>Active (speakers) vs passive (listeners) participation rate.</summary>
        Task<Response<ParticipationModeDto>> GetActiveVsPassiveRateAsync(
            DateTime? from = null,
            DateTime? to = null);
    }
}
