using Cocorra.BLL.Services.AnalyticsService;
using Cocorra.DAL.AppMetaData;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cocorra.API.Controllers
{
    /// <summary>
    /// Data-Driven Decisions — analytics endpoints for Admins and Coaches.
    ///
    /// All endpoints:
    ///   - Require a valid JWT with VerificationStatus=Active (default policy).
    ///   - Require the Admin or Coach role.
    ///   - Default date range: last 30 days (resolved in the service layer).
    ///   - All timestamps in request/response are UTC.
    ///   - Responses are cached for 10 minutes; simultaneous requests share one DB query.
    /// </summary>
    [ApiController]
    [Authorize(Roles = "Admin,Coach")]
    public class AnalyticsController : ControllerBase
    {
        private readonly IAnalyticsService _analyticsService;

        public AnalyticsController(IAnalyticsService analyticsService)
        {
            _analyticsService = analyticsService;
        }

        /// <summary>
        /// Full platform snapshot: users, rooms, participation, and reports in one call.
        /// </summary>
        /// <param name="from">UTC start date (optional, default = 30 days ago).</param>
        /// <param name="to">UTC end date (optional, default = now).</param>
        [HttpGet(Router.AnalyticsRouting.Summary)]
        public async Task<IActionResult> GetPlatformSummary(
            [FromQuery] DateTime? from,
            [FromQuery] DateTime? to)
        {
            var result = await _analyticsService.GetPlatformSummaryAsync(from, to);
            return Ok(result);
        }

        /// <summary>
        /// User registration trends and status breakdown, bucketed by day or month.
        /// </summary>
        /// <param name="granularity">"daily" or "monthly" (default: "monthly").</param>
        /// <param name="from">UTC start date (optional, default = 30 days ago).</param>
        /// <param name="to">UTC end date (optional, default = now).</param>
        /// <param name="limit">Number of top entries (MBTI, etc.) to return (default: 10).</param>
        [HttpGet(Router.AnalyticsRouting.UserGrowth)]
        public async Task<IActionResult> GetUserGrowth(
            [FromQuery] string granularity = "monthly",
            [FromQuery] DateTime? from = null,
            [FromQuery] DateTime? to = null,
            [FromQuery] int limit = 10)
        {
            if (limit < 1 || limit > 100)
                return BadRequest(new { succeeded = false, message = "limit must be between 1 and 100." });

            var result = await _analyticsService.GetUserGrowthAsync(granularity, from, to, limit);
            if (!result.Succeeded)
                return BadRequest(result);

            return Ok(result);
        }

        /// <summary>
        /// Room creation stats: category breakdown, top rooms, public/private ratio.
        /// </summary>
        /// <param name="from">UTC start date (optional, default = 30 days ago).</param>
        /// <param name="to">UTC end date (optional, default = now).</param>
        /// <param name="limit">Number of top rooms to return (default: 10).</param>
        [HttpGet(Router.AnalyticsRouting.Rooms)]
        public async Task<IActionResult> GetRoomAnalytics(
            [FromQuery] DateTime? from = null,
            [FromQuery] DateTime? to = null,
            [FromQuery] int limit = 10)
        {
            if (limit < 1 || limit > 100)
                return BadRequest(new { succeeded = false, message = "limit must be between 1 and 100." });

            var result = await _analyticsService.GetRoomAnalyticsAsync(from, to, limit);
            return Ok(result);
        }

        /// <summary>
        /// Participation metrics: spoken time totals, top speakers, peak join hours (UTC).
        /// </summary>
        /// <param name="from">UTC start date (optional, default = 30 days ago).</param>
        /// <param name="to">UTC end date (optional, default = now).</param>
        /// <param name="limit">Number of top speakers to return (default: 10).</param>
        [HttpGet(Router.AnalyticsRouting.Participation)]
        public async Task<IActionResult> GetParticipationStats(
            [FromQuery] DateTime? from = null,
            [FromQuery] DateTime? to = null,
            [FromQuery] int limit = 10)
        {
            if (limit < 1 || limit > 100)
                return BadRequest(new { succeeded = false, message = "limit must be between 1 and 100." });

            var result = await _analyticsService.GetParticipationStatsAsync(from, to, limit);
            return Ok(result);
        }

        /// <summary>
        /// Report insights: open vs resolved, category breakdown, most reported users.
        /// </summary>
        /// <param name="from">UTC start date (optional, default = 30 days ago).</param>
        /// <param name="to">UTC end date (optional, default = now).</param>
        /// <param name="limit">Number of most-reported users to return (default: 10).</param>
        [HttpGet(Router.AnalyticsRouting.Reports)]
        public async Task<IActionResult> GetReportInsights(
            [FromQuery] DateTime? from = null,
            [FromQuery] DateTime? to = null,
            [FromQuery] int limit = 10)
        {
            if (limit < 1 || limit > 100)
                return BadRequest(new { succeeded = false, message = "limit must be between 1 and 100." });

            var result = await _analyticsService.GetReportInsightsAsync(from, to, limit);
            return Ok(result);
        }

        /// <summary>
        /// Funnel analysis over sequence of steps.
        /// </summary>
        [HttpGet(Router.AnalyticsRouting.Funnel)]
        public async Task<IActionResult> GetFunnel(
            [FromQuery] string steps = "user_registered,email_confirmed,activation_completed,room_joined,mic_activated",
            [FromQuery] DateTime? from = null,
            [FromQuery] DateTime? to = null)
        {
            var parsedSteps = steps.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var result = await _analyticsService.GetFunnelAsync(parsedSteps, from, to);
            if (!result.Succeeded)
                return BadRequest(result);

            return Ok(result);
        }

        /// <summary>
        /// Cohort retention metrics (D1, D7, D30) for a cohort.
        /// </summary>
        [HttpGet(Router.AnalyticsRouting.Retention)]
        public async Task<IActionResult> GetRetention(
            [FromQuery] string cohortEvent = "user_registered",
            [FromQuery] string activeEvent = "session_started",
            [FromQuery] DateTime? from = null,
            [FromQuery] DateTime? to = null)
        {
            var result = await _analyticsService.GetRetentionCohortAsync(cohortEvent, activeEvent, from, to);
            if (!result.Succeeded)
                return BadRequest(result);

            return Ok(result);
        }

        /// <summary>
        /// Most active rooms ranked by room_joined events (actual attendance).
        /// </summary>
        /// <param name="from">UTC start date (optional, default = 30 days ago).</param>
        /// <param name="to">UTC end date (optional, default = now).</param>
        /// <param name="limit">Number of rooms to return (default: 10).</param>
        [HttpGet(Router.AnalyticsRouting.ActiveRooms)]
        public async Task<IActionResult> GetMostActiveRooms(
            [FromQuery] DateTime? from = null,
            [FromQuery] DateTime? to = null,
            [FromQuery] int limit = 10)
        {
            if (limit < 1 || limit > 100)
                return BadRequest(new { succeeded = false, message = "limit must be between 1 and 100." });

            var result = await _analyticsService.GetMostActiveRoomsAsync(from, to, limit);
            return Ok(result);
        }

        /// <summary>
        /// Platform activity by UTC hour-of-day (0–23) — peak active hours.
        /// </summary>
        /// <param name="from">UTC start date (optional, default = 30 days ago).</param>
        /// <param name="to">UTC end date (optional, default = now).</param>
        [HttpGet(Router.AnalyticsRouting.PeakHours)]
        public async Task<IActionResult> GetPeakActiveHours(
            [FromQuery] DateTime? from = null,
            [FromQuery] DateTime? to = null)
        {
            var result = await _analyticsService.GetPeakActiveHoursAsync(from, to);
            return Ok(result);
        }

        /// <summary>
        /// Voice-verification drop-off rate: distinct users who started vs. completed activation.
        /// </summary>
        /// <param name="from">UTC start date (optional, default = 30 days ago).</param>
        /// <param name="to">UTC end date (optional, default = now).</param>
        [HttpGet(Router.AnalyticsRouting.VoiceDropOff)]
        public async Task<IActionResult> GetVoiceVerificationDropOff(
            [FromQuery] DateTime? from = null,
            [FromQuery] DateTime? to = null)
        {
            var result = await _analyticsService.GetVoiceVerificationDropOffAsync(from, to);
            return Ok(result);
        }

        /// <summary>
        /// Active (speakers) vs passive (listeners) participation rate among room joiners.
        /// </summary>
        /// <param name="from">UTC start date (optional, default = 30 days ago).</param>
        /// <param name="to">UTC end date (optional, default = now).</param>
        [HttpGet(Router.AnalyticsRouting.ActiveVsPassive)]
        public async Task<IActionResult> GetActiveVsPassiveRate(
            [FromQuery] DateTime? from = null,
            [FromQuery] DateTime? to = null)
        {
            var result = await _analyticsService.GetActiveVsPassiveRateAsync(from, to);
            return Ok(result);
        }
    }
}
