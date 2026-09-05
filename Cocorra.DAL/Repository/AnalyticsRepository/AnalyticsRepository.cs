using Cocorra.DAL.Data;
using Cocorra.DAL.DTOS.AnalyticsDto;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;
using Microsoft.EntityFrameworkCore;

namespace Cocorra.DAL.Repository.AnalyticsRepository
{
    public partial class AnalyticsRepository : IAnalyticsRepository
    {
        private readonly AppDbContext _context;

        public AnalyticsRepository(AppDbContext context)
        {
            _context = context;
        }

        // ─────────────────────────────────────────────────────────────────────
        // USER GROWTH
        // ─────────────────────────────────────────────────────────────────────
        // ─────────────────────────────────────────────────────────────────────
        // ROOM ANALYTICS
        // ─────────────────────────────────────────────────────────────────────
        public async Task<RoomAnalyticsDto> GetRoomAnalyticsAsync(
            DateTime from,
            DateTime to,
            int topN = 10)
        {
            var rooms = await _context.Rooms
                .AsNoTracking()
                .Where(r => r.StartDate >= from && r.StartDate <= to)
                .Select(r => new
                {
                    r.Id,
                    r.RoomTitle,
                    r.Category,
                    r.Status,
                    r.IsPrivate,
                    r.DurationHours,
                    ParticipantCount = r.Participants.Count
                })
                .ToListAsync();

            if (rooms.Count == 0)
            {
                return new RoomAnalyticsDto { From = from, To = to };
            }

            // ── Category breakdown ─────────────────────────────────────────
            var categoryGroups = rooms
                .GroupBy(r => r.Category.ToString())
                .Select(g => new RoomCategoryStatDto
                {
                    Category = g.Key,
                    Count = g.Count(),
                    Percentage = Math.Round((double)g.Count() / rooms.Count * 100, 2)
                })
                .OrderByDescending(c => c.Count)
                .ToList();

            // ── Top rooms by participant count ─────────────────────────────
            var topRooms = rooms
                .OrderByDescending(r => r.ParticipantCount)
                .Take(topN)
                .Select(r => new TopRoomDto
                {
                    RoomId = r.Id,
                    RoomTitle = r.RoomTitle,
                    Category = r.Category.ToString(),
                    ParticipantCount = r.ParticipantCount,
                    DurationHours = r.DurationHours
                })
                .ToList();

            return new RoomAnalyticsDto
            {
                From = from,
                To = to,
                TotalRooms = rooms.Count,
                ScheduledRooms = rooms.Count(r => r.Status == RoomStatus.Scheduled),
                ActiveRooms = rooms.Count(r => r.Status == RoomStatus.Live),
                EndedRooms = rooms.Count(r => r.Status == RoomStatus.Ended),
                PrivateRooms = rooms.Count(r => r.IsPrivate),
                PublicRooms = rooms.Count(r => !r.IsPrivate),
                AvgParticipantsPerRoom = Math.Round(rooms.Average(r => (double)r.ParticipantCount), 2),
                AvgDurationHours = Math.Round(rooms.Average(r => (double)r.DurationHours), 2),
                RoomsByCategory = categoryGroups,
                TopRooms = topRooms
            };
        }

