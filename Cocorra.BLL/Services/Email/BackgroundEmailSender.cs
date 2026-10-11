using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cocorra.BLL.Services.Email
{
    /// <summary>
    /// Sends the emails queued in <see cref="EmailQueue"/>, one at a time. A failed send is
    /// logged and skipped; it is not retried.
    /// </summary>
    public class BackgroundEmailSender : BackgroundService
    {
        private readonly EmailQueue _queue;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<BackgroundEmailSender> _logger;

        public BackgroundEmailSender(EmailQueue queue, IServiceScopeFactory scopeFactory, ILogger<BackgroundEmailSender> logger)
        {
            _queue = queue;
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                await foreach (var email in _queue.Reader.ReadAllAsync(stoppingToken))
                {
                    try
                    {
                        using var scope = _scopeFactory.CreateScope();
                        await email.Send(scope.ServiceProvider, stoppingToken);
                    }
                    catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                    {
                        _logger.LogError(ex, "Failed to send {EmailDescription}", email.Description);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Shutting down.
            }
        }
    }
}
