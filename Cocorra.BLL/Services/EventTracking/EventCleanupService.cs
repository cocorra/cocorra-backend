using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cocorra.DAL.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cocorra.BLL.Services.EventTracking
{
    public class EventCleanupService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<EventCleanupService> _logger;

        public EventCleanupService(IServiceScopeFactory scopeFactory, ILogger<EventCleanupService> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using (var scope = _scopeFactory.CreateScope())
                    {
                        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                        var cutoff = DateTime.UtcNow.AddDays(-180);

                        int deletedCount = await db.UserEvents
                            .Where(e => e.OccurredAtUtc < cutoff)
                            .ExecuteDeleteAsync(ct);

                        if (deletedCount > 0)
                        {
                            _logger.LogInformation("EventCleanupService: Purged {DeletedCount} expired events older than {Cutoff}", deletedCount, cutoff);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "EventCleanupService: Error occurred during user event purge cycle.");
                }

                try
                {
                    // Delay for 24 hours
                    await Task.Delay(TimeSpan.FromHours(24), ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }
}
