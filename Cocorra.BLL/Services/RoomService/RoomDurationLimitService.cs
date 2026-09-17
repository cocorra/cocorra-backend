using System;
using System.Threading;
using System.Threading.Tasks;
using Cocorra.BLL.Services.RealTimeNotifier;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cocorra.BLL.Services.RoomService
{
    /// <summary>
    /// Closes rooms that have run past the duration they were booked for.
    ///
    /// <para>
    /// DurationHours was validated at creation — it must be 2 or 3 — and then never enforced by
    /// anything. A host who closed the app without ending their room left it Live forever: it
    /// stayed in the feed, kept accepting joins, and kept minting LiveKit tokens. It is also the
    /// only way a participant's token can expire while they are still in a session, because the
    /// TTL is deliberately longer than any bookable duration. Capping the room is what makes
    /// that guarantee hold, and therefore what makes reducing the TTL safe.
    /// </para>
    ///
    /// <para>
    /// Separate from HostReconnectGraceService rather than folded into its loop: the two
    /// enforce unrelated policies on very different timescales — 90 seconds against three hours
    /// — and a failure in this sweep must not stop abandoned rooms being reaped by that one.
    /// </para>
    /// </summary>
    public class RoomDurationLimitService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<RoomDurationLimitService> _logger;
        private readonly RoomLifecycleSettings _settings;

        public RoomDurationLimitService(
            IServiceScopeFactory scopeFactory,
            ILogger<RoomDurationLimitService> logger,
            IOptions<RoomLifecycleSettings>? settings = null)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
            _settings = settings?.Value ?? new RoomLifecycleSettings();
        }

        protected override async Task ExecuteAsync(CancellationToken ct)
        {
            var overtime = TimeSpan.FromMinutes(Math.Max(0, _settings.RoomOvertimeGraceMinutes));
            var interval = TimeSpan.FromSeconds(Math.Max(1, _settings.DurationSweepIntervalSeconds));

            _logger.LogInformation(
                "RoomDurationLimitService started. Overtime allowance {OvertimeMinutes}m, swept every {IntervalSeconds}s.",
                overtime.TotalMinutes, interval.TotalSeconds);

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await SweepAsync(overtime, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Never let one bad cycle kill the loop — every overdue room after it would
                    // then stay Live for good, which is the bug this service exists to fix.
                    _logger.LogError(ex, "RoomDurationLimitService: sweep failed; will retry next interval.");
                }

                try
                {
                    await Task.Delay(interval, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        /// <summary>
        /// Ends every room past its duration and tells the participants. Returns how many were
        /// ended. Public so a test can drive one cycle without running the hosted loop.
        /// </summary>
        public async Task<int> SweepAsync(TimeSpan overtimeAllowance, CancellationToken ct = default)
        {
            using var scope = _scopeFactory.CreateScope();
            var roomService = scope.ServiceProvider.GetRequiredService<IRoomService>();
            var notifier = scope.ServiceProvider.GetRequiredService<IRealTimeNotifier>();

            var endedRoomIds = await roomService.EndRoomsPastScheduledDurationAsync(
                DateTime.UtcNow, overtimeAllowance);

            foreach (var roomId in endedRoomIds)
            {
                if (ct.IsCancellationRequested)
                    break;

                await notifier.RoomEndedAsync(
                    roomId, "This session has reached its scheduled length and has ended.");

                _logger.LogInformation(
                    "RoomDurationLimitService: ended room {RoomId} — past its booked duration " +
                    "plus {OvertimeMinutes}m overtime.",
                    roomId, overtimeAllowance.TotalMinutes);
            }

            return endedRoomIds.Count;
        }
    }
}
