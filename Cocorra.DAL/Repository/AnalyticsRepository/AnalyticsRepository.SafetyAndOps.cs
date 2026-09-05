using Cocorra.DAL.DTOS.AnalyticsDto;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;
using Microsoft.EntityFrameworkCore;

namespace Cocorra.DAL.Repository.AnalyticsRepository
{
    /// <summary>
    /// AN-021, AN-022, AN-023 — safety, review latency and support.
    /// All three run over data the audit already verified; none needed a new event.
    /// </summary>
    public partial class AnalyticsRepository
    {
        // ─────────────────────────────────────────────────────────────────────
        // AN-021 / M-301 — report rate by room category
        // ─────────────────────────────────────────────────────────────────────
        public async Task<ReportRateInsightsDto> GetReportRateByCategoryAsync(
            DateTime fromUtc,
            DateTime toUtc)
        {
            var reports = await _context.Reports
                .AsNoTracking()
                .Where(r => r.CreatedAt >= fromUtc && r.CreatedAt <= toUtc)
                .Select(r => new { r.Id, r.ReportedRoomId })
                .ToListAsync();

            var withRoom = reports.Where(r => r.ReportedRoomId != null).ToList();
            var reportedRoomIds = withRoom.Select(r => r.ReportedRoomId!.Value).Distinct().ToList();

            var categoryByRoom = await _context.Rooms
                .AsNoTracking()
                .Where(r => reportedRoomIds.Contains(r.Id))
                .ToDictionaryAsync(r => r.Id, r => r.Category);

            var reportsByCategory = withRoom
                .Where(r => categoryByRoom.ContainsKey(r.ReportedRoomId!.Value))
                .GroupBy(r => categoryByRoom[r.ReportedRoomId!.Value])
                .ToDictionary(g => g.Key, g => g.Count());

            // Exposure denominator: distinct non-host joins of rooms in each category during
            // the window. Without it this ranks categories by popularity, not by risk.
            var joinsByCategory = await _context.RoomParticipants
                .AsNoTracking()
                .Where(p => p.JoinedAt >= fromUtc && p.JoinedAt <= toUtc && p.UserId != p.Room!.HostId)
                .GroupBy(p => p.Room!.Category)
                .Select(g => new { Category = g.Key, Joins = g.Count() })
                .ToListAsync();

            var joinsLookup = joinsByCategory.ToDictionary(j => j.Category, j => j.Joins);

            var roomsByCategory = await _context.Rooms
                .AsNoTracking()
                .Where(r => r.CreatedAt >= fromUtc && r.CreatedAt <= toUtc)
                .GroupBy(r => r.Category)
                .Select(g => new { Category = g.Key, Rooms = g.Count() })
                .ToListAsync();

            var roomsLookup = roomsByCategory.ToDictionary(r => r.Category, r => r.Rooms);

            var categories = Enum.GetValues<RoomCategory>()
                .Select(category =>
                {
                    var reportCount = reportsByCategory.TryGetValue(category, out var rc) ? rc : 0;
                    var joins = joinsLookup.TryGetValue(category, out var j) ? j : 0;

                    return new ReportRateByCategoryDto
                    {
                        Category = category.ToString(),
                        ReportCount = reportCount,
                        RoomJoins = joins,
                        RoomsInCategory = roomsLookup.TryGetValue(category, out var rm) ? rm : 0,
                        // Null, not 0: with no joins there is no exposure to normalise against,
                        // and a rate of zero would read as "this category is safe".
                        ReportsPer1000Joins = joins > 0
                            ? Math.Round((double)reportCount / joins * 1000, 2)
                            : null
                    };
                })
                .OrderByDescending(c => c.ReportsPer1000Joins ?? -1)
                .ToList();

            return new ReportRateInsightsDto
            {
                From = fromUtc,
                To = toUtc,
                Categories = categories,
                // Excluded rather than bucketed into Others, which would inflate that category
                // with reports that have nothing to do with it.
                ReportsWithoutRoomContext = reports.Count - withRoom.Count,
                TotalReports = reports.Count
            };
        }

