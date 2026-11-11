using System.Text.Json;
using Cocorra.DAL.DTOS.AnalyticsDto;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;
using Cocorra.DAL.Time;
using Microsoft.EntityFrameworkCore;

namespace Cocorra.DAL.Repository.AnalyticsRepository
{
    /// <summary>
    /// AN-008: User growth, split into two metrics with different trust levels.
    ///
    /// The registration count is sound and stays. The status breakdown was not: users were
    /// bucketed by CreatedAt but counted by their CURRENT Status, so every past bucket was
    /// repainted with today's outcomes and the distortion grew with bucket age — producing a
    /// false "our early users were worse" gradient. Status-at-time is now reconstructed from
    /// voice_verification_result events, and reports how far back it can actually speak.
    /// </summary>
    public partial class AnalyticsRepository
    {
        public async Task<UserGrowthDto> GetUserGrowthAsync(
            string granularity,
            DateTime from,
            DateTime to)
        {
            var isMonthly = granularity.Equals("monthly", StringComparison.OrdinalIgnoreCase);

            // ── Registrations per Cairo bucket (M-501) ──────────────────────
            // Counted per UTC hour in SQL so only hour rows, not user rows, leave the database.
            var hourlyRegistrations = await _context.Users
                .AsNoTracking()
                .Where(u => u.CreatedAt >= from && u.CreatedAt <= to)
                .GroupBy(u => new { u.CreatedAt.Date, u.CreatedAt.Hour })
                .Select(g => new UtcHourCount { Date = g.Key.Date, Hour = g.Key.Hour, Count = g.Count() })
                .ToListAsync();

            var registrationBuckets = hourlyRegistrations
                .GroupBy(h => isMonthly ? CairoTime.MonthOf(h.StartUtc) : CairoTime.DateOf(h.StartUtc))
                .Select(g => new { Bucket = g.Key, Count = g.Sum(h => h.Count) })
                .ToList();

            var dataPoints = registrationBuckets
                .OrderBy(b => b.Bucket)
                .Select(b => new UserGrowthDataPointDto
                {
                    Period = isMonthly ? b.Bucket.ToString("yyyy-MM") : b.Bucket.ToString("yyyy-MM-dd"),
                    NewUsers = b.Count
                })
                .ToList();

            var totalUsersInPeriod = registrationBuckets.Sum(b => b.Count);

            // ── Demographics of users registered in this window ─────────────
            var mbtiDist = await _context.Users
                .AsNoTracking()
                .Where(u => u.CreatedAt >= from && u.CreatedAt <= to && u.MBTI != null && u.MBTI != "")
                .GroupBy(u => u.MBTI!)
                .Select(g => new { Mbti = g.Key, Count = g.Count() })
                .OrderByDescending(g => g.Count)
                .ToListAsync();

            var avgAge = totalUsersInPeriod > 0
                ? await _context.Users
                    .AsNoTracking()
                    .Where(u => u.CreatedAt >= from && u.CreatedAt <= to)
                    .AverageAsync(u => (double)u.Age)
                : 0;

            // ── Status at time, reconstructed from events (M-502) ───────────
            var (statusAtTime, statusAvailableFrom) =
                await ReconstructStatusAtTimeAsync(from, to, isMonthly);

            return new UserGrowthDto
            {
                Granularity = granularity.ToLower(),
                From = from,
                To = to,
                TotalUsersInPeriod = totalUsersInPeriod,
                DataPoints = dataPoints,
                StatusAtTime = statusAtTime,
                StatusHistoryAvailableFromUtc = statusAvailableFrom,
                MbtiDistributionOfUsersRegisteredInWindow = mbtiDist.ToDictionary(m => m.Mbti, m => m.Count),
                AverageAgeOfUsersRegisteredInWindow = Math.Round(avgAge, 2)
            };
        }

