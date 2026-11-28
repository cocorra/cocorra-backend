using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cocorra.BLL.Services.TextRooms
{
    public sealed class TextRoomMaintenanceHostedService : BackgroundService
    {
        private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan PurgeInterval = TimeSpan.FromMinutes(10);

        private DateTime _lastPurgeUtc = DateTime.MinValue;

        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<TextRoomMaintenanceHostedService> _logger;

        public TextRoomMaintenanceHostedService(
            IServiceScopeFactory scopeFactory,
            ILogger<TextRoomMaintenanceHostedService> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await RunCycleAsync(stoppingToken);

                try
                {
                    await Task.Delay(TickInterval, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        private async Task RunCycleAsync(CancellationToken ct)
        {
            await RunStepAsync("SpawnDueSchedules", m => m.SpawnDueSchedulesAsync(ct), ct);
            await RunStepAsync("ProcessRoomLifecycle", m => m.ProcessRoomLifecycleAsync(ct), ct);
            await RunStepAsync("RotateDueTopics", m => m.RotateDueTopicsAsync(ct), ct);

            // Retention is measured in hours; scanning every tick would only add load.
            if (DateTime.UtcNow - _lastPurgeUtc >= PurgeInterval)
            {
                await RunStepAsync("PurgeExpiredMessages", m => m.PurgeExpiredMessagesAsync(ct), ct);
                _lastPurgeUtc = DateTime.UtcNow;
            }
        }

        // A scope (and DbContext) per step: entities left dirty by a failed SaveChanges would
        // otherwise be re-saved by the next step and fail it too.
        private async Task RunStepAsync(string stepName, Func<ITextRoomMaintenance, Task<int>> step, CancellationToken ct)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                await step(scope.ServiceProvider.GetRequiredService<ITextRoomMaintenance>());
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Isolate each maintenance step so one failure never starves the remaining jobs.
                _logger.LogError(ex, "Text room maintenance step {StepName} failed.", stepName);
            }
        }
    }
}
