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
    /// Closes rooms whose host dropped and never came back.
    ///
    /// <para>
    /// A dropped host connection used to end the room immediately, from inside
    /// RoomHub.OnDisconnectedAsync — so a lift, a tunnel or a Wi-Fi handoff killed the session
    /// for everyone, permanently, since an Ended room refuses all rejoins. The hub now only
    /// stamps Room.HostDisconnectedAt and the room stays Live; this service is what eventually
    /// ends it, once the grace window has genuinely expired.
    /// </para>
    ///
    /// <para>
    /// Server-side and database-backed on purpose. The obvious alternative, a timer started in
    /// the hub, dies with the process: a deploy mid-window would leave rooms Live with an absent
    /// host and nothing to close them. A timestamp plus this sweep also recovers a backlog after
    /// a restart, and is the one part of the design that already works if the API is ever run on
    /// more than one instance.
    /// </para>
    /// </summary>
    public class HostReconnectGraceService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<HostReconnectGraceService> _logger;
        private readonly RoomLifecycleSettings _settings;

        public HostReconnectGraceService(
            IServiceScopeFactory scopeFactory,
            ILogger<HostReconnectGraceService> logger,
            IOptions<RoomLifecycleSettings>? settings = null)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
            _settings = settings?.Value ?? new RoomLifecycleSettings();
        }

        protected override async Task ExecuteAsync(CancellationToken ct)
        {
            var grace = TimeSpan.FromSeconds(Math.Max(1, _settings.HostReconnectGraceSeconds));
            var interval = TimeSpan.FromSeconds(Math.Max(1, _settings.GraceSweepIntervalSeconds));

            _logger.LogInformation(
                "HostReconnectGraceService started. Grace window {GraceSeconds}s, swept every {IntervalSeconds}s.",
                grace.TotalSeconds, interval.TotalSeconds);

            // No startup delay, unlike the analytics services: a room abandoned just before a
            // deploy is already past its deadline when the process comes back, and its audience
            // is waiting on this sweep to be told.
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await SweepAsync(grace, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Never let one bad cycle kill the loop — every subsequent abandoned room
                    // would then stay Live forever.
                    _logger.LogError(ex, "HostReconnectGraceService: sweep failed; will retry next interval.");
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
        /// Ends every room past its deadline and tells the participants. Returns how many were
        /// ended. Public so a test can drive one cycle without running the hosted loop.
        /// </summary>
        public async Task<int> SweepAsync(TimeSpan grace, CancellationToken ct = default)
        {
            var cutoff = DateTime.UtcNow - grace;

            using var scope = _scopeFactory.CreateScope();
            var roomService = scope.ServiceProvider.GetRequiredService<IRoomService>();
            var notifier = scope.ServiceProvider.GetRequiredService<IRealTimeNotifier>();

            var endedRoomIds = await roomService.EndRoomsWithExpiredHostGraceAsync(cutoff);

            foreach (var roomId in endedRoomIds)
            {
                if (ct.IsCancellationRequested)
                    break;

                await notifier.RoomEndedAsync(
                    roomId, "The host lost connection and did not return. This room has ended.");

                _logger.LogInformation(
                    "HostReconnectGraceService: ended room {RoomId} — host did not reconnect within {GraceSeconds}s.",
                    roomId, grace.TotalSeconds);
            }

            return endedRoomIds.Count;
        }
    }
}
