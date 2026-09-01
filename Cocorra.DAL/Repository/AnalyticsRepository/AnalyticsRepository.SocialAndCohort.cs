using System.Text.Json;
using Cocorra.DAL.DTOS.AnalyticsDto;
using Cocorra.DAL.Models;
using Microsoft.EntityFrameworkCore;

namespace Cocorra.DAL.Repository.AnalyticsRepository
{
    /// <summary>AN-029, AN-037, AN-038.</summary>
    public partial class AnalyticsRepository
    {
        // ─────────────────────────────────────────────────────────────────────
        // AN-029 / M-701 — social graph
        // ─────────────────────────────────────────────────────────────────────
        public async Task<SocialGraphDto> GetSocialGraphAsync(DateTime fromUtc, DateTime toUtc)
        {
            var sent = await _context.UserEvents
                .AsNoTracking()
                .Where(e => e.EventType == EventTypes.FriendRequestSent
                         && e.UserId != null
                         && e.OccurredAtUtc >= fromUtc && e.OccurredAtUtc <= toUtc)
                .Select(e => e.UserId!.Value)
                .ToListAsync();

            var accepted = await _context.UserEvents
                .AsNoTracking()
                .Where(e => e.EventType == EventTypes.FriendRequestAccepted
                         && e.UserId != null
                         && e.OccurredAtUtc >= fromUtc && e.OccurredAtUtc <= toUtc)
                .Select(e => e.PropertiesJson)
                .ToListAsync();

            var messages = await _context.UserEvents
                .AsNoTracking()
                .Where(e => e.EventType == EventTypes.MessageSent
                         && e.UserId != null
                         && e.OccurredAtUtc >= fromUtc && e.OccurredAtUtc <= toUtc)
                .Select(e => new { UserId = e.UserId!.Value, e.PropertiesJson })
                .ToListAsync();

            var hoursToAccept = accepted
                .Select(json => ReadDoubleProperty(json, "hoursToAccept"))
                .Where(v => v.HasValue)
                .Select(v => v!.Value)
                .ToList();

            // Null rather than 0 when the property is absent: messages emitted before AN-030
            // carry no isFirstMessageToRecipient, and reporting those periods as "no
            // conversations started" would be a claim the data cannot support.
            var firstMessageFlags = messages
                .Select(m => ReadBoolProperty(m.PropertiesJson, "isFirstMessageToRecipient"))
                .ToList();

            int? conversationsStarted = firstMessageFlags.Any(f => f.HasValue)
                ? firstMessageFlags.Count(f => f == true)
                : null;

            var sendersBySender = sent.GroupBy(s => s).ToList();

            var earliest = await _context.UserEvents
                .AsNoTracking()
                .Where(e => e.EventType == EventTypes.FriendRequestSent || e.EventType == EventTypes.MessageSent)
                .OrderBy(e => e.OccurredAtUtc)
                .Select(e => (DateTime?)e.OccurredAtUtc)
                .FirstOrDefaultAsync();

            return new SocialGraphDto
            {
                From = fromUtc,
                To = toUtc,
                FriendRequestsSent = sent.Count,
                FriendRequestsAccepted = accepted.Count,
                AcceptanceRatePercent = sent.Count > 0
                    ? Math.Round((double)accepted.Count / sent.Count * 100, 2)
                    : null,
                MedianHoursToAccept = Percentile(hoursToAccept, 0.50),
                DistinctSenders = sendersBySender.Count,
                // Concentration guard: without it, one prolific sender can move the acceptance
                // rate and make a spam wave look like healthy social activity.
                MaxRequestsBySingleSender = sendersBySender.Count > 0 ? sendersBySender.Max(g => g.Count()) : 0,
                MessagesSent = messages.Count,
                DistinctMessageSenders = messages.Select(m => m.UserId).Distinct().Count(),
                ConversationsStarted = conversationsStarted,
                DataAvailableFromUtc = earliest
            };
        }

