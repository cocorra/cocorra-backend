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
            DateTime? to = null,
            int topN = 10);

        Task<Response<RoomAnalyticsDto>> GetRoomAnalyticsAsync(
            DateTime? from = null,
            DateTime? to = null,
            int topN = 10);

        Task<Response<ParticipationStatsDto>> GetParticipationStatsAsync(
            DateTime? from = null,
            DateTime? to = null,
            int topN = 10);

        Task<Response<ReportInsightsDto>> GetReportInsightsAsync(
            DateTime? from = null,
            DateTime? to = null,
            int topN = 10);

        Task<Response<Dictionary<string, int>>> GetFunnelAsync(
            string[] steps,
            DateTime? from = null,
            DateTime? to = null);

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