        // ─────────────────────────────────────────────────────────────────────
        // PARTICIPATION STATS (AN-005: Host-Excluded)
        // ─────────────────────────────────────────────────────────────────────
        public async Task<ParticipationStatsDto> GetParticipationStatsAsync(
            DateTime from,
            DateTime to)
        {
            var participants = await _context.RoomParticipants
                .AsNoTracking()
                .Where(p => p.JoinedAt >= from && p.JoinedAt <= to && p.UserId != p.Room.HostId)
                .Select(p => new
                {
                    p.UserId,
                    p.TotalSpokenSeconds,
                    p.JoinedAt
                })
                .ToListAsync();

            if (participants.Count == 0)
            {
                return new ParticipationStatsDto { From = from, To = to };
            }

            // ── Peak hours (UTC) ───────────────────────────────────────────
            var peakHours = participants
                .GroupBy(p => p.JoinedAt.Hour)
                .Select(g => new PeakHourDto { Hour = g.Key, JoinCount = g.Count() })
                .OrderBy(h => h.Hour)
                .ToList();

            var totalSpokenSeconds = participants.Sum(p => p.TotalSpokenSeconds);

            // AN-005 step 3: derive speakers from mic_activated, host-excluded, rather than
            // TotalSpokenSeconds > 0. Host exclusion alone is not enough — a non-host promoted
            // to the stage is unmuted by default too, so accrued seconds still include idle
            // open-mic time. Only the event proves someone actually took the mic.
            var usersWhoSpoke = await CountDistinctNonHostMicActivatorsAsync(from, to);

            return new ParticipationStatsDto
            {
                From = from,
                To = to,
                TotalParticipations = participants.Count,
                AvgSpokenSecondsPerParticipant = participants.Count > 0
                    ? Math.Round(participants.Average(p => p.TotalSpokenSeconds), 2)
                    : 0,
                TotalSpokenHours = Math.Round(totalSpokenSeconds / 3600.0, 2),
                UsersWhoSpoke = usersWhoSpoke,
                PeakHours = peakHours
            };
        }

        /// <summary>
        /// Distinct users with a mic_activated event in the window, excluding each event's own
        /// room host. Shared by GetParticipationStatsAsync and GetActiveVsPassiveRateAsync so
        /// the two panels cannot disagree about who spoke.
        /// </summary>
        private async Task<int> CountDistinctNonHostMicActivatorsAsync(DateTime from, DateTime to)
        {
            return await _context.UserEvents
                .AsNoTracking()
                .Where(e => e.EventType == EventTypes.MicActivated
                         && e.OccurredAtUtc >= from && e.OccurredAtUtc <= to
                         && e.UserId != null)
                .Where(e => e.RoomId == null
                         || !_context.Rooms.Any(r => r.Id == e.RoomId && r.HostId == e.UserId))
                .Select(e => e.UserId)
                .Distinct()
                .CountAsync();
        }

        // ─────────────────────────────────────────────────────────────────────
        // REPORT INSIGHTS
        // ─────────────────────────────────────────────────────────────────────
        public async Task<ReportInsightsDto> GetReportInsightsAsync(
            DateTime from,
            DateTime to,
            int topN = 10)
        {
            var reports = await _context.Reports
                .AsNoTracking()
                .Where(r => r.CreatedAt >= from && r.CreatedAt <= to)
                .Select(r => new
                {
                    r.Category,
                    r.Status,
                    r.ReportedUserId,
                    ReportedUserFirstName = r.ReportedUser != null ? r.ReportedUser.FirstName : string.Empty,
                    ReportedUserLastName = r.ReportedUser != null ? r.ReportedUser.LastName : string.Empty,
                    ReportedUserEmail = r.ReportedUser != null ? r.ReportedUser.Email : string.Empty
                })
                .ToListAsync();

            if (reports.Count == 0)
            {
                return new ReportInsightsDto { From = from, To = to };
            }

            // ── Category breakdown ─────────────────────────────────────────
            var categoryStats = reports
                .GroupBy(r => r.Category.ToString())
                .Select(g => new ReportCategoryStatDto
                {
                    Category = g.Key,
                    Count = g.Count(),
                    Percentage = Math.Round((double)g.Count() / reports.Count * 100, 2)
                })
                .OrderByDescending(c => c.Count)
                .ToList();

            // ── Most reported users ────────────────────────────────────────
            var mostReported = reports
                .Where(r => r.ReportedUserId.HasValue)
                .GroupBy(r => r.ReportedUserId!.Value)
                .Select(g => new MostReportedUserDto
                {
                    UserId = g.Key,
                    FullName = $"{g.First().ReportedUserFirstName} {g.First().ReportedUserLastName}".Trim(),
                    Email = g.First().ReportedUserEmail ?? string.Empty,
                    ReportCount = g.Count()
                })
                .OrderByDescending(u => u.ReportCount)
                .Take(topN)
                .ToList();

            return new ReportInsightsDto
            {
                From = from,
                To = to,
                TotalReports = reports.Count,
                OpenReports = reports.Count(r => r.Status.Equals("Open", StringComparison.OrdinalIgnoreCase)),
                ResolvedReports = reports.Count(r => r.Status.Equals("Resolved", StringComparison.OrdinalIgnoreCase)),
                InProgressReports = reports.Count(r => r.Status.Equals("InProgress", StringComparison.OrdinalIgnoreCase)),
                ReportsByCategory = categoryStats,
                MostReportedUsers = mostReported
            };
        }

