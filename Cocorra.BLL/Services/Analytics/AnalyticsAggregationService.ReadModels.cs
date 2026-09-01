using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cocorra.DAL.Data;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;
using Cocorra.DAL.Models.Analytics;
using Microsoft.EntityFrameworkCore;

namespace Cocorra.BLL.Services.Analytics
{
    /// <summary>
    /// AN-014/AN-015: rollups for RM-2 (rooms), RM-3 (hosts) and RM-4 (funnel cohorts).
    ///
    /// Every grain is recomputed in full and UPSERTed on its natural key, so re-running a date
    /// is safe and produces identical rows. Counts are stored, never percentages: a weekly rate
    /// has to be computed from summed numerators and denominators, and averaging seven daily
    /// rates is wrong whenever daily volumes differ — which they do here, between a day with
    /// three live rooms and a day with none.
    /// </summary>
    public partial class AnalyticsAggregationService
    {
        /// <summary>
        /// RM-2 — per room, per day. Room dimensions are denormalised onto the row so a later
        /// category or capacity change cannot retroactively rewrite history.
        /// </summary>
        internal static async Task AggregateDailyRoomMetricsAsync(AppDbContext db, DateTime date, CancellationToken ct)
        {
            var nextDate = date.AddDays(1);

            var roomIds = await db.RoomParticipants
                .AsNoTracking()
                .Where(p => p.JoinedAt >= date && p.JoinedAt < nextDate)
                .Select(p => p.RoomId)
                .Distinct()
                .ToListAsync(ct);

            if (roomIds.Count == 0)
            {
                return;
            }

            var rooms = await db.Rooms
                .AsNoTracking()
                .Where(r => roomIds.Contains(r.Id))
                .Select(r => new
                {
                    r.Id,
                    r.HostId,
                    r.Category,
                    r.SelectionMode,
                    r.StageCapacity
                })
                .ToListAsync(ct);

            var participantStats = await db.RoomParticipants
                .AsNoTracking()
                .Where(p => p.JoinedAt >= date && p.JoinedAt < nextDate && p.UserId != p.Room!.HostId)
                .GroupBy(p => p.RoomId)
                .Select(g => new
                {
                    RoomId = g.Key,
                    DistinctJoiners = g.Select(x => x.UserId).Distinct().Count(),
                    TotalSpokenSeconds = g.Sum(x => x.TotalSpokenSeconds)
                })
                .ToListAsync(ct);

            var statsByRoom = participantStats.ToDictionary(s => s.RoomId, s => s);

            // Speakers come from mic_activated, host-excluded, for the same reason as M-101:
            // TotalSpokenSeconds accrues while a stage participant sits unmuted and idle.
            var speakerCounts = await db.UserEvents
                .AsNoTracking()
                .Where(e => e.EventType == EventTypes.MicActivated
                         && e.OccurredAtUtc >= date && e.OccurredAtUtc < nextDate
                         && e.RoomId != null && roomIds.Contains(e.RoomId.Value)
                         && e.UserId != null)
                .Where(e => !db.Rooms.Any(r => r.Id == e.RoomId && r.HostId == e.UserId))
                .GroupBy(e => e.RoomId!.Value)
                .Select(g => new { RoomId = g.Key, Speakers = g.Select(x => x.UserId).Distinct().Count() })
                .ToListAsync(ct);

            var speakersByRoom = speakerCounts.ToDictionary(s => s.RoomId, s => s.Speakers);

            var reportCounts = await db.Reports
                .AsNoTracking()
                .Where(r => r.ReportedRoomId != null && roomIds.Contains(r.ReportedRoomId.Value)
                         && r.CreatedAt >= date && r.CreatedAt < nextDate)
                .GroupBy(r => r.ReportedRoomId!.Value)
                .Select(g => new { RoomId = g.Key, Count = g.Count() })
                .ToListAsync(ct);

            var reportsByRoom = reportCounts.ToDictionary(r => r.RoomId, r => r.Count);

            var existingRows = await db.DailyRoomMetrics
                .Where(m => m.Date == date && roomIds.Contains(m.RoomId))
                .ToListAsync(ct);

            var existingByRoom = existingRows.ToDictionary(m => m.RoomId, m => m);

            foreach (var room in rooms)
            {
                statsByRoom.TryGetValue(room.Id, out var stats);

                var row = existingByRoom.TryGetValue(room.Id, out var found)
                    ? found
                    : new DailyRoomMetrics { Date = date, RoomId = room.Id };

                row.HostId = room.HostId;
                row.Category = room.Category.ToString();
                row.SelectionMode = room.SelectionMode.ToString();
                row.StageCapacity = room.StageCapacity;
                row.DistinctJoiners = stats?.DistinctJoiners ?? 0;
                row.DistinctSpeakers = speakersByRoom.TryGetValue(room.Id, out var spk) ? spk : 0;
                row.TotalSpokenSeconds = (int)Math.Min(int.MaxValue, stats?.TotalSpokenSeconds ?? 0);
                row.ReportsCount = reportsByRoom.TryGetValue(room.Id, out var rep) ? rep : 0;
                row.ComputedAtUtc = DateTime.UtcNow;

                // HandRaises and StagePromotions stay at 0 until AN-017/AN-018 emit the events.
                // They are NOT backfillable: the flags they would derive from are transient live
                // state that is reset on approval. A reader must treat these as not-measured,
                // which is why the metric contract carries that limitation explicitly.

                if (found is null)
                {
                    db.DailyRoomMetrics.Add(row);
                }
            }
        }