        // ─────────────────────────────────────────────────────────────────────
        // AN-037 / M-702 — MBTI as four dichotomies, not sixteen types
        // ─────────────────────────────────────────────────────────────────────
        public async Task<MbtiAnalysisDto> GetMbtiDichotomyAnalysisAsync(DateTime fromUtc, DateTime toUtc)
        {
            var users = await _context.Users
                .AsNoTracking()
                .Select(u => new { u.Id, u.MBTI })
                .ToListAsync();

            var withMbti = users
                .Where(u => !string.IsNullOrWhiteSpace(u.MBTI) && u.MBTI!.Trim().Length == 4)
                .Select(u => new { u.Id, Type = u.MBTI!.Trim().ToUpperInvariant() })
                .ToList();

            // Speakers come from mic_activated, host-excluded — the same definition M-101 uses,
            // so the two cannot disagree about who spoke.
            var speakerIds = await _context.UserEvents
                .AsNoTracking()
                .Where(e => e.EventType == EventTypes.MicActivated
                         && e.UserId != null
                         && e.OccurredAtUtc >= fromUtc && e.OccurredAtUtc <= toUtc)
                .Where(e => e.RoomId == null
                         || !_context.Rooms.Any(r => r.Id == e.RoomId && r.HostId == e.UserId))
                .Select(e => e.UserId!.Value)
                .Distinct()
                .ToListAsync();

            var speakers = speakerIds.ToHashSet();

            // Only users who actually joined a room in the window belong in the denominator:
            // someone who never showed up cannot be said to have declined to speak.
            var joinerIds = await _context.UserEvents
                .AsNoTracking()
                .Where(e => e.EventType == EventTypes.RoomJoined
                         && e.UserId != null
                         && e.OccurredAtUtc >= fromUtc && e.OccurredAtUtc <= toUtc)
                .Where(e => e.RoomId == null
                         || !_context.Rooms.Any(r => r.Id == e.RoomId && r.HostId == e.UserId))
                .Select(e => e.UserId!.Value)
                .Distinct()
                .ToListAsync();

            var joiners = joinerIds.ToHashSet();
            var population = withMbti.Where(u => joiners.Contains(u.Id)).ToList();

            var dichotomies = new[]
            {
                (Index: 0, Name: "E/I", Left: 'E', Right: 'I'),
                (Index: 1, Name: "S/N", Left: 'S', Right: 'N'),
                (Index: 2, Name: "T/F", Left: 'T', Right: 'F'),
                (Index: 3, Name: "J/P", Left: 'J', Right: 'P')
            };

            var results = dichotomies.Select(d =>
            {
                var left = population.Where(u => u.Type[d.Index] == d.Left).ToList();
                var right = population.Where(u => u.Type[d.Index] == d.Right).ToList();

                var leftSpoke = left.Count(u => speakers.Contains(u.Id));
                var rightSpoke = right.Count(u => speakers.Contains(u.Id));

                double? leftRate = left.Count > 0 ? Math.Round((double)leftSpoke / left.Count * 100, 2) : null;
                double? rightRate = right.Count > 0 ? Math.Round((double)rightSpoke / right.Count * 100, 2) : null;

                return new MbtiDichotomyStatDto
                {
                    Dichotomy = d.Name,
                    LeftTrait = d.Left.ToString(),
                    LeftUsers = left.Count,
                    LeftUsersWhoSpoke = leftSpoke,
                    LeftSpeakingRatePercent = leftRate,
                    RightTrait = d.Right.ToString(),
                    RightUsers = right.Count,
                    RightUsersWhoSpoke = rightSpoke,
                    RightSpeakingRatePercent = rightRate,
                    DifferencePercentagePoints = leftRate.HasValue && rightRate.HasValue
                        ? Math.Round(leftRate.Value - rightRate.Value, 2)
                        : null
                };
            }).ToList();

            return new MbtiAnalysisDto
            {
                From = fromUtc,
                To = toUtc,
                UsersWithMbti = population.Count,
                UsersWithoutMbti = joiners.Count - population.Count,
                Dichotomies = results
            };
        }