        // ─────────────────────────────────────────────────────────────────────
        // AN-022 / M-302 — admin review latency
        //
        // Correcting the earlier audit: 06-blind-spots.md concluded this was unmeasurable, but
        // it considered only the relational data. ApplicationUser has no UpdatedAt, so the
        // relational route really is a dead end — the event PAIR, however, carries it.
        // ─────────────────────────────────────────────────────────────────────
        public async Task<ReviewLatencyDto> GetReviewLatencyAsync(
            DateTime fromUtc,
            DateTime toUtc)
        {
            var submissions = await _context.UserEvents
                .AsNoTracking()
                .Where(e => e.EventType == EventTypes.VoiceVerificationSubmitted
                         && e.UserId != null
                         && e.OccurredAtUtc >= fromUtc && e.OccurredAtUtc <= toUtc)
                .GroupBy(e => e.UserId!.Value)
                .Select(g => new { UserId = g.Key, SubmittedAt = g.Min(x => x.OccurredAtUtc) })
                .ToListAsync();

            if (submissions.Count == 0)
            {
                return new ReviewLatencyDto { From = fromUtc, To = toUtc };
            }

            var userIds = submissions.Select(s => s.UserId).ToList();

            // Results are NOT bounded by the window end: a submission near the window edge is
            // reviewed after it, and excluding those would systematically drop the slowest
            // reviews — biasing the very tail this metric exists to expose.
            var results = await _context.UserEvents
                .AsNoTracking()
                .Where(e => e.EventType == EventTypes.VoiceVerificationResult
                         && e.UserId != null
                         && userIds.Contains(e.UserId.Value)
                         && e.OccurredAtUtc >= fromUtc)
                .GroupBy(e => e.UserId!.Value)
                .Select(g => new { UserId = g.Key, ResultAt = g.Min(x => x.OccurredAtUtc) })
                .ToListAsync();

            var resultByUser = results.ToDictionary(r => r.UserId, r => r.ResultAt);

            var measured = submissions
                .Where(s => resultByUser.ContainsKey(s.UserId) && resultByUser[s.UserId] >= s.SubmittedAt)
                .Select(s => new
                {
                    s.SubmittedAt,
                    Hours = (resultByUser[s.UserId] - s.SubmittedAt).TotalHours
                })
                .ToList();

            var allHours = measured.Select(m => m.Hours).ToList();

            var byDay = measured
                .GroupBy(m => m.SubmittedAt.DayOfWeek)
                .OrderBy(g => (int)g.Key)
                .Select(g => new ReviewLatencyByBucketDto
                {
                    Bucket = g.Key.ToString(),
                    ReviewsMeasured = g.Count(),
                    P50Hours = Percentile(g.Select(x => x.Hours).ToList(), 0.50),
                    P90Hours = Percentile(g.Select(x => x.Hours).ToList(), 0.90)
                })
                .ToList();

            var byHour = measured
                .GroupBy(m => m.SubmittedAt.Hour)
                .OrderBy(g => g.Key)
                .Select(g => new ReviewLatencyByBucketDto
                {
                    Bucket = g.Key.ToString("D2"),
                    ReviewsMeasured = g.Count(),
                    P50Hours = Percentile(g.Select(x => x.Hours).ToList(), 0.50),
                    P90Hours = Percentile(g.Select(x => x.Hours).ToList(), 0.90)
                })
                .ToList();

            // Queue depth beside the latency: a fast p50 with a growing backlog is a different
            // situation from a fast p50 with an empty one.
            var latestQueueDepth = await _context.DailyStateSnapshots
                .AsNoTracking()
                .Where(s => s.MetricKey == "pending_verification_queue")
                .OrderByDescending(s => s.Date)
                .Select(s => new { s.Value, s.Date })
                .FirstOrDefaultAsync();

            var earliestSubmission = await _context.UserEvents
                .AsNoTracking()
                .Where(e => e.EventType == EventTypes.VoiceVerificationSubmitted)
                .OrderBy(e => e.OccurredAtUtc)
                .Select(e => (DateTime?)e.OccurredAtUtc)
                .FirstOrDefaultAsync();

            return new ReviewLatencyDto
            {
                From = fromUtc,
                To = toUtc,
                ReviewsMeasured = measured.Count,
                // No mean is returned anywhere in this response, by contract.
                P50Hours = Percentile(allHours, 0.50),
                P90Hours = Percentile(allHours, 0.90),
                P99Hours = Percentile(allHours, 0.99),
                ByDayOfWeekUtc = byDay,
                ByHourUtc = byHour,
                CurrentPendingQueueDepth = latestQueueDepth?.Value,
                QueueDepthAsOfUtc = latestQueueDepth?.Date,
                DataAvailableFromUtc = earliestSubmission
            };
        }