        /// <summary>
        /// RM-3 — per host, per day. Derived entirely from Rooms.HostId and Rooms.CreatedAt,
        /// which are relational and never purged, so this is the one read model that can be
        /// backfilled across the platform's whole history.
        /// </summary>
        internal static async Task AggregateDailyHostMetricsAsync(AppDbContext db, DateTime date, CancellationToken ct)
        {
            var nextDate = date.AddDays(1);

            var hostRows = await db.Rooms
                .AsNoTracking()
                .Where(r => r.CreatedAt >= date && r.CreatedAt < nextDate)
                .GroupBy(r => r.HostId)
                .Select(g => new
                {
                    HostId = g.Key,
                    RoomsCreated = g.Count(),
                    RoomsGoneLive = g.Count(r => r.Status != RoomStatus.Scheduled)
                })
                .ToListAsync(ct);

            if (hostRows.Count == 0)
            {
                return;
            }

            var hostIds = hostRows.Select(h => h.HostId).ToList();

            var joinerCounts = await db.RoomParticipants
                .AsNoTracking()
                .Where(p => p.Room!.CreatedAt >= date && p.Room.CreatedAt < nextDate
                         && hostIds.Contains(p.Room.HostId)
                         && p.UserId != p.Room!.HostId)
                .GroupBy(p => p.Room!.HostId)
                .Select(g => new { HostId = g.Key, Joiners = g.Select(x => x.UserId).Distinct().Count() })
                .ToListAsync(ct);

            var joinersByHost = joinerCounts.ToDictionary(j => j.HostId, j => j.Joiners);

            var reportCounts = await db.Reports
                .AsNoTracking()
                .Where(r => r.ReportedRoomId != null
                         && db.Rooms.Any(room => room.Id == r.ReportedRoomId
                                              && room.CreatedAt >= date && room.CreatedAt < nextDate
                                              && hostIds.Contains(room.HostId)))
                .Select(r => new
                {
                    HostId = db.Rooms.Where(room => room.Id == r.ReportedRoomId).Select(room => room.HostId).FirstOrDefault()
                })
                .ToListAsync(ct);

            var reportsByHost = reportCounts
                .GroupBy(r => r.HostId)
                .ToDictionary(g => g.Key, g => g.Count());

            var existingRows = await db.DailyHostMetrics
                .Where(m => m.Date == date && hostIds.Contains(m.HostId))
                .ToListAsync(ct);

            var existingByHost = existingRows.ToDictionary(m => m.HostId, m => m);

            foreach (var host in hostRows)
            {
                var row = existingByHost.TryGetValue(host.HostId, out var found)
                    ? found
                    : new DailyHostMetrics { Date = date, HostId = host.HostId };

                row.RoomsCreated = host.RoomsCreated;
                row.RoomsGoneLive = host.RoomsGoneLive;
                row.TotalJoinersAcrossRooms = joinersByHost.TryGetValue(host.HostId, out var j) ? j : 0;
                row.ReportsAboutHostRooms = reportsByHost.TryGetValue(host.HostId, out var r) ? r : 0;
                row.ComputedAtUtc = DateTime.UtcNow;

                if (found is null)
                {
                    db.DailyHostMetrics.Add(row);
                }
            }
        }