        // ─────────────────────────────────────────────────────────────────────
        // AN-038 / M-103 — weekly cohort retention grid
        // ─────────────────────────────────────────────────────────────────────
        public async Task<CohortGridDto> GetCohortGridAsync(DateTime fromUtc, DateTime toUtc)
        {
            var joins = await _context.UserEvents
                .AsNoTracking()
                .Where(e => e.EventType == EventTypes.RoomJoined
                         && e.UserId != null
                         && e.OccurredAtUtc >= fromUtc && e.OccurredAtUtc <= toUtc)
                .Where(e => e.RoomId == null
                         || !_context.Rooms.Any(r => r.Id == e.RoomId && r.HostId == e.UserId))
                .Select(e => new { UserId = e.UserId!.Value, e.OccurredAtUtc })
                .ToListAsync();

            if (joins.Count == 0)
            {
                return new CohortGridDto { From = fromUtc, To = toUtc };
            }

            var weeksByUser = joins
                .GroupBy(j => j.UserId)
                .ToDictionary(g => g.Key, g => g.Select(j => StartOfWeekUtc(j.OccurredAtUtc)).Distinct().ToHashSet());

            var allWeeks = joins.Select(j => StartOfWeekUtc(j.OccurredAtUtc)).Distinct().OrderBy(w => w).ToList();
            var lastWeek = allWeeks.Last();

            // A cohort is defined by a user's FIRST join week, so each user appears in exactly
            // one row. Bucketing by every week they were active would double-count them and
            // make the grid read far better than reality.
            var firstWeekByUser = weeksByUser.ToDictionary(kv => kv.Key, kv => kv.Value.Min());

            var rows = new List<CohortGridRowDto>();

            foreach (var cohortWeek in allWeeks)
            {
                var cohort = firstWeekByUser.Where(kv => kv.Value == cohortWeek).Select(kv => kv.Key).ToList();
                if (cohort.Count == 0)
                {
                    continue;
                }

                var weeksAvailable = (int)((lastWeek - cohortWeek).TotalDays / 7);
                var retention = new List<double?>();

                for (var offset = 0; offset <= weeksAvailable; offset++)
                {
                    var targetWeek = cohortWeek.AddDays(offset * 7);
                    var active = cohort.Count(userId => weeksByUser[userId].Contains(targetWeek));
                    retention.Add(Math.Round((double)active / cohort.Count * 100, 2));
                }

                rows.Add(new CohortGridRowDto
                {
                    CohortWeekStartUtc = cohortWeek,
                    CohortSize = cohort.Count,
                    WeeklyRetentionPercent = retention
                });
            }

            var weeksOfHistory = allWeeks.Count;

            return new CohortGridDto
            {
                From = fromUtc,
                To = toUtc,
                Cohorts = rows,
                WeeksOfHistory = weeksOfHistory,
                // 8 weeks is the gate from the rollout plan. Below it the grid is too sparse to
                // read as a trend, and drawing a curve through three points invites a confident
                // conclusion the data cannot support.
                HasSufficientHistory = weeksOfHistory >= 8,
                DataAvailableFromUtc = joins.Min(j => j.OccurredAtUtc)
            };
        }

        private static double? ReadDoubleProperty(string? propertiesJson, string name)
        {
            if (string.IsNullOrWhiteSpace(propertiesJson))
            {
                return null;
            }

            try
            {
                using var document = JsonDocument.Parse(propertiesJson);
                return document.RootElement.TryGetProperty(name, out var element)
                       && element.TryGetDouble(out var value)
                    ? value
                    : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static bool? ReadBoolProperty(string? propertiesJson, string name)
        {
            if (string.IsNullOrWhiteSpace(propertiesJson))
            {
                return null;
            }

            try
            {
                using var document = JsonDocument.Parse(propertiesJson);
                if (!document.RootElement.TryGetProperty(name, out var element))
                {
                    return null;
                }

                return element.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => null
                };
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}
