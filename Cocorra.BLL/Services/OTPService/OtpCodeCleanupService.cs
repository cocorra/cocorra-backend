using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cocorra.DAL.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cocorra.BLL.Services.OTPService
{
    /// <summary>
    /// Deletes OTP codes that expired or ran out of attempts without being used. Those rows can
    /// no longer verify anything; this only keeps the table small.
    /// </summary>
    public class OtpCodeCleanupService : BackgroundService
    {
        public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<OtpCodeCleanupService> _logger;

        public OtpCodeCleanupService(IServiceScopeFactory scopeFactory, ILogger<OtpCodeCleanupService> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var timer = new PeriodicTimer(Interval);
            try
            {
                while (await timer.WaitForNextTickAsync(stoppingToken))
                {
                    try
                    {
                        var deleted = await DeleteDeadCodesAsync(stoppingToken);
                        if (deleted > 0)
                            _logger.LogInformation("Deleted {Count} expired or exhausted OTP codes", deleted);
                    }
                    catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                    {
                        _logger.LogError(ex, "OTP code cleanup failed");
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Shutting down.
            }
        }

        public async Task<int> DeleteDeadCodesAsync(CancellationToken cancellationToken)
        {
            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = DateTime.UtcNow;
            return await context.OtpCodes
                .Where(o => o.ExpiresAt <= now || o.Attempts >= OtpCodeService.MaxAttemptsPerCode)
                .ExecuteDeleteAsync(cancellationToken);
        }
    }
}
