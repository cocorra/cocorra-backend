using Cocorra.DAL.DTOS.AnalyticsDto;
using Cocorra.DAL.Enums;
using Microsoft.EntityFrameworkCore;

namespace Cocorra.DAL.Repository.AnalyticsRepository
{
    /// <summary>
    /// AN-020 — Supply Health.
    ///
    /// Every figure here comes from Rooms, which is relational, verified by the audit, and
    /// never purged by the 180-day event retention. No new events and no schema change were
    /// needed for any of it — the platform's most consequential unwatched question was simply
    /// never asked.
    /// </summary>
    public partial class AnalyticsRepository
    {
        public async Task<SupplyHealthDto> GetSupplyHealthAsync(
            string granularity,
            DateTime fromUtc,
            DateTime toUtc)
        {
            var isMonthly = granularity.Equals("monthly", StringComparison.OrdinalIgnoreCase);

            var roomsInWindow = await _context.Rooms
                .AsNoTracking()
                .Where(r => r.CreatedAt >= fromUtc && r.CreatedAt <= toUtc)
                .Select(r => new
                {
                    r.Id,
                    r.HostId,
                    r.CreatedAt,
                    r.StartDate,
                    r.Status
                })
                .ToListAsync();

            if (roomsInWindow.Count == 0)
            {
                return new SupplyHealthDto { From = fromUtc, To = toUtc, Granularity = granularity.ToLower() };
            }

            // ── B-1: distinct active hosts per period ───────────────────────
            var activeHosts = roomsInWindow
                .GroupBy(r => isMonthly
                    ? new DateTime(r.CreatedAt.Year, r.CreatedAt.Month, 1)
                    : r.CreatedAt.Date)
                .OrderBy(g => g.Key)
                .Select(g => new ActiveHostsDataPointDto
                {
                    Period = isMonthly ? g.Key.ToString("yyyy-MM") : g.Key.ToString("yyyy-MM-dd"),
                    DistinctHosts = g.Select(r => r.HostId).Distinct().Count(),
                    RoomsCreated = g.Count(),
                    RoomsGoneLive = g.Count(r => r.Status != RoomStatus.Scheduled)
                })
                .ToList();

            // ── B-2: host retention (does a first-time host come back?) ─────
            // "First room" is evaluated over ALL history, not just the window: a host whose
            // first room predates the window is not a new host, and counting them as one would
            // depress the rate for no reason.
            var hostIdsInWindow = roomsInWindow.Select(r => r.HostId).Distinct().ToList();

            var hostRoomDates = await _context.Rooms
                .AsNoTracking()
                .Where(r => hostIdsInWindow.Contains(r.HostId))
                .Select(r => new { r.HostId, r.CreatedAt })
                .ToListAsync();

            var roomsByHost = hostRoomDates
                .GroupBy(r => r.HostId)
                .ToDictionary(g => g.Key, g => g.Select(x => x.CreatedAt).OrderBy(d => d).ToList());

            var newHosts = roomsByHost
                .Where(kv => kv.Value[0] >= fromUtc && kv.Value[0] <= toUtc)
                .ToList();

            var withSecond = newHosts.Where(kv => kv.Value.Count > 1).ToList();

            var daysToSecond = withSecond
                .Select(kv => (kv.Value[1] - kv.Value[0]).TotalDays)
                .ToList();

            var hostRetention = new HostRetentionDto
            {
                NewHosts = newHosts.Count,
                HostsWithSecondRoom = withSecond.Count,
                SecondRoomRatePercent = newHosts.Count > 0
                    ? Math.Round((double)withSecond.Count / newHosts.Count * 100, 2)
                    : 0,
                MedianDaysToSecondRoom = Percentile(daysToSecond, 0.50)
            };

            // ── B-2: concentration ──────────────────────────────────────────
            var roomsPerHost = roomsInWindow
                .GroupBy(r => r.HostId)
                .Select(g => g.Count())
                .OrderByDescending(c => c)
                .ToList();

            var totalRooms = roomsInWindow.Count;
            var half = totalRooms / 2.0;
            var running = 0;
            var hostsCoveringHalf = 0;

            foreach (var count in roomsPerHost)
            {
                running += count;
                hostsCoveringHalf++;
                if (running >= half)
                {
                    break;
                }
            }

            var concentration = new HostConcentrationDto
            {
                TotalHostsInWindow = roomsPerHost.Count,
                TotalRoomsInWindow = totalRooms,
                TopHostSharePercent = Math.Round((double)roomsPerHost[0] / totalRooms * 100, 2),
                Top3SharePercent = Math.Round((double)roomsPerHost.Take(3).Sum() / totalRooms * 100, 2),
                HostsCoveringHalfOfRooms = hostsCoveringHalf
            };

            // ── B-3: when rooms actually run ────────────────────────────────
            var schedule = roomsInWindow
                .GroupBy(r => r.StartDate.Hour)
                .OrderBy(g => g.Key)
                .Select(g => new HostSchedulePointDto { HourUtc = g.Key, RoomsStarted = g.Count() })
                .ToList();

            var earliestRoom = await _context.Rooms
                .AsNoTracking()
                .OrderBy(r => r.CreatedAt)
                .Select(r => (DateTime?)r.CreatedAt)
                .FirstOrDefaultAsync();

            return new SupplyHealthDto
            {
                From = fromUtc,
                To = toUtc,
                Granularity = granularity.ToLower(),
                ActiveHosts = activeHosts,
                HostRetention = hostRetention,
                Concentration = concentration,
                ScheduleByHourUtc = schedule,
                DataAvailableFromUtc = earliestRoom
            };
        }
    }
}