        /// <summary>
        /// RM-4 — onboarding funnel per cohort date, recomputed over a trailing window.
        ///
        /// The trailing window is not an optimisation, it is a correctness requirement:
        /// activation_completed fires when an admin reviews a recording, which can be days after
        /// the user registered. A cohort's later steps therefore keep changing long after its
        /// cohort date has passed, and a one-pass rollup would freeze them too early.
        /// </summary>
        private async Task AggregateFunnelCohortsAsync(AppDbContext db, CancellationToken ct)
        {
            var trailingDays = _options.AggregationTrailingDays > 0 ? _options.AggregationTrailingDays : 45;
            var windowStart = DateTime.UtcNow.Date.AddDays(-trailingDays);

            var steps = new[]
            {
                EventTypes.UserRegistered,
                EventTypes.VoiceVerificationSubmitted,
                EventTypes.ActivationCompleted,
                EventTypes.RoomJoined,
                EventTypes.MicActivated
            };

            // Cohort = the day a user first registered. Their later steps are counted whenever
            // they happened, which is the whole point of the trailing recompute.
            var cohortUsers = await db.UserEvents
                .AsNoTracking()
                .Where(e => e.EventType == EventTypes.UserRegistered
                         && e.UserId != null
                         && e.OccurredAtUtc >= windowStart)
                .GroupBy(e => e.UserId!.Value)
                .Select(g => new { UserId = g.Key, RegisteredAt = g.Min(x => x.OccurredAtUtc) })
                .ToListAsync(ct);

            if (cohortUsers.Count == 0)
            {
                return;
            }

            var userIds = cohortUsers.Select(u => u.UserId).ToList();

            var stepEvents = await db.UserEvents
                .AsNoTracking()
                .Where(e => steps.Contains(e.EventType)
                         && e.UserId != null
                         && userIds.Contains(e.UserId.Value))
                .GroupBy(e => new { UserId = e.UserId!.Value, e.EventType })
                .Select(g => new { g.Key.UserId, g.Key.EventType, FirstAt = g.Min(x => x.OccurredAtUtc) })
                .ToListAsync(ct);

            var firstByUser = stepEvents
                .GroupBy(e => e.UserId)
                .ToDictionary(g => g.Key, g => g.ToDictionary(x => x.EventType, x => x.FirstAt));

            var cohortDateByUser = cohortUsers.ToDictionary(u => u.UserId, u => u.RegisteredAt.Date);
            var cohortDates = cohortDateByUser.Values.Distinct().ToList();

            var existingRows = await db.DailyFunnelMetrics
                .Where(m => m.FunnelName == OnboardingFunnelName && cohortDates.Contains(m.CohortDate))
                .ToListAsync(ct);

            var existingByKey = existingRows.ToDictionary(m => (m.CohortDate, m.StepIndex), m => m);

            foreach (var cohortDate in cohortDates)
            {
                var cohort = cohortDateByUser.Where(kv => kv.Value == cohortDate).Select(kv => kv.Key).ToList();

                var qualified = new HashSet<Guid>(cohort);
                var previousStepTime = new Dictionary<Guid, DateTime>();

                for (byte stepIndex = 0; stepIndex < steps.Length; stepIndex++)
                {
                    var stepName = steps[stepIndex];
                    var nowQualified = new HashSet<Guid>();
                    var gaps = new List<double>();

                    foreach (var userId in qualified)
                    {
                        if (!firstByUser.TryGetValue(userId, out var stepTimes) ||
                            !stepTimes.TryGetValue(stepName, out var stepTime))
                        {
                            continue;
                        }

                        if (stepIndex == 0)
                        {
                            nowQualified.Add(userId);
                            previousStepTime[userId] = stepTime;
                            continue;
                        }

                        if (previousStepTime.TryGetValue(userId, out var prevTime) && stepTime >= prevTime)
                        {
                            nowQualified.Add(userId);
                            gaps.Add((stepTime - prevTime).TotalSeconds);
                            previousStepTime[userId] = stepTime;
                        }
                    }

                    qualified = nowQualified;

                    var row = existingByKey.TryGetValue((cohortDate, stepIndex), out var found)
                        ? found
                        : new DailyFunnelMetrics
                        {
                            CohortDate = cohortDate,
                            FunnelName = OnboardingFunnelName,
                            StepIndex = stepIndex
                        };

                    row.StepName = stepName;
                    row.UsersReached = qualified.Count;
                    row.MedianSecondsFromPrevious = MedianSeconds(gaps);
                    row.ComputedAtUtc = DateTime.UtcNow;

                    if (found is null)
                    {
                        db.DailyFunnelMetrics.Add(row);
                    }
                }
            }
        }

        public const string OnboardingFunnelName = "onboarding";

        private static int MedianSeconds(List<double> values)
        {
            if (values.Count == 0)
            {
                return 0;
            }

            var sorted = values.OrderBy(v => v).ToList();
            var mid = sorted.Count / 2;

            var median = sorted.Count % 2 == 1
                ? sorted[mid]
                : (sorted[mid - 1] + sorted[mid]) / 2.0;

            return (int)Math.Min(int.MaxValue, Math.Round(median));
        }
    }
}
