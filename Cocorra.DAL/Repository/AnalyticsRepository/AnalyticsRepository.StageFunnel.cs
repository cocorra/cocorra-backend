using Cocorra.DAL.DTOS.AnalyticsDto;
using Cocorra.DAL.Models;
using Microsoft.EntityFrameworkCore;

namespace Cocorra.DAL.Repository.AnalyticsRepository
{
    /// <summary>
    /// AN-027 / M-400 — the stage participation funnel.
    ///
    /// The four steps are not a design choice made here; they are the four points in Cocorra's
    /// actual room flow that emit an event, in the order a participant passes through them:
    ///
    ///     JoinRoom          → room_joined      (RoomHub, always on)
    ///     RaiseHand         → hand_raised      (RoomHub, Analytics:EnableHighFrequencyEvents)
    ///     ApproveToStage    → stage_promoted   (RoomHub, Analytics:EnableNewEventEmission)
    ///     ToggleMic(unmute) → mic_activated    (RoomHub, always on)
    ///
    /// Two of the four are behind flags that default to off. That is the reason this funnel
    /// reports instrumentation state per step rather than assuming all four are present: with
    /// the flags off it must say "not measured" twice, not "zero" twice.
    /// </summary>
    public partial class AnalyticsRepository
    {
        /// <summary>
        /// The funnel in flow order. Changing this array changes the metric, so M-400's contract
        /// in <c>MetricRegistry</c> must be updated in the same commit.
        /// </summary>
        private static readonly (string EventType, string Step)[] StageFunnelSteps =
        [
            (EventTypes.RoomJoined,    "Joined room"),
            (EventTypes.HandRaised,    "Raised hand"),
            (EventTypes.StagePromoted, "Promoted to stage"),
            (EventTypes.MicActivated,  "Activated microphone")
        ];

