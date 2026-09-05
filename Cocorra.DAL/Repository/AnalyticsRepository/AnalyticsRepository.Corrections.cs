using Cocorra.DAL.DTOS.AnalyticsDto;
using Cocorra.DAL.Models;
using Microsoft.EntityFrameworkCore;

namespace Cocorra.DAL.Repository.AnalyticsRepository
{
    /// <summary>
    /// AN-006 and AN-007: the replacement metrics, kept in their own partial so the legacy
    /// endpoints they supersede stay readable and reviewable side by side until cutover.
    /// </summary>
    public partial class AnalyticsRepository
    {
        // ─────────────────────────────────────────────────────────────────────
        // AN-006 / M-102 — Weekly Return Rate
        //
        // This is a replacement, not a repair of the legacy cohort metric. Changing the
        // legacy `== day` comparison to `>= day` while leaving session_started as the signal
        // would have produced a plausible number resting on a cookie-derived input that was
        // never validated on the Flutter client. The signal here is room_joined: server
        // emitted, indexed, cookie independent, and untouched by any of D-1..D-5.
        // ─────────────────────────────────────────────────────────────────────
        public async Task<WeeklyReturnRateDto> GetWeeklyReturnRateAsync(
            DateTime fromUtc,
            DateTime toUtc)
        {
            // Host exclusion is by column where RoomId is present; a join event with no room
            // context cannot be attributed to a host and is kept rather than silently dropped.
            var joins = await _context.UserEvents
                .AsNoTracking()
                .Where(e => e.EventType == EventTypes.RoomJoined
                         && e.UserId != null
                         && e.OccurredAtUtc >= fromUtc
                         && e.OccurredAtUtc <= toUtc)
                .Where(e => e.RoomId == null
                         || !_context.Rooms.Any(r => r.Id == e.RoomId && r.HostId == e.UserId))
                .Select(e => new { UserId = e.UserId!.Value, e.OccurredAtUtc })
                .ToListAsync();

            if (joins.Count == 0)
            {
                return new WeeklyReturnRateDto { From = fromUtc, To = toUtc };
            }

            var dataAvailableFrom = joins.Min(j => j.OccurredAtUtc);

            // Per user, which weeks did they join in at all.
            var weeksByUser = joins
                .GroupBy(j => j.UserId)
                .ToDictionary(g => g.Key, g => g.Select(j => StartOfWeekUtc(j.OccurredAtUtc)).Distinct().ToHashSet());

            var allWeeks = joins
                .Select(j => StartOfWeekUtc(j.OccurredAtUtc))
                .Distinct()
                .OrderBy(w => w)
                .ToList();

            var latestWeek = allWeeks.Last();
            var cohorts = new List<WeeklyReturnCohortDto>();

            foreach (var week in allWeeks)
            {
                var cohort = weeksByUser
                    .Where(kv => kv.Value.Contains(week))
                    .Select(kv => kv.Key)
                    .ToList();

                if (cohort.Count == 0)
                {
                    continue;
                }

                var returned = cohort.Count(userId => weeksByUser[userId].Any(w => w > week));

                cohorts.Add(new WeeklyReturnCohortDto
                {
                    WeekStartUtc = week,
                    CohortSize = cohort.Count,
                    ReturnedInLaterWeek = returned,
                    ReturnRatePercent = Math.Round((double)returned / cohort.Count * 100, 2),
                    // The final week has no later week inside the window yet, so its rate is
                    // structurally 0 and must be labelled incomplete rather than charted.
                    IsComplete = week < latestWeek
                });
            }

            return new WeeklyReturnRateDto
            {
                From = fromUtc,
                To = toUtc,
                Cohorts = cohorts,
                DataAvailableFromUtc = dataAvailableFrom
            };
        }