        // AN-007: Sequential Funnel with strict monotonicity guarantee
        public async Task<Dictionary<string, int>> GetFunnelAsync(
            string[] steps, 
            DateTime fromUtc, 
            DateTime toUtc)
        {
            if (steps == null || steps.Length == 0)
            {
                return new Dictionary<string, int>();
            }

            // Fetch candidate step events in the window
            var userEvents = await _context.UserEvents
                .AsNoTracking()
                .Where(e => steps.Contains(e.EventType)
                         && e.OccurredAtUtc >= fromUtc 
                         && e.OccurredAtUtc <= toUtc
                         && e.UserId != null)
                .Select(e => new
                {
                    UserId = e.UserId!.Value,
                    e.EventType,
                    e.OccurredAtUtc
                })
                .ToListAsync();

            // Group by user and find earliest timestamp for each step
            var userEarliestSteps = userEvents
                .GroupBy(e => e.UserId)
                .Select(g => new
                {
                    UserId = g.Key,
                    StepTimes = g.GroupBy(x => x.EventType)
                                 .ToDictionary(sg => sg.Key, sg => sg.Min(x => x.OccurredAtUtc))
                })
                .ToList();

            var result = new Dictionary<string, int>();
            var qualifiedUserIds = new HashSet<Guid>(userEarliestSteps.Select(u => u.UserId));
            var previousStepTimes = new Dictionary<Guid, DateTime>();

            for (int i = 0; i < steps.Length; i++)
            {
                var stepName = steps[i];
                var currentStepQualified = new HashSet<Guid>();

                foreach (var user in userEarliestSteps)
                {
                    if (!qualifiedUserIds.Contains(user.UserId))
                    {
                        continue;
                    }

                    if (user.StepTimes.TryGetValue(stepName, out var stepTime))
                    {
                        // Step 0 requires only completion; step N requires stepTime >= stepN-1 time
                        if (i == 0 || (previousStepTimes.TryGetValue(user.UserId, out var prevTime) && stepTime >= prevTime))
                        {
                            currentStepQualified.Add(user.UserId);
                            previousStepTimes[user.UserId] = stepTime;
                        }
                    }
                }

                qualifiedUserIds = currentStepQualified;
                result[stepName] = qualifiedUserIds.Count;
            }

            return result;
        }

