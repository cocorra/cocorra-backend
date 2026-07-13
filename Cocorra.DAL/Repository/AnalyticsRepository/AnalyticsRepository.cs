using Cocorra.DAL.Data;
using Cocorra.DAL.DTOS.AnalyticsDto;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;
using Microsoft.EntityFrameworkCore;

namespace Cocorra.DAL.Repository.AnalyticsRepository
{
    public class AnalyticsRepository : IAnalyticsRepository
    {
        private readonly AppDbContext _context;

        public AnalyticsRepository(AppDbContext context)
        {
            _context = context;
        }

        // ─────────────────────────────────────────────────────────────────────
        // USER GROWTH
        // ─────────────────────────────────────────────────────────────────────
        public async Task<UserGrowthDto> GetUserGrowthAsync(
            string granularity,
            DateTime from,
            DateTime to,
            int topN = 10)
        {
            var usersInPeriod = await _context.Users
                .AsNoTracking()
                .Where(u => u.CreatedAt >= from && u.CreatedAt <= to)
                .Select(u => new
                {
                    u.CreatedAt,
                    u.Status,
                    u.MBTI,
                    u.Age
                })
                .ToListAsync();

            // ── Time bucketing ──────────────────────────────────────────────
            var grouped = granularity.Equals("monthly", StringComparison.OrdinalIgnoreCase)
                ? usersInPeriod.GroupBy(u => new DateTime(u.CreatedAt.Year, u.CreatedAt.Month, 1))
                : usersInPeriod.GroupBy(u => u.CreatedAt.Date);

            var dataPoints = grouped
                .OrderBy(g => g.Key)
                .Select(g =>
                {
                    var label = granularity.Equals("monthly", StringComparison.OrdinalIgnoreCase)
                        ? g.Key.ToString("yyyy-MM")
                        : g.Key.ToString("yyyy-MM-dd");

                    return new UserGrowthDataPointDto
                    {
                        Period = label,
                        NewUsers = g.Count(),
                        ActiveUsers = g.Count(u => u.Status == UserStatus.Active),
                        PendingUsers = g.Count(u => u.Status == UserStatus.Pending),
                        BannedUsers = g.Count(u => u.Status == UserStatus.Banned),
                        RejectedUsers = g.Count(u => u.Status == UserStatus.Rejected),
                        ReRecordUsers = g.Count(u => u.Status == UserStatus.ReRecord)
                    };
                })
                .ToList();

            // ── Status breakdown (all-time in window) ──────────────────────
            var statusBreakdown = usersInPeriod
                .GroupBy(u => u.Status.ToString())
                .ToDictionary(g => g.Key, g => g.Count());

            // ── MBTI distribution ──────────────────────────────────────────
            var mbtiDist = usersInPeriod
                .Where(u => !string.IsNullOrWhiteSpace(u.MBTI))
                .GroupBy(u => u.MBTI!)
                .OrderByDescending(g => g.Count())
                .ToDictionary(g => g.Key, g => g.Count());

            // ── Average age ────────────────────────────────────────────────
            var avgAge = usersInPeriod.Count > 0
                ? usersInPeriod.Average(u => (double)u.Age)
                : 0;

            return new UserGrowthDto
            {
                Granularity = granularity.ToLower(),
                From = from,
                To = to,
                TotalUsersInPeriod = usersInPeriod.Count,
                DataPoints = dataPoints,
                StatusBreakdown = statusBreakdown,
                MbtiDistribution = mbtiDist,
                AverageAge = Math.Round(avgAge, 2)
            };
        }

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
        // PARTICIPATION STATS
        // ─────────────────────────────────────────────────────────────────────
        public async Task<ParticipationStatsDto> GetParticipationStatsAsync(
            DateTime from,
            DateTime to,
            int topN = 10)
        {
            var participants = await _context.RoomParticipants
                .AsNoTracking()
                .Where(p => p.JoinedAt >= from && p.JoinedAt <= to)
                .Select(p => new
                {
                    p.UserId,
                    p.TotalSpokenSeconds,
                    p.IsHandRaised,
                    p.JoinedAt,
                    UserFirstName = p.User != null ? p.User.FirstName : string.Empty,
                    UserLastName = p.User != null ? p.User.LastName : string.Empty
                })
                .ToListAsync();

            if (participants.Count == 0)
            {
                return new ParticipationStatsDto { From = from, To = to };
            }

            // ── Top speakers ───────────────────────────────────────────────
            var topSpeakers = participants
                .GroupBy(p => p.UserId)
                .Select(g => new TopSpeakerDto
                {
                    UserId = g.Key,
                    FullName = $"{g.First().UserFirstName} {g.First().UserLastName}".Trim(),
                    TotalSpokenSeconds = g.Sum(p => p.TotalSpokenSeconds),
                    RoomsParticipatedIn = g.Count()
                })
                .Where(s => s.TotalSpokenSeconds > 0)
                .OrderByDescending(s => s.TotalSpokenSeconds)
                .Take(topN)
                .ToList();

            // ── Peak hours (UTC) ───────────────────────────────────────────
            var peakHours = participants
                .GroupBy(p => p.JoinedAt.Hour)
                .Select(g => new PeakHourDto { Hour = g.Key, JoinCount = g.Count() })
                .OrderBy(h => h.Hour)
                .ToList();

            var totalSpokenSeconds = participants.Sum(p => p.TotalSpokenSeconds);

            return new ParticipationStatsDto
            {
                From = from,
                To = to,
                TotalParticipations = participants.Count,
                AvgSpokenSecondsPerParticipant = participants.Count > 0
                    ? Math.Round(participants.Average(p => p.TotalSpokenSeconds), 2)
                    : 0,
                TotalSpokenHours = Math.Round(totalSpokenSeconds / 3600.0, 2),
                UsersWhoSpoke = participants.Count(p => p.TotalSpokenSeconds > 0),
                UsersWhoRaisedHand = participants.Count(p => p.IsHandRaised),
                TopSpeakers = topSpeakers,
                PeakHours = peakHours
            };
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

        public async Task<Dictionary<string, int>> GetFunnelAsync(
            string[] steps, 
            DateTime fromUtc, 
            DateTime toUtc)
        {
            var eventCounts = await _context.UserEvents
                .Where(e => steps.Contains(e.EventType)
                         && e.OccurredAtUtc >= fromUtc 
                         && e.OccurredAtUtc <= toUtc
                         && e.UserId != null)
                .GroupBy(e => e.EventType)
                .Select(g => new { g.Key, Count = g.Select(x => x.UserId).Distinct().Count() })
                .ToDictionaryAsync(x => x.Key, x => x.Count);

            // Ensure all steps have a value (defaulting to 0) and preserve initial step ordering
            var result = new Dictionary<string, int>();
            foreach (var step in steps)
            {
                result[step] = eventCounts.TryGetValue(step, out var count) ? count : 0;
            }

            return result;
        }

        public async Task<Dictionary<int, double>> GetRetentionCohortAsync(
            string cohortEvent, 
            string activeEvent, 
            DateTime cohortStartUtc, 
            DateTime cohortEndUtc)
        {
            // 1. Get the cohort of users who performed the cohortEvent in the time window,
            // and their cohort date (first time they did it in this cohort window)
            var cohortUsers = await _context.UserEvents
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

            // 2. Get all active events for these users that occurred after their cohort date
            var activityEvents = await _context.UserEvents
                .Where(e => e.EventType == activeEvent 
                         && e.UserId != null 
                         && cohortUserIds.Contains(e.UserId.Value))
                .Select(e => new
                {
                    UserId = e.UserId!.Value,
                    e.OccurredAtUtc
                })
                .ToListAsync();

            // 3. Map user cohort date for ease of calculations
            var userCohortMap = cohortUsers.ToDictionary(u => u.UserId, u => u.CohortDate);

            // 4. Calculate day differences and group by day offset (D1, D7, D30)
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

        // Active (took the mic) vs passive (join-only) participation.
        public async Task<ParticipationModeDto> GetActiveVsPassiveRateAsync(
            DateTime from,
            DateTime to)
        {
            // Distinct users who joined at least one room in the window.
            var joined = await _context.UserEvents
                .AsNoTracking()
                .Where(e => e.EventType == EventTypes.RoomJoined
                         && e.OccurredAtUtc >= from && e.OccurredAtUtc <= to
                         && e.UserId != null)
                .Select(e => e.UserId)
                .Distinct()
                .ToListAsync();

            int total = joined.Count;
            if (total == 0)
            {
                return new ParticipationModeDto { From = from, To = to };
            }

            // Of those joiners, how many ever activated the mic in the window.
            int speakers = await _context.UserEvents
                .AsNoTracking()
                .Where(e => e.EventType == EventTypes.MicActivated
                         && e.OccurredAtUtc >= from && e.OccurredAtUtc <= to
                         && e.UserId != null && joined.Contains(e.UserId))
                .Select(e => e.UserId)
                .Distinct()
                .CountAsync();

            return new ParticipationModeDto
            {
                From = from,
                To = to,
                TotalParticipants = total,
                ActiveSpeakers = speakers,
                PassiveListeners = total - speakers,
                ActiveRate = Math.Round((double)speakers / total * 100, 2)
            };
        }
    }
}