        // ─────────────────────────────────────────────────────────────────────
        // AN-007 / M-507 — Sequential funnel with per-step elapsed time
        // ─────────────────────────────────────────────────────────────────────
        public async Task<ActivationFunnelDto> GetActivationFunnelAsync(
            string[] steps,
            DateTime fromUtc,
            DateTime toUtc)
        {
            if (steps is null || steps.Length == 0)
            {
                return new ActivationFunnelDto { From = fromUtc, To = toUtc };
            }

            var stepEvents = await _context.UserEvents
                .AsNoTracking()
                .Where(e => steps.Contains(e.EventType)
                         && e.OccurredAtUtc >= fromUtc
                         && e.OccurredAtUtc <= toUtc
                         && e.UserId != null)
                .GroupBy(e => new { UserId = e.UserId!.Value, e.EventType })
                .Select(g => new
                {
                    g.Key.UserId,
                    g.Key.EventType,
                    FirstOccurredAtUtc = g.Min(x => x.OccurredAtUtc)
                })
                .ToListAsync();

            if (stepEvents.Count == 0)
            {
                return new ActivationFunnelDto { From = fromUtc, To = toUtc };
            }

            var firstTimeByUser = stepEvents
                .GroupBy(e => e.UserId)
                .ToDictionary(g => g.Key, g => g.ToDictionary(x => x.EventType, x => x.FirstOccurredAtUtc));

            var result = new List<FunnelStepDto>();
            var qualified = new HashSet<Guid>(firstTimeByUser.Keys);
            var previousStepTime = new Dictionary<Guid, DateTime>();
            var firstStepCount = 0;
            var previousStepCount = 0;

            for (var i = 0; i < steps.Length; i++)
            {
                var stepName = steps[i];
                var nowQualified = new HashSet<Guid>();
                var gaps = new List<double>();

                foreach (var userId in qualified)
                {
                    if (!firstTimeByUser[userId].TryGetValue(stepName, out var stepTime))
                    {
                        continue;
                    }

                    if (i == 0)
                    {
                        nowQualified.Add(userId);
                        previousStepTime[userId] = stepTime;
                        continue;
                    }

                    // A step only counts if it happened at or after the previous step. Without
                    // this the funnel can widen downward, which is what D-5 was.
                    if (previousStepTime.TryGetValue(userId, out var prevTime) && stepTime >= prevTime)
                    {
                        nowQualified.Add(userId);
                        gaps.Add((stepTime - prevTime).TotalSeconds);
                        previousStepTime[userId] = stepTime;
                    }
                }

                qualified = nowQualified;

                if (i == 0)
                {
                    firstStepCount = qualified.Count;
                }

                result.Add(new FunnelStepDto
                {
                    Step = stepName,
                    Count = qualified.Count,
                    ConversionFromFirstStepPercent = firstStepCount > 0
                        ? Math.Round((double)qualified.Count / firstStepCount * 100, 2)
                        : 0,
                    ConversionFromPreviousStepPercent = i == 0
                        ? 100
                        : previousStepCount > 0
                            ? Math.Round((double)qualified.Count / previousStepCount * 100, 2)
                            : 0,
                    MedianSecondsFromPreviousStep = Percentile(gaps, 0.50),
                    P90SecondsFromPreviousStep = Percentile(gaps, 0.90)
                });

                previousStepCount = qualified.Count;
            }

            // Monotonicity is structural above, because `qualified` only ever shrinks. The
            // assertion stays as a guard: if a future edit breaks it, the caller should get an
            // error rather than a chart that reads as growth through the funnel.
            for (var i = 1; i < result.Count; i++)
            {
                if (result[i].Count > result[i - 1].Count)
                {
                    throw new InvalidOperationException(
                        $"Funnel monotonicity violated: step '{result[i].Step}' ({result[i].Count}) " +
                        $"exceeds '{result[i - 1].Step}' ({result[i - 1].Count}).");
                }
            }

            return new ActivationFunnelDto
            {
                From = fromUtc,
                To = toUtc,
                Steps = result,
                DataAvailableFromUtc = stepEvents.Min(e => e.FirstOccurredAtUtc)
            };
        }

        /// <summary>Monday 00:00 UTC of the week containing <paramref name="value"/>.</summary>
        private static DateTime StartOfWeekUtc(DateTime value)
        {
            var date = value.Date;
            var offset = ((int)date.DayOfWeek + 6) % 7; // Monday = 0
            return date.AddDays(-offset);
        }

        /// <summary>
        /// Nearest-rank percentile. Returns null for an empty sample rather than 0, so
        /// "no user completed this transition" cannot be read as "it took no time".
        /// </summary>
        private static double? Percentile(List<double> values, double percentile)
        {
            if (values.Count == 0)
            {
                return null;
            }

            var sorted = values.OrderBy(v => v).ToList();

            if (sorted.Count == 1)
            {
                return Math.Round(sorted[0], 2);
            }

            var rank = percentile * (sorted.Count - 1);
            var lower = (int)Math.Floor(rank);
            var upper = (int)Math.Ceiling(rank);
            var weight = rank - lower;

            return Math.Round(sorted[lower] * (1 - weight) + sorted[upper] * weight, 2);
        }
    }
}
