using Cocorra.BLL.Base;
using Cocorra.DAL.DTOS.AnalyticsDto;
using Cocorra.DAL.Repository.AnalyticsRepository;
using Microsoft.Extensions.Caching.Memory;

namespace Cocorra.BLL.Services.AnalyticsService
{
    public class AnalyticsService : ResponseHandler, IAnalyticsService
    {
        private readonly IAnalyticsRepository _analyticsRepository;
        private readonly IMemoryCache _cache;

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

        public AnalyticsService(
            IAnalyticsRepository analyticsRepository,
            IMemoryCache cache)
        {
            _analyticsRepository = analyticsRepository;
            _cache = cache;
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

            return Success(result);
        }

        public async Task<Response<UserGrowthDto>> GetUserGrowthAsync(
            string granularity = "monthly",
            DateTime? from = null,
            DateTime? to = null,
            int topN = 10)
        {
            // Validate granularity
            if (!new[] { "daily", "monthly" }.Contains(granularity.ToLower()))
                return BadRequest<UserGrowthDto>("granularity must be 'daily' or 'monthly'.");

            var fromUtc = ResolveFrom(from);
            var toUtc = ResolveTo(to);
            var key = $"analytics:users:{granularity.ToLower()}:{fromUtc:yyyyMMdd}:{toUtc:yyyyMMddHH}:{topN}";

            var result = await GetOrCreateWithLockAsync(_cache, key, _userGrowthLock, () =>
                _analyticsRepository.GetUserGrowthAsync(granularity, fromUtc, toUtc, topN));

            return Success(result);
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

            return Success(result);
        }

        public async Task<Response<ParticipationStatsDto>> GetParticipationStatsAsync(
            DateTime? from = null,
            DateTime? to = null,
            int topN = 10)
        {
            var fromUtc = ResolveFrom(from);
            var toUtc = ResolveTo(to);
            var key = $"analytics:participation:{fromUtc:yyyyMMdd}:{toUtc:yyyyMMddHH}:{topN}";

            var result = await GetOrCreateWithLockAsync(_cache, key, _participationLock, () =>
                _analyticsRepository.GetParticipationStatsAsync(fromUtc, toUtc, topN));

            return Success(result);
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

            return Success(result);
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

            return Success(result);
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

            return Success(result);
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

            return Success(result);
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

            return Success(result);
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

            return Success(result);
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

            return Success(result);
        }
    }
}
