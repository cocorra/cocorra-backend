using Cocorra.BLL.Services.Analytics;
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
        private readonly IMetricRegistry _metricRegistry;
        private readonly IPipelineHealthService _pipelineHealth;
        private readonly IAnalyticsBackfillService _backfill;
        private readonly IDecisionCenterService _decisionCenter;

        public AnalyticsController(
            IAnalyticsService analyticsService,
            IMetricRegistry metricRegistry,
            IPipelineHealthService pipelineHealth,
            IAnalyticsBackfillService backfill,
            IDecisionCenterService decisionCenter)
        {
            _analyticsService = analyticsService;
            _metricRegistry = metricRegistry;
            _pipelineHealth = pipelineHealth;
            _backfill = backfill;
            _decisionCenter = decisionCenter;
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
            [FromQuery] DateTime? to = null)
        {
            var result = await _analyticsService.GetUserGrowthAsync(granularity, from, to);
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
        [HttpGet(Router.AnalyticsRouting.Participation)]
        public async Task<IActionResult> GetParticipationStats(
            [FromQuery] DateTime? from = null,
            [FromQuery] DateTime? to = null)
        {
            var result = await _analyticsService.GetParticipationStatsAsync(from, to);
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
        /// AN-007 / M-507: sequential activation funnel with median and p90 elapsed time
        /// between consecutive steps. Replaces GET /Analytics/Funnel, which counts each step
        /// independently and can therefore widen downward.
        /// </summary>
        [HttpGet(Router.AnalyticsRouting.ActivationFunnel)]
        public async Task<IActionResult> GetActivationFunnel(
            [FromQuery] string? steps = null,
            [FromQuery] DateTime? from = null,
            [FromQuery] DateTime? to = null)
        {
            var parsedSteps = string.IsNullOrWhiteSpace(steps)
                ? null
                : steps.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var result = await _analyticsService.GetActivationFunnelAsync(parsedSteps, from, to);
            if (!result.Succeeded)
                return BadRequest(result);

            return Ok(result);
        }

        /// <summary>
        /// AN-006 / M-102: of non-host users who joined a room in week N, the share who joined
        /// again in any later week. Replaces GET /Analytics/Retention, which matched activity
        /// on exactly day N over a cookie-derived signal.
        /// </summary>
        [HttpGet(Router.AnalyticsRouting.ReturnRate)]
        public async Task<IActionResult> GetWeeklyReturnRate(
            [FromQuery] DateTime? from = null,
            [FromQuery] DateTime? to = null)
        {
            var result = await _analyticsService.GetWeeklyReturnRateAsync(from, to);
            if (!result.Succeeded)
                return BadRequest(result);

            return Ok(result);
        }

        /// <summary>
        /// AN-020: supply-side health — distinct active hosts per period, whether first-time
        /// hosts run a second room, how concentrated supply is, and when rooms actually start.
        /// All from Rooms, which is relational and never purged, so this reaches back further
        /// than any event-derived metric.
        /// </summary>
        [HttpGet(Router.AnalyticsRouting.SupplyHealth)]
        public async Task<IActionResult> GetSupplyHealth(
            [FromQuery] string granularity = "monthly",
            [FromQuery] DateTime? from = null,
            [FromQuery] DateTime? to = null)
        {
            var result = await _analyticsService.GetSupplyHealthAsync(granularity, from, to);
            if (!result.Succeeded)
                return BadRequest(result);

            return Ok(result);
        }

        /// <summary>
        /// AN-021: reports per 1,000 room joins, split by room category. Admin only — this is
        /// moderation data, and a Coach has no need for platform-wide safety rates.
        /// </summary>
        [HttpGet(Router.AnalyticsRouting.ReportRate)]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> GetReportRateByCategory(
            [FromQuery] DateTime? from = null,
            [FromQuery] DateTime? to = null)
        {
            var result = await _analyticsService.GetReportRateByCategoryAsync(from, to);
            if (!result.Succeeded)
                return BadRequest(result);

            return Ok(result);
        }

        /// <summary>
        /// AN-022: voice-verification review latency. Percentiles only — no mean is returned,
        /// by contract, because a mean over a bimodal wait hides the users being harmed.
        /// </summary>
        [HttpGet(Router.AnalyticsRouting.ReviewLatency)]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> GetReviewLatency(
            [FromQuery] DateTime? from = null,
            [FromQuery] DateTime? to = null)
        {
            var result = await _analyticsService.GetReviewLatencyAsync(from, to);
            if (!result.Succeeded)
                return BadRequest(result);

            return Ok(result);
        }

        /// <summary>
        /// AN-023: support ticket and chat analytics. A PROXY for product reliability — there
        /// is no error tracking, so this counts problems users reported, not failures.
        /// </summary>
        [HttpGet(Router.AnalyticsRouting.Support)]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> GetSupportAnalytics(
            [FromQuery] DateTime? from = null,
            [FromQuery] DateTime? to = null)
        {
            var result = await _analyticsService.GetSupportAnalyticsAsync(from, to);
            if (!result.Succeeded)
                return BadRequest(result);

            return Ok(result);
        }

        /// <summary>
        /// AN-039: the Decision Center. Change detection across the watched signals, gated on
        /// having a baseline: until enough complete weeks of history exist, values are shown
        /// but no direction is asserted and nothing is flagged. Detection without a baseline
        /// fires on ordinary variance, and a dashboard that cries wolf early is ignored for
        /// good — harder to undo than a late launch.
        /// </summary>
        [HttpGet(Router.AnalyticsRouting.DecisionCenter)]
        public async Task<IActionResult> GetDecisionCenter(CancellationToken cancellationToken)
        {
            var result = await _decisionCenter.GetDecisionCenterAsync(cancellationToken);
            return Ok(new { succeeded = true, statusCode = 200, data = result });
        }

        /// <summary>
        /// AN-029: social graph health. Reciprocity is reported beside volume, because sends
        /// alone would let a spam wave read as engagement.
        /// </summary>
        [HttpGet(Router.AnalyticsRouting.Social)]
        public async Task<IActionResult> GetSocialGraph(
            [FromQuery] DateTime? from = null,
            [FromQuery] DateTime? to = null)
        {
            var result = await _analyticsService.GetSocialGraphAsync(from, to);
            return Ok(result);
        }

        /// <summary>
        /// AN-037: MBTI tested as four dichotomies rather than sixteen types. Observational —
        /// the response carries the caveat that a difference is an association, not a cause.
        /// </summary>
        [HttpGet(Router.AnalyticsRouting.Mbti)]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> GetMbtiDichotomies(
            [FromQuery] DateTime? from = null,
            [FromQuery] DateTime? to = null)
        {
            var result = await _analyticsService.GetMbtiAnalysisAsync(from, to);
            return Ok(result);
        }

        /// <summary>
        /// AN-038: weekly cohort retention grid. Check hasSufficientHistory before reading it
        /// as a trend — below about 8 weeks the grid is too sparse to support one.
        /// </summary>
        [HttpGet(Router.AnalyticsRouting.CohortGrid)]
        public async Task<IActionResult> GetCohortGrid(
            [FromQuery] DateTime? from = null,
            [FromQuery] DateTime? to = null)
        {
            var result = await _analyticsService.GetCohortGridAsync(from, to);
            return Ok(result);
        }

        /// <summary>
        /// AN-025: pipeline health. There is no structured logging sink, no APM and no metrics
        /// export in this deployment, so this endpoint is the one place a failing invariant is
        /// actually visible. A metric's trust level means nothing if the pipeline feeding it
        /// stopped three days ago.
        /// </summary>
        [HttpGet(Router.AnalyticsRouting.SystemHealth)]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> GetSystemHealth(CancellationToken cancellationToken)
        {
            var health = await _pipelineHealth.GetHealthAsync(cancellationToken);
            return Ok(new { succeeded = true, statusCode = 200, data = health });
        }

        /// <summary>
        /// AN-016: replay the read-model rollups over a historical date range, using the same
        /// code path as the live aggregation service.
        ///
        /// Operator action, not a scheduled job: it competes with live ingestion, so run it
        /// against a restored copy first. Resumable — the response returns resumeFromDate if
        /// the run is capped, cancelled or fails partway.
        /// </summary>
        [HttpPost(Router.AnalyticsRouting.Backfill)]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> RunBackfill(
            [FromQuery] DateTime from,
            [FromQuery] DateTime to,
            [FromQuery] bool force = false,
            CancellationToken cancellationToken = default)
        {
            if (from > to)
                return BadRequest(new { succeeded = false, message = "from must not be after to." });

            var result = await _backfill.BackfillAsync(from, to, force, cancellationToken);
            return Ok(new { succeeded = true, statusCode = 200, data = result });
        }

        /// <summary>
        /// AN-012: the metric registry. Every metric this API serves has a contract here with
        /// its business purpose, technical definition, formula, exclusions, limitations and
        /// validation method, plus a trust level.
        /// </summary>
        [HttpGet(Router.AnalyticsRouting.MetricsRegistry)]
        [Authorize(Roles = "Admin")]
        public IActionResult GetMetricsRegistry()
        {
            return Ok(new
            {
                succeeded = true,
                statusCode = 200,
                data = _metricRegistry.GetAllContracts().Select(c => new
                {
                    metricKey = c.MetricKey,
                    name = c.Name,
                    trustLevel = c.TrustLevel.ToString(),
                    businessPurpose = c.BusinessPurpose,
                    technicalDefinition = c.TechnicalDefinition,
                    formula = c.Formula,
                    exclusions = c.Exclusions,
                    limitations = c.Limitations,
                    dataAvailableFromUtc = c.DataAvailableFromUtc,
                    validationMethod = c.ValidationMethod
                })
            });
        }

        /// <summary>
        /// DEPRECATED (M-102-LEGACY, graded UNRELIABLE): matches activity on exactly day N over
        /// a caller-supplied signal defaulting to the cookie-derived session_started. Use
        /// GET /Analytics/Return/Weekly instead. Retained only until the dashboard cuts over.
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