        // AN-006: Server-Authoritative Return Metric
        public async Task<Dictionary<int, double>> GetRetentionCohortAsync(
            string cohortEvent, 
            string activeEvent, 
            DateTime cohortStartUtc, 
            DateTime cohortEndUtc)
        {
            // 1. Cohort users who performed cohortEvent in the cohort window
            var cohortUsers = await _context.UserEvents
                .AsNoTracking()
                .Where(e => e.EventType == cohortEvent 
                         && e.OccurredAtUtc >= cohortStartUtc 
                         && e.OccurredAtUtc <= cohortEndUtc 
                         && e.UserId != null)
                .GroupBy(e => e.UserId)
                .Select(g => new
                {
                    UserId = g.Key!.Value,
                    CohortDate = g.Min(x => x.OccurredAtUtc)
                })
                .ToListAsync();

            if (!cohortUsers.Any())
            {
                return new Dictionary<int, double> { { 1, 0.0 }, { 7, 0.0 }, { 30, 0.0 } };
            }

            var cohortUserIds = cohortUsers.Select(u => u.UserId).ToList();
            var userCohortMap = cohortUsers.ToDictionary(u => u.UserId, u => u.CohortDate);

            // 2. Bounded fetch. The unbounded version scanned every activity event a cohort
            // user ever produced. Exact-day matching never looks past D30, so D30 + 1 day of
            // slack is sufficient and the bound cannot change the result.
            var maxActivityDate = cohortEndUtc.AddDays(31);
            var activityEvents = await _context.UserEvents
                .AsNoTracking()
                .Where(e => e.EventType == activeEvent 
                         && e.UserId != null 
                         && cohortUserIds.Contains(e.UserId.Value)
                         && e.OccurredAtUtc >= cohortStartUtc
                         && e.OccurredAtUtc <= maxActivityDate)
                .Select(e => new
                {
                    UserId = e.UserId!.Value,
                    e.OccurredAtUtc
                })
                .ToListAsync();

            var cohortSize = cohortUsers.Count;
            var retentionDays = new[] { 1, 7, 30 };
            var retentionCounts = new Dictionary<int, int> { { 1, 0 }, { 7, 0 }, { 30, 0 } };

            foreach (var day in retentionDays)
            {
                var activeOnDayCount = activityEvents
                    .Where(e =>
                    {
                        var cohortDate = userCohortMap[e.UserId];
                        var timeDiff = e.OccurredAtUtc.Date - cohortDate.Date;
                        return timeDiff.Days == day;
                    })
                    .Select(e => e.UserId)
                    .Distinct()
                    .Count();

                retentionCounts[day] = activeOnDayCount;
            }

            return retentionCounts.ToDictionary(
                kv => kv.Key,
                kv => cohortSize > 0 ? Math.Round((double)kv.Value / cohortSize * 100, 2) : 0.0
            );
        }

        // ─────────────────────────────────────────────────────────────────────
        // EVENT-DRIVEN METRICS (UserEvents)
        // ─────────────────────────────────────────────────────────────────────

        // Most active room — ranked by room_joined events (actual attendance).
        public async Task<List<TopActiveRoomDto>> GetMostActiveRoomsAsync(
            DateTime from,
            DateTime to,
            int topN = 10)
        {
            // Aggregate on the indexed event table (RoomId promoted from PropertiesJson).
            var ranked = await _context.UserEvents
                .AsNoTracking()
                .Where(e => e.EventType == EventTypes.RoomJoined
                         && e.OccurredAtUtc >= from && e.OccurredAtUtc <= to
                         && e.RoomId != null)
                .GroupBy(e => e.RoomId!.Value)
                .Select(g => new
                {
                    RoomId = g.Key,
                    JoinEvents = g.Count(),
                    UniqueJoiners = g.Select(x => x.UserId).Distinct().Count()
                })
                .OrderByDescending(r => r.JoinEvents)
                .Take(topN)
                .ToListAsync();

            if (ranked.Count == 0)
            {
                return new List<TopActiveRoomDto>();
            }

            // Enrich the ranked ids with room details in one follow-up query.
            var roomIds = ranked.Select(r => r.RoomId).ToList();
            var roomInfo = await _context.Rooms
                .AsNoTracking()
                .Where(r => roomIds.Contains(r.Id))
                .Select(r => new { r.Id, r.RoomTitle, r.Category })
                .ToDictionaryAsync(r => r.Id);

            return ranked
                .Select(r => new TopActiveRoomDto
                {
                    RoomId = r.RoomId,
                    RoomTitle = roomInfo.TryGetValue(r.RoomId, out var info) ? info.RoomTitle : string.Empty,
                    Category = roomInfo.TryGetValue(r.RoomId, out var info2) ? info2.Category.ToString() : string.Empty,
                    JoinEvents = r.JoinEvents,
                    UniqueJoiners = r.UniqueJoiners
                })
                .ToList();
        }

        // Peak active hours — grouped by UTC hour-of-day (DateTime.Hour → DATEPART on SQL Server).
        public async Task<List<HourlyActivityDto>> GetPeakActiveHoursAsync(
            DateTime from,
            DateTime to)
        {
            var rows = await _context.UserEvents
                .AsNoTracking()
                .Where(e => e.OccurredAtUtc >= from && e.OccurredAtUtc <= to)
                .GroupBy(e => e.OccurredAtUtc.Hour)
                .Select(g => new HourlyActivityDto
                {
                    Hour = g.Key,
                    EventCount = g.Count(),
                    ActiveUsers = g.Select(x => x.UserId).Distinct().Count()
                })
                .ToListAsync();

            // Fill 0–23 so the dashboard chart always has every hour.
            return Enumerable.Range(0, 24)
                .Select(h => rows.FirstOrDefault(r => r.Hour == h)
                             ?? new HourlyActivityDto { Hour = h })
                .OrderBy(r => r.Hour)
                .ToList();
        }