        /// <summary>
        /// Rebuilds the verification-status distribution as it stood at the end of each bucket.
        /// For every user, the status at time t is the most recent voice_verification_result at
        /// or before t; a user with no such event yet is Pending.
        ///
        /// Returns an empty series when no status events exist rather than a series of zeros,
        /// so an uninstrumented period renders as a gap.
        /// </summary>
        private async Task<(List<StatusAtTimeDataPointDto> Series, DateTime? AvailableFrom)>
            ReconstructStatusAtTimeAsync(DateTime from, DateTime to, bool isMonthly)
        {
            var statusEvents = await _context.UserEvents
                .AsNoTracking()
                .Where(e => e.EventType == EventTypes.VoiceVerificationResult
                         && e.UserId != null
                         && e.OccurredAtUtc <= to)
                .OrderBy(e => e.OccurredAtUtc)
                .Select(e => new { UserId = e.UserId!.Value, e.OccurredAtUtc, e.PropertiesJson })
                .ToListAsync();

            if (statusEvents.Count == 0)
            {
                return (new List<StatusAtTimeDataPointDto>(), null);
            }

            var availableFrom = statusEvents[0].OccurredAtUtc;

            // Users registered on or before the window end are the population whose status we
            // can speak to. CreatedAt is relational and never purged.
            var registeredUsers = await _context.Users
                .AsNoTracking()
                .Where(u => u.CreatedAt <= to)
                .Select(u => new { u.Id, u.CreatedAt })
                .ToListAsync();

            var buckets = BuildBucketBoundaries(from, to, isMonthly);
            var series = new List<StatusAtTimeDataPointDto>();

            // Walk the buckets forward, folding in each bucket's events once. Latest-status-wins
            // within a bucket because statusEvents is ordered by time.
            var latestStatus = new Dictionary<Guid, UserStatus>();
            var eventIndex = 0;

            foreach (var (bucketLabelDate, bucketEnd) in buckets)
            {
                while (eventIndex < statusEvents.Count && statusEvents[eventIndex].OccurredAtUtc <= bucketEnd)
                {
                    var evt = statusEvents[eventIndex];
                    var parsed = ParseStatusFromProperties(evt.PropertiesJson);
                    if (parsed.HasValue)
                    {
                        latestStatus[evt.UserId] = parsed.Value;
                    }
                    eventIndex++;
                }

                // Only buckets at or after the first status event can be reconstructed. Earlier
                // buckets would report every user as Pending, which is an artefact of missing
                // instrumentation rather than a fact about the platform.
                if (bucketEnd < availableFrom)
                {
                    continue;
                }

                var existingUserCount = registeredUsers.Count(u => u.CreatedAt <= bucketEnd);
                var withStatus = latestStatus.Count;

                series.Add(new StatusAtTimeDataPointDto
                {
                    Period = isMonthly ? bucketLabelDate.ToString("yyyy-MM") : bucketLabelDate.ToString("yyyy-MM-dd"),
                    ActiveUsers = latestStatus.Count(kv => kv.Value == UserStatus.Active),
                    BannedUsers = latestStatus.Count(kv => kv.Value == UserStatus.Banned),
                    RejectedUsers = latestStatus.Count(kv => kv.Value == UserStatus.Rejected),
                    ReRecordUsers = latestStatus.Count(kv => kv.Value == UserStatus.ReRecord),
                    // A registered user with no status decision yet is Pending by definition.
                    PendingUsers = latestStatus.Count(kv => kv.Value == UserStatus.Pending)
                                   + Math.Max(0, existingUserCount - withStatus)
                });
            }

            return (series, availableFrom);
        }

        /// <summary>
        /// Cairo day or month buckets covering [from, to]. Each bucket is its Cairo calendar
        /// date, for the label, and the last UTC tick that still belongs to it.
        /// </summary>
        private static List<(DateTime LocalDate, DateTime EndUtc)> BuildBucketBoundaries(
            DateTime from,
            DateTime to,
            bool isMonthly)
        {
            var buckets = new List<(DateTime, DateTime)>();
            var cursor = isMonthly ? CairoTime.MonthOf(from) : CairoTime.DateOf(from);

            while (CairoTime.StartOfDayUtc(cursor) <= to)
            {
                var next = isMonthly ? cursor.AddMonths(1) : cursor.AddDays(1);
                buckets.Add((cursor, CairoTime.StartOfDayUtc(next).AddTicks(-1)));
                cursor = next;
            }

            return buckets;
        }

        /// <summary>
        /// Reads the status out of a voice_verification_result payload. AdminService emits it as
        /// <c>new { status = newStatus.ToString() }</c>. Returns null on anything unparseable
        /// rather than guessing — a mis-parsed status would silently move users between buckets.
        /// </summary>
        private static UserStatus? ParseStatusFromProperties(string? propertiesJson)
        {
            if (string.IsNullOrWhiteSpace(propertiesJson))
            {
                return null;
            }

            try
            {
                using var document = JsonDocument.Parse(propertiesJson);
                if (!document.RootElement.TryGetProperty("status", out var statusElement))
                {
                    return null;
                }

                var raw = statusElement.ValueKind == JsonValueKind.String
                    ? statusElement.GetString()
                    : statusElement.ToString();

                return Enum.TryParse<UserStatus>(raw, ignoreCase: true, out var status) ? status : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}