        public async Task<StageFunnelDto> GetStageFunnelAsync(DateTime fromUtc, DateTime toUtc)
        {
            var stepEventTypes = StageFunnelSteps.Select(s => s.EventType).ToArray();

            // ── 1. Instrumentation probe ──────────────────────────────────────
            //
            // Deliberately NOT bounded by the requested window. The question this answers is
            // "does this event type exist at all", and asking it only inside the window would
            // classify a quiet week as an uninstrumented one. Bounded above by toUtc so that
            // querying a historical range does not borrow instrumentation that only started
            // afterwards.
            var firstSeen = await _context.UserEvents
                .AsNoTracking()
                .Where(e => stepEventTypes.Contains(e.EventType) && e.OccurredAtUtc <= toUtc)
                .GroupBy(e => e.EventType)
                .Select(g => new { EventType = g.Key, FirstEverUtc = g.Min(x => x.OccurredAtUtc) })
                .ToListAsync();

            var firstSeenByType = firstSeen.ToDictionary(x => x.EventType, x => x.FirstEverUtc);

            // ── 2. Window events, non-host, room-scoped ───────────────────────
            //
            // RoomId is required here, unlike the activation funnel: this funnel's unit is a
            // participation in a specific room, so an event with no room context cannot be
            // placed in any journey. Host exclusion is by join against Rooms.HostId — a host is
            // on stage by construction and would complete every step spuriously.
            var stepEvents = await _context.UserEvents
                .AsNoTracking()
                .Where(e => stepEventTypes.Contains(e.EventType)
                         && e.UserId != null
                         && e.RoomId != null
                         && e.OccurredAtUtc >= fromUtc
                         && e.OccurredAtUtc <= toUtc)
                .Where(e => !_context.Rooms.Any(r => r.Id == e.RoomId && r.HostId == e.UserId))
                .GroupBy(e => new { RoomId = e.RoomId!.Value, UserId = e.UserId!.Value, e.EventType })
                .Select(g => new
                {
                    g.Key.RoomId,
                    g.Key.UserId,
                    g.Key.EventType,
                    // MIN collapses duplicates and repeats to one first occurrence: the funnel
                    // asks whether a participant reached a step, not how many times.
                    FirstOccurredAtUtc = g.Min(x => x.OccurredAtUtc),
                    Occurrences = g.Count()
                })
                .ToListAsync();

            var result = new StageFunnelDto
            {
                From = fromUtc,
                To = toUtc,
                DataAvailableFromUtc = stepEvents.Count == 0
                    ? null
                    : stepEvents.Min(e => e.FirstOccurredAtUtc)
            };

            // Per (room, participant): the first time each step happened.
            var journeys = stepEvents
                .GroupBy(e => (e.RoomId, e.UserId))
                .ToDictionary(
                    g => g.Key,
                    g => g.ToDictionary(x => x.EventType, x => x.FirstOccurredAtUtc));

            result.RoomsInScope = stepEvents.Select(e => e.RoomId).Distinct().Count();

            var totalEventsByType = stepEvents
                .GroupBy(e => e.EventType)
                .ToDictionary(g => g.Key, g => g.Sum(x => x.Occurrences));

            var observedByType = stepEvents
                .GroupBy(e => e.EventType)
                .ToDictionary(g => g.Key, g => g.Count());

            // ── 3. Walk the funnel ────────────────────────────────────────────
            //
            // `qualified` only ever shrinks, which is what makes monotonicity structural rather
            // than asserted. Once a step is uninstrumented every later step is unknowable in
            // sequence terms, so `sequenceBroken` latches and downstream counts become null
            // instead of collapsing to a fabricated zero.
            var steps = new List<StageFunnelStepDto>();
            var qualified = new HashSet<(Guid RoomId, Guid UserId)>(journeys.Keys);
            var previousStepTime = new Dictionary<(Guid RoomId, Guid UserId), DateTime>();
            var sequenceBroken = false;
            string? brokenAtStep = null;
            int? firstStepCount = null;
            int? previousStepCount = null;

            for (var i = 0; i < StageFunnelSteps.Length; i++)
            {
                var (eventType, stepName) = StageFunnelSteps[i];
                var measured = firstSeenByType.TryGetValue(eventType, out var measurementStart);

                var dto = new StageFunnelStepDto
                {
                    Step = stepName,
                    EventType = eventType,
                    IsMeasured = measured,
                    MeasurementStartedUtc = measured ? measurementStart : null,
                    IsPartiallyMeasured = measured && measurementStart > fromUtc,
                    ObservedParticipations = measured ? observedByType.GetValueOrDefault(eventType) : null,
                    TotalEvents = measured ? totalEventsByType.GetValueOrDefault(eventType) : null
                };

                if (!measured)
                {
                    sequenceBroken = true;
                    brokenAtStep ??= stepName;
                    dto.NotMeasuredReason =
                        $"'{eventType}' has never been recorded. This step is not instrumented, " +
                        "so its count is unknown rather than zero.";
                }
                else if (sequenceBroken)
                {
                    dto.NotMeasuredReason =
                        $"Not computable: an earlier step ('{brokenAtStep}') is not instrumented, " +
                        "so progression through it cannot be established.";
                }

                if (sequenceBroken)
                {
                    // Everything from the break onwards is reported as unknown. ObservedParticipations
                    // above still carries whatever the step genuinely saw, so the reader is not left
                    // with nothing — only with nothing that claims to be a funnel.
                    steps.Add(dto);
                    continue;
                }

                var nowQualified = new HashSet<(Guid RoomId, Guid UserId)>();
                var gaps = new List<double>();

                foreach (var key in qualified)
                {
                    if (!journeys[key].TryGetValue(eventType, out var stepTime))
                    {
                        continue;
                    }

                    if (i == 0)
                    {
                        nowQualified.Add(key);
                        previousStepTime[key] = stepTime;
                        continue;
                    }

                    // A step only counts if it happened at or after the previous step. Without
                    // this, a mic_activated with no preceding stage_promoted would count at
                    // step 4 and the funnel could widen downward — defect D-5, one layer down.
                    if (previousStepTime.TryGetValue(key, out var prevTime) && stepTime >= prevTime)
                    {
                        nowQualified.Add(key);
                        gaps.Add((stepTime - prevTime).TotalSeconds);
                        previousStepTime[key] = stepTime;
                    }
                }

                qualified = nowQualified;
                var count = qualified.Count;

                firstStepCount ??= count;

                dto.Count = count;
                dto.DistinctUsers = qualified.Select(k => k.UserId).Distinct().Count();
                dto.ConversionFromFirstStepPercent = firstStepCount > 0
                    ? Math.Round((double)count / firstStepCount.Value * 100, 2)
                    : null;

                if (i == 0)
                {
                    dto.ConversionFromPreviousStepPercent = 100;
                    dto.DropOffFromPreviousStep = 0;
                    dto.DropOffFromPreviousStepPercent = 0;
                }
                else if (previousStepCount is > 0)
                {
                    dto.ConversionFromPreviousStepPercent =
                        Math.Round((double)count / previousStepCount.Value * 100, 2);
                    dto.DropOffFromPreviousStep = previousStepCount.Value - count;
                    dto.DropOffFromPreviousStepPercent =
                        Math.Round((double)(previousStepCount.Value - count) / previousStepCount.Value * 100, 2);
                }
                else
                {
                    // Nobody reached the previous step. A rate over an empty denominator is not
                    // 0%, it is undefined, and rendering it as 0% would invent a finding.
                    dto.ConversionFromPreviousStepPercent = null;
                    dto.DropOffFromPreviousStep = 0;
                    dto.DropOffFromPreviousStepPercent = null;
                }

                dto.MedianSecondsFromPreviousStep = i == 0 ? null : Percentile(gaps, 0.50);
                dto.P90SecondsFromPreviousStep = i == 0 ? null : Percentile(gaps, 0.90);

                previousStepCount = count;
                steps.Add(dto);
            }

            result.Steps = steps;
            result.IsFullyInstrumented = steps.All(s => s.IsMeasured && !s.IsPartiallyMeasured);

            // ── 4. Direct promotions ──────────────────────────────────────────
            //
            // A host can promote someone who never raised a hand. Strict sequencing drops those
            // participations at step 2, which without this line reads as promotions disappearing.
            // Only computable when both events are instrumented; null otherwise, for the same
            // reason every other unknown here is null.
            var handRaiseMeasured = firstSeenByType.ContainsKey(EventTypes.HandRaised);
            var promotionMeasured = firstSeenByType.ContainsKey(EventTypes.StagePromoted);

            result.DirectPromotionsWithoutHandRaise = handRaiseMeasured && promotionMeasured
                ? journeys.Count(j => j.Value.ContainsKey(EventTypes.StagePromoted)
                                   && !j.Value.ContainsKey(EventTypes.HandRaised))
                : null;

            // Monotonicity guard. Structural above, asserted here so a future edit surfaces as an
            // error rather than as a chart that reads as growth through a funnel.
            var counted = steps.Where(s => s.Count.HasValue).ToList();
            for (var i = 1; i < counted.Count; i++)
            {
                if (counted[i].Count > counted[i - 1].Count)
                {
                    throw new InvalidOperationException(
                        $"Stage funnel monotonicity violated: step '{counted[i].Step}' ({counted[i].Count}) " +
                        $"exceeds '{counted[i - 1].Step}' ({counted[i - 1].Count}).");
                }
            }

            return result;
        }
    }
}