        // Voice verification drop-off — distinct users submitted vs. completed activation.
        public async Task<VoiceVerificationFunnelDto> GetVoiceVerificationDropOffAsync(
            DateTime from,
            DateTime to)
        {
            var counts = await _context.UserEvents
                .AsNoTracking()
                .Where(e => (e.EventType == EventTypes.VoiceVerificationSubmitted
                          || e.EventType == EventTypes.ActivationCompleted)
                         && e.OccurredAtUtc >= from && e.OccurredAtUtc <= to
                         && e.UserId != null)
                .GroupBy(e => e.EventType)
                .Select(g => new { g.Key, Users = g.Select(x => x.UserId).Distinct().Count() })
                .ToDictionaryAsync(x => x.Key, x => x.Users);

            counts.TryGetValue(EventTypes.VoiceVerificationSubmitted, out var started);
            counts.TryGetValue(EventTypes.ActivationCompleted, out var completed);

            return new VoiceVerificationFunnelDto
            {
                From = from,
                To = to,
                Started = started,
                Completed = completed,
                DropOffRate = started > 0 ? Math.Round((1.0 - (double)completed / started) * 100, 2) : 0,
                CompletionRate = started > 0 ? Math.Round((double)completed / started * 100, 2) : 0
            };
        }

        // Active (took the mic) vs passive (join-only) participation (AN-005 / M-101: Host-Excluded).
        public async Task<ParticipationModeDto> GetActiveVsPassiveRateAsync(
            DateTime from,
            DateTime to)
        {
            // Distinct users who joined at least one room in the window
            var joinedEvents = await _context.UserEvents
                .AsNoTracking()
                .Where(e => e.EventType == EventTypes.RoomJoined
                         && e.OccurredAtUtc >= from && e.OccurredAtUtc <= to
                         && e.UserId != null)
                .Select(e => new { e.UserId, e.RoomId })
                .ToListAsync();

            // Load host map for rooms in the events to perform host exclusion
            var roomIds = joinedEvents.Where(e => e.RoomId != null).Select(e => e.RoomId!.Value).Distinct().ToList();
            var roomHostMap = await _context.Rooms
                .AsNoTracking()
                .Where(r => roomIds.Contains(r.Id))
                .ToDictionaryAsync(r => r.Id, r => r.HostId);

            var nonHostJoiners = joinedEvents
                .Where(e => e.RoomId == null || !roomHostMap.TryGetValue(e.RoomId.Value, out var hostId) || e.UserId != hostId)
                .Select(e => e.UserId!.Value)
                .Distinct()
                .ToList();

            int total = nonHostJoiners.Count;
            if (total == 0)
            {
                return new ParticipationModeDto { From = from, To = to };
            }

            var micEvents = await _context.UserEvents
                .AsNoTracking()
                .Where(e => e.EventType == EventTypes.MicActivated
                         && e.OccurredAtUtc >= from && e.OccurredAtUtc <= to
                         && e.UserId != null
                         && nonHostJoiners.Contains(e.UserId.Value))
                .Select(e => new { e.UserId, e.RoomId })
                .ToListAsync();

            var nonHostSpeakers = micEvents
                .Where(e => e.RoomId == null || !roomHostMap.TryGetValue(e.RoomId.Value, out var hostId) || e.UserId != hostId)
                .Select(e => e.UserId!.Value)
                .Distinct()
                .Count();

            int passive = Math.Max(0, total - nonHostSpeakers);

            return new ParticipationModeDto
            {
                From = from,
                To = to,
                TotalParticipants = total,
                ActiveSpeakers = nonHostSpeakers,
                PassiveListeners = passive,
                ActiveRate = Math.Round((double)nonHostSpeakers / total * 100, 2)
            };
        }
    }
}
