using Cocorra.BLL.Base;
using Cocorra.BLL.Services.Analytics;
using Cocorra.BLL.Services.EventTracking;
using Cocorra.DAL.DTOS.AnalyticsDto;
using Cocorra.DAL.Repository.AnalyticsRepository;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Cocorra.BLL.Services.AnalyticsService
{
    public class AnalyticsService : ResponseHandler, IAnalyticsService
    {
        private readonly IAnalyticsRepository _analyticsRepository;
        private readonly IMemoryCache _cache;
        private readonly IMetricRegistry _metricRegistry;

        // SemaphoreSlim guards against cache stampede:
        // if the cache expires and N admins refresh the dashboard simultaneously,
        // only ONE EF query will execute; the rest wait for the result.
        private static readonly SemaphoreSlim _summaryLock = new(1, 1);
        private static readonly SemaphoreSlim _userGrowthLock = new(1, 1);
        private static readonly SemaphoreSlim _roomLock = new(1, 1);
        private static readonly SemaphoreSlim _participationLock = new(1, 1);
        private static readonly SemaphoreSlim _reportLock = new(1, 1);
        private static readonly SemaphoreSlim _funnelLock = new(1, 1);
        private static readonly SemaphoreSlim _retentionLock = new(1, 1);
        private static readonly SemaphoreSlim _activeRoomsLock = new(1, 1);
        private static readonly SemaphoreSlim _peakHoursLock = new(1, 1);
        private static readonly SemaphoreSlim _voiceDropOffLock = new(1, 1);
        private static readonly SemaphoreSlim _activePassiveLock = new(1, 1);

        // Cache TTL: 10 minutes — balances freshness vs. DB load.
        private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

        /// <summary>
        /// AN-036. Minutes to add to any UTC hour in a response to reach the audience's local
        /// hour. Read from configuration once, with the shared default as the fallback.
        /// </summary>
        private readonly int _displayOffsetMinutes;

        /// <summary>
        /// <paramref name="options"/> is optional so existing construction sites — including
        /// the unit tests — keep working unchanged. A missing configuration section yields the
        /// documented default rather than a zero offset, because silently falling back to UTC
        /// is the failure this option exists to prevent.
        /// </summary>
        public AnalyticsService(
            IAnalyticsRepository analyticsRepository,
            IMemoryCache cache,
            IMetricRegistry metricRegistry,
            IOptions<EventTrackingOptions>? options = null)
        {
            _analyticsRepository = analyticsRepository;
            _cache = cache;
            _metricRegistry = metricRegistry;
            _displayOffsetMinutes = options?.Value.DisplayTimeZoneOffsetMinutes
                                    ?? AnalyticsDisplayDefaults.TimeZoneOffsetMinutes;
        }

        /// <summary>
        /// AN-012: builds the trust envelope carried on Response&lt;T&gt;.Meta.
        ///
        /// Response&lt;T&gt;.Meta already existed on every response and was always null, and
        /// ResponseHandler.Success already accepted it, so this is purely additive: a client
        /// that ignores Meta is unaffected. A client that reads it can finally tell a VERIFIED
        /// number from an UNRELIABLE one, which is the whole point of the exercise.
        /// </summary>
        private object BuildMeta(params string[] metricKeys)
        {
            var contracts = metricKeys
                .Select(_metricRegistry.GetContract)
                .Where(c => c is not null)
                .Select(c => new
                {
                    metricKey = c!.MetricKey,
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
                .ToList();

            return new
            {
                computedAtUtc = DateTime.UtcNow,
                // The weakest component governs: a composite is only as trustworthy as its
                // least trustworthy input, and rounding that up would defeat the purpose.
                trustLevel = contracts.Count == 0
                    ? MetricTrustLevel.Unreliable.ToString()
                    : contracts.Max(c => Enum.Parse<MetricTrustLevel>(c.trustLevel)).ToString(),
                metrics = contracts,
                // AN-036. Every timestamp and hour bucket in the payload is UTC. This is the
                // offset a client applies for display, and it is on the envelope rather than on
                // individual DTOs so a caller cannot receive an hour-of-day series without it.
                // Applying it server-side was rejected: the stored figure must stay unambiguous.
                display = new
                {
                    timeZone = "UTC",
                    suggestedDisplayOffsetMinutes = _displayOffsetMinutes
                }
            };
        }

        // ── Helpers ────────────────────────────────────────────────────────

        /// <summary>Resolves a nullable date; defaults to 30 days ago at midnight UTC.</summary>
        private static DateTime ResolveFrom(DateTime? from)
            => from?.ToUniversalTime() ?? DateTime.UtcNow.AddDays(-30).Date;

        /// <summary>Resolves a nullable date; defaults to end-of-today UTC.</summary>
        private static DateTime ResolveTo(DateTime? to)
            => to?.ToUniversalTime() ?? DateTime.UtcNow;

        /// <summary>
        /// Thread-safe cache-get-or-create using a SemaphoreSlim to
        /// prevent stampede when multiple callers hit an expired entry.
        /// </summary>
        private static async Task<T> GetOrCreateWithLockAsync<T>(
            IMemoryCache cache,
            string key,
            SemaphoreSlim semaphore,
            Func<Task<T>> factory)
        {
            if (cache.TryGetValue(key, out T? cached) && cached is not null)
                return cached;

            await semaphore.WaitAsync();
            try
            {
                // Double-check after acquiring the lock
                if (cache.TryGetValue(key, out cached) && cached is not null)
                    return cached;

                var value = await factory();
                cache.Set(key, value, CacheTtl);
                return value;
            }
            finally
            {
                semaphore.Release();
            }
        }

        // ── Public methods ──────────────────────────────────────────────────

        public async Task<Response<PlatformSummaryDto>> GetPlatformSummaryAsync(
            DateTime? from = null,
            DateTime? to = null)
        {
            var fromUtc = ResolveFrom(from);
            var toUtc = ResolveTo(to);
            var key = $"analytics:summary:{fromUtc:yyyyMMdd}:{toUtc:yyyyMMddHH}";

            var result = await GetOrCreateWithLockAsync(_cache, key, _summaryLock, async () =>
            {
                // Run all four queries concurrently — they hit different tables.
                var userTask = _analyticsRepository.GetUserGrowthAsync("monthly", fromUtc, toUtc);
                var roomTask = _analyticsRepository.GetRoomAnalyticsAsync(fromUtc, toUtc);
                var participationTask = _analyticsRepository.GetParticipationStatsAsync(fromUtc, toUtc);
                var reportTask = _analyticsRepository.GetReportInsightsAsync(fromUtc, toUtc);

                await Task.WhenAll(userTask, roomTask, participationTask, reportTask);

                return new PlatformSummaryDto
                {
                    Users = userTask.Result,
                    Rooms = roomTask.Result,
                    Participation = participationTask.Result,
                    Reports = reportTask.Result,
                    GeneratedAt = DateTime.UtcNow
                };
            });

            return Success(result, BuildMeta(MetricRegistry.PlatformSummary, MetricRegistry.UserRegistrations, MetricRegistry.RoomAnalytics, MetricRegistry.RoomParticipation, MetricRegistry.ReportRate));
        }

        public async Task<Response<UserGrowthDto>> GetUserGrowthAsync(
            string granularity = "monthly",
            DateTime? from = null,
            DateTime? to = null)
        {
            // Validate granularity
            if (!new[] { "daily", "monthly" }.Contains(granularity.ToLower()))
                return BadRequest<UserGrowthDto>("granularity must be 'daily' or 'monthly'.");

            var fromUtc = ResolveFrom(from);
            var toUtc = ResolveTo(to);
            var key = $"analytics:users:{granularity.ToLower()}:{fromUtc:yyyyMMdd}:{toUtc:yyyyMMddHH}";

            var result = await GetOrCreateWithLockAsync(_cache, key, _userGrowthLock, () =>
                _analyticsRepository.GetUserGrowthAsync(granularity, fromUtc, toUtc));

            return Success(result, BuildMeta(MetricRegistry.UserRegistrations, MetricRegistry.UserStatusHistory));
        }

        public async Task<Response<RoomAnalyticsDto>> GetRoomAnalyticsAsync(
            DateTime? from = null,
            DateTime? to = null,
            int topN = 10)
        {
            var fromUtc = ResolveFrom(from);
            var toUtc = ResolveTo(to);
            var key = $"analytics:rooms:{fromUtc:yyyyMMdd}:{toUtc:yyyyMMddHH}:{topN}";

            var result = await GetOrCreateWithLockAsync(_cache, key, _roomLock, () =>
                _analyticsRepository.GetRoomAnalyticsAsync(fromUtc, toUtc, topN));

            // M-205, not M-200: this payload counts rooms and carries no host figure. M-200 is
            // Distinct Active Hosts and belongs to /Analytics/Supply/Health.
            return Success(result, BuildMeta(MetricRegistry.RoomAnalytics, MetricRegistry.RoomsGoneLive));
        }

        private static readonly SemaphoreSlim _socialLock = new(1, 1);
        private static readonly SemaphoreSlim _mbtiLock = new(1, 1);
        private static readonly SemaphoreSlim _cohortGridLock = new(1, 1);

        public async Task<Response<SocialGraphDto>> GetSocialGraphAsync(DateTime? from = null, DateTime? to = null)
        {
            var fromUtc = ResolveFrom(from);
            var toUtc = ResolveTo(to);
            var key = $"analytics:social:{fromUtc:yyyyMMdd}:{toUtc:yyyyMMddHH}";

            var result = await GetOrCreateWithLockAsync(_cache, key, _socialLock, () =>
                _analyticsRepository.GetSocialGraphAsync(fromUtc, toUtc));

            return Success(result, BuildMeta(MetricRegistry.SocialGraph));
        }

        public async Task<Response<MbtiAnalysisDto>> GetMbtiAnalysisAsync(DateTime? from = null, DateTime? to = null)
        {
            var fromUtc = ResolveFrom(from);
            var toUtc = ResolveTo(to);
            var key = $"analytics:mbti:{fromUtc:yyyyMMdd}:{toUtc:yyyyMMddHH}";

            var result = await GetOrCreateWithLockAsync(_cache, key, _mbtiLock, () =>
                _analyticsRepository.GetMbtiDichotomyAnalysisAsync(fromUtc, toUtc));

            return Success(result, BuildMeta(MetricRegistry.MbtiSpeakingAssociation));
        }

        public async Task<Response<CohortGridDto>> GetCohortGridAsync(DateTime? from = null, DateTime? to = null)
        {
            // A cohort grid over 30 days would be four columns wide and say nothing, so the
            // default window is deliberately long.
            var fromUtc = from?.ToUniversalTime() ?? DateTime.UtcNow.AddDays(-120).Date;
            var toUtc = ResolveTo(to);
            var key = $"analytics:cohortGrid:{fromUtc:yyyyMMdd}:{toUtc:yyyyMMddHH}";

            var result = await GetOrCreateWithLockAsync(_cache, key, _cohortGridLock, () =>
                _analyticsRepository.GetCohortGridAsync(fromUtc, toUtc));

            return Success(result, BuildMeta(MetricRegistry.CohortGrid));
        }

        private static readonly SemaphoreSlim _supplyHealthLock = new(1, 1);
        private static readonly SemaphoreSlim _reportRateLock = new(1, 1);
        private static readonly SemaphoreSlim _reviewLatencyLock = new(1, 1);
        private static readonly SemaphoreSlim _supportLock = new(1, 1);

        public async Task<Response<SupplyHealthDto>> GetSupplyHealthAsync(
            string granularity = "monthly",
            DateTime? from = null,
            DateTime? to = null)
        {
            if (!new[] { "daily", "monthly" }.Contains(granularity.ToLower()))
                return BadRequest<SupplyHealthDto>("granularity must be 'daily' or 'monthly'.");

            // Supply trends need a longer default window than 30 days: host retention is only
            // visible once a host has had time to come back.
            var fromUtc = from?.ToUniversalTime() ?? DateTime.UtcNow.AddDays(-180).Date;
            var toUtc = ResolveTo(to);
            var key = $"analytics:supply:{granularity.ToLower()}:{fromUtc:yyyyMMdd}:{toUtc:yyyyMMddHH}";

            var result = await GetOrCreateWithLockAsync(_cache, key, _supplyHealthLock, () =>
                _analyticsRepository.GetSupplyHealthAsync(granularity, fromUtc, toUtc));

            return Success(result, BuildMeta(MetricRegistry.ActiveHosts, MetricRegistry.HostRetention, MetricRegistry.HostConcentration));
        }

        public async Task<Response<ReportRateInsightsDto>> GetReportRateByCategoryAsync(
            DateTime? from = null,
            DateTime? to = null)
        {
            var fromUtc = ResolveFrom(from);
            var toUtc = ResolveTo(to);
            var key = $"analytics:reportRate:{fromUtc:yyyyMMdd}:{toUtc:yyyyMMddHH}";

            var result = await GetOrCreateWithLockAsync(_cache, key, _reportRateLock, () =>
                _analyticsRepository.GetReportRateByCategoryAsync(fromUtc, toUtc));

            return Success(result, BuildMeta(MetricRegistry.ReportRateByCategory, MetricRegistry.ReportRate));
        }

        public async Task<Response<ReviewLatencyDto>> GetReviewLatencyAsync(
            DateTime? from = null,
            DateTime? to = null)
        {
            var fromUtc = ResolveFrom(from);
            var toUtc = ResolveTo(to);
            var key = $"analytics:reviewLatency:{fromUtc:yyyyMMdd}:{toUtc:yyyyMMddHH}";

            var result = await GetOrCreateWithLockAsync(_cache, key, _reviewLatencyLock, () =>
                _analyticsRepository.GetReviewLatencyAsync(fromUtc, toUtc));

            return Success(result, BuildMeta(MetricRegistry.ReviewLatency, MetricRegistry.OpenReportBacklog));
        }

        public async Task<Response<SupportAnalyticsDto>> GetSupportAnalyticsAsync(
            DateTime? from = null,
            DateTime? to = null)
        {
            var fromUtc = ResolveFrom(from);
            var toUtc = ResolveTo(to);
            var key = $"analytics:support:{fromUtc:yyyyMMdd}:{toUtc:yyyyMMddHH}";

            var result = await GetOrCreateWithLockAsync(_cache, key, _supportLock, () =>
                _analyticsRepository.GetSupportAnalyticsAsync(fromUtc, toUtc));

            return Success(result, BuildMeta(MetricRegistry.SupportVolume));
        }

        private static readonly SemaphoreSlim _platformHealthLock = new(1, 1);

        /// <summary>
        /// A-1 — Platform Health.
        ///
        /// Defaults to a rolling 7 days rather than the service-wide 30, because the north star
        /// M-100 is defined over a 7-day window. Serving it over 30 days by default would return
        /// a figure that is not the metric its contract describes.
        /// </summary>
        public async Task<Response<PlatformHealthDto>> GetPlatformHealthAsync(
            DateTime? from = null,
            DateTime? to = null,
            string? compareTo = "previous_period")
        {
            var toUtc = ResolveTo(to);
            var fromUtc = from?.ToUniversalTime() ?? toUtc.Date.AddDays(-6);

            if (fromUtc > toUtc)
            {
                return BadRequest<PlatformHealthDto>("'from' must be earlier than or equal to 'to'.");
            }

            // Only one comparison mode is supported. An unrecognised value is rejected rather
            // than silently ignored: a caller asking for compareTo=previous_year and receiving a
            // previous-week comparison without complaint would draw wrong conclusions from a
            // response that looked correct.
            bool compare;
            if (string.IsNullOrWhiteSpace(compareTo) || compareTo.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                compare = false;
            }
            else if (compareTo.Equals("previous_period", StringComparison.OrdinalIgnoreCase))
            {
                compare = true;
            }
            else
            {
                return BadRequest<PlatformHealthDto>(
                    "'compareTo' must be 'previous_period' or 'none'.");
            }

            var key = $"analytics:platformhealth:{fromUtc:yyyyMMdd}:{toUtc:yyyyMMddHH}:{compare}";

            var result = await GetOrCreateWithLockAsync(_cache, key, _platformHealthLock, () =>
                _analyticsRepository.GetPlatformHealthAsync(fromUtc, toUtc, compare));

            return Success(result, BuildMeta(
                MetricRegistry.WeeklyParticipatingUsers,
                MetricRegistry.SpeakingConversion,
                MetricRegistry.ActiveHosts,
                MetricRegistry.RoomsGoneLive,
                MetricRegistry.VoiceVerificationFunnel,
                MetricRegistry.UserRegistrations));
        }

        private static readonly SemaphoreSlim _stageFunnelLock = new(1, 1);

        /// <summary>
        /// AN-027 / M-400 — stage participation funnel.
        ///
        /// The date range is validated rather than silently swapped: a caller asking for a
        /// backwards window has a bug, and returning an empty funnel would hide it behind a
        /// number that looks like "nobody participated".
        /// </summary>
        public async Task<Response<StageFunnelDto>> GetStageFunnelAsync(
            DateTime? from = null,
            DateTime? to = null)
        {
            var fromUtc = ResolveFrom(from);
            var toUtc = ResolveTo(to);

            if (fromUtc > toUtc)
            {
                return BadRequest<StageFunnelDto>("'from' must be earlier than or equal to 'to'.");
            }

            var key = $"analytics:stagefunnel:{fromUtc:yyyyMMddHH}:{toUtc:yyyyMMddHH}";

            var result = await GetOrCreateWithLockAsync(_cache, key, _stageFunnelLock, () =>
                _analyticsRepository.GetStageFunnelAsync(fromUtc, toUtc));

            return Success(result, BuildMeta(MetricRegistry.StageFunnel));
        }

        /// <summary>Default onboarding sequence for M-507.</summary>
        private static readonly string[] DefaultActivationSteps =
        {
            "user_registered",
            "voice_verification_submitted",
            "activation_completed",
            "room_joined",
            "mic_activated"
        };

        private static readonly SemaphoreSlim _activationFunnelLock = new(1, 1);
        private static readonly SemaphoreSlim _returnRateLock = new(1, 1);

        public async Task<Response<ActivationFunnelDto>> GetActivationFunnelAsync(
            string[]? steps = null,
            DateTime? from = null,
            DateTime? to = null)
        {
            var resolvedSteps = steps is { Length: > 0 } ? steps : DefaultActivationSteps;
            var fromUtc = ResolveFrom(from);
            var toUtc = ResolveTo(to);
            var key = $"analytics:activationFunnel:{string.Join("_", resolvedSteps)}:{fromUtc:yyyyMMdd}:{toUtc:yyyyMMddHH}";

            var result = await GetOrCreateWithLockAsync(_cache, key, _activationFunnelLock, () =>
                _analyticsRepository.GetActivationFunnelAsync(resolvedSteps, fromUtc, toUtc));

            return Success(result, BuildMeta(MetricRegistry.ActivationFunnel));
        }

        public async Task<Response<WeeklyReturnRateDto>> GetWeeklyReturnRateAsync(
            DateTime? from = null,
            DateTime? to = null)
        {
            // Return cohorts need more history than the 30-day default to be meaningful.
            var fromUtc = from?.ToUniversalTime() ?? DateTime.UtcNow.AddDays(-84).Date;
            var toUtc = ResolveTo(to);
            var key = $"analytics:returnRate:{fromUtc:yyyyMMdd}:{toUtc:yyyyMMddHH}";

            var result = await GetOrCreateWithLockAsync(_cache, key, _returnRateLock, () =>
                _analyticsRepository.GetWeeklyReturnRateAsync(fromUtc, toUtc));

            return Success(result, BuildMeta(MetricRegistry.WeeklyReturnRate));
        }

        public async Task<Response<ParticipationStatsDto>> GetParticipationStatsAsync(
            DateTime? from = null,
            DateTime? to = null)
        {
            var fromUtc = ResolveFrom(from);
            var toUtc = ResolveTo(to);
            var key = $"analytics:participation:{fromUtc:yyyyMMdd}:{toUtc:yyyyMMddHH}";

            var result = await GetOrCreateWithLockAsync(_cache, key, _participationLock, () =>
                _analyticsRepository.GetParticipationStatsAsync(fromUtc, toUtc));

            return Success(result, BuildMeta(MetricRegistry.RoomParticipation, MetricRegistry.SpeakingConversion));
        }

        public async Task<Response<ReportInsightsDto>> GetReportInsightsAsync(
            DateTime? from = null,
            DateTime? to = null,
            int topN = 10)
        {
            var fromUtc = ResolveFrom(from);
            var toUtc = ResolveTo(to);
            var key = $"analytics:reports:{fromUtc:yyyyMMdd}:{toUtc:yyyyMMddHH}:{topN}";

            var result = await GetOrCreateWithLockAsync(_cache, key, _reportLock, () =>
                _analyticsRepository.GetReportInsightsAsync(fromUtc, toUtc, topN));

            return Success(result, BuildMeta(MetricRegistry.ReportRate));
        }

        public async Task<Response<Dictionary<string, int>>> GetFunnelAsync(
            string[] steps,
            DateTime? from = null,
            DateTime? to = null)
        {
            if (steps == null || steps.Length == 0)
                return BadRequest<Dictionary<string, int>>("Funnel steps are required.");

            var fromUtc = ResolveFrom(from);
            var toUtc = ResolveTo(to);
            var stepsKey = string.Join("_", steps);
            var key = $"analytics:funnel:{stepsKey}:{fromUtc:yyyyMMdd}:{toUtc:yyyyMMddHH}";

            var result = await GetOrCreateWithLockAsync(_cache, key, _funnelLock, () =>
                _analyticsRepository.GetFunnelAsync(steps, fromUtc, toUtc));

            // M-507-LEGACY, not M-507. This route counts each step independently, so the result
            // can widen downward (D-5). Declaring the sequential contract here would have the
            // trust envelope certify the defect as fixed on the one route that still has it.
            return Success(result, BuildMeta(MetricRegistry.LegacyIndependentFunnel));
        }

        public async Task<Response<Dictionary<int, double>>> GetRetentionCohortAsync(
            string cohortEvent,
            string activeEvent,
            DateTime? cohortStart = null,
            DateTime? cohortEnd = null)
        {
            if (string.IsNullOrWhiteSpace(cohortEvent) || string.IsNullOrWhiteSpace(activeEvent))
                return BadRequest<Dictionary<int, double>>("cohortEvent and activeEvent are required.");

            var cohortStartUtc = ResolveFrom(cohortStart);
            var cohortEndUtc = ResolveTo(cohortEnd);
            var key = $"analytics:retention:{cohortEvent}:{activeEvent}:{cohortStartUtc:yyyyMMdd}:{cohortEndUtc:yyyyMMddHH}";

            var result = await GetOrCreateWithLockAsync(_cache, key, _retentionLock, () =>
                _analyticsRepository.GetRetentionCohortAsync(cohortEvent, activeEvent, cohortStartUtc, cohortEndUtc));

            return Success(result, BuildMeta(MetricRegistry.LegacyRetentionCohort));
        }

        public async Task<Response<List<TopActiveRoomDto>>> GetMostActiveRoomsAsync(
            DateTime? from = null,
            DateTime? to = null,
            int topN = 10)
        {
            var fromUtc = ResolveFrom(from);
            var toUtc = ResolveTo(to);
            var key = $"analytics:activerooms:{fromUtc:yyyyMMdd}:{toUtc:yyyyMMddHH}:{topN}";

            var result = await GetOrCreateWithLockAsync(_cache, key, _activeRoomsLock, () =>
                _analyticsRepository.GetMostActiveRoomsAsync(fromUtc, toUtc, topN));

            return Success(result, BuildMeta(MetricRegistry.MostActiveRooms));
        }

        public async Task<Response<List<HourlyActivityDto>>> GetPeakActiveHoursAsync(
            DateTime? from = null,
            DateTime? to = null)
        {
            var fromUtc = ResolveFrom(from);
            var toUtc = ResolveTo(to);
            var key = $"analytics:peakhours:{fromUtc:yyyyMMdd}:{toUtc:yyyyMMddHH}";

            var result = await GetOrCreateWithLockAsync(_cache, key, _peakHoursLock, () =>
                _analyticsRepository.GetPeakActiveHoursAsync(fromUtc, toUtc));

            return Success(result, BuildMeta(MetricRegistry.PeakActiveHours));
        }

        public async Task<Response<VoiceVerificationFunnelDto>> GetVoiceVerificationDropOffAsync(
            DateTime? from = null,
            DateTime? to = null)
        {
            var fromUtc = ResolveFrom(from);
            var toUtc = ResolveTo(to);
            var key = $"analytics:voicedropoff:{fromUtc:yyyyMMdd}:{toUtc:yyyyMMddHH}";

            var result = await GetOrCreateWithLockAsync(_cache, key, _voiceDropOffLock, () =>
                _analyticsRepository.GetVoiceVerificationDropOffAsync(fromUtc, toUtc));

            return Success(result, BuildMeta(MetricRegistry.VoiceVerificationFunnel));
        }

        public async Task<Response<ParticipationModeDto>> GetActiveVsPassiveRateAsync(
            DateTime? from = null,
            DateTime? to = null)
        {
            var fromUtc = ResolveFrom(from);
            var toUtc = ResolveTo(to);
            var key = $"analytics:activepassive:{fromUtc:yyyyMMdd}:{toUtc:yyyyMMddHH}";

            var result = await GetOrCreateWithLockAsync(_cache, key, _activePassiveLock, () =>
                _analyticsRepository.GetActiveVsPassiveRateAsync(fromUtc, toUtc));

            return Success(result, BuildMeta(MetricRegistry.SpeakingConversion, MetricRegistry.RoomParticipation));
        }
    }
}