        // ─────────────────────────────────────────────────────────────────────
        // AN-023 / M-601 — support analytics
        // ─────────────────────────────────────────────────────────────────────
        public async Task<SupportAnalyticsDto> GetSupportAnalyticsAsync(
            DateTime fromUtc,
            DateTime toUtc)
        {
            var tickets = await _context.SupportTickets
                .AsNoTracking()
                .Where(t => t.CreatedAt >= fromUtc && t.CreatedAt <= toUtc)
                .Select(t => new { t.Type, t.UserId })
                .ToListAsync();

            // Denominator for normalisation: users active enough to be able to hit a problem.
            var activeUsers = await _context.Users
                .AsNoTracking()
                .CountAsync(u => u.Status == UserStatus.Active);

            var byType = Enum.GetValues<SupportTicketType>()
                .Select(type =>
                {
                    var count = tickets.Count(t => t.Type == type);
                    return new SupportTypeStatDto
                    {
                        Type = type.ToString(),
                        TicketCount = count,
                        TicketsPer1000ActiveUsers = activeUsers > 0
                            ? Math.Round((double)count / activeUsers * 1000, 2)
                            : null
                    };
                })
                .OrderByDescending(t => t.TicketCount)
                .ToList();

            var chats = await _context.SupportChats
                .AsNoTracking()
                .Where(c => c.CreatedAt >= fromUtc && c.CreatedAt <= toUtc)
                .Select(c => new { c.Id, c.CreatedAt, c.ClosedAt })
                .ToListAsync();

            var chatIds = chats.Select(c => c.Id).ToList();

            var firstAdminReply = await _context.SupportMessages
                .AsNoTracking()
                .Where(m => chatIds.Contains(m.SupportChatId) && m.IsFromAdmin)
                .GroupBy(m => m.SupportChatId)
                .Select(g => new { ChatId = g.Key, FirstAt = g.Min(x => x.CreatedAt) })
                .ToListAsync();

            var replyByChat = firstAdminReply.ToDictionary(r => r.ChatId, r => r.FirstAt);

            var responseMinutes = chats
                .Where(c => replyByChat.ContainsKey(c.Id) && replyByChat[c.Id] >= c.CreatedAt)
                .Select(c => (replyByChat[c.Id] - c.CreatedAt).TotalMinutes)
                .ToList();

            var resolutionHours = chats
                .Where(c => c.ClosedAt.HasValue && c.ClosedAt.Value >= c.CreatedAt)
                .Select(c => (c.ClosedAt!.Value - c.CreatedAt).TotalHours)
                .ToList();

            return new SupportAnalyticsDto
            {
                From = fromUtc,
                To = toUtc,
                TotalTickets = tickets.Count,
                // Anonymous tickets are included, not filtered: a user who cannot log in is
                // precisely the reliability signal this metric exists to catch.
                AnonymousTickets = tickets.Count(t => t.UserId is null),
                ByType = byType,
                ChatsOpened = chats.Count,
                ChatsClosed = chats.Count(c => c.ClosedAt.HasValue),
                MedianFirstResponseMinutes = Percentile(responseMinutes, 0.50),
                P90FirstResponseMinutes = Percentile(responseMinutes, 0.90),
                MedianResolutionHours = Percentile(resolutionHours, 0.50)
            };
        }
    }
}
