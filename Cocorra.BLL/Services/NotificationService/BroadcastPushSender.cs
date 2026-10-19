using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cocorra.BLL.Services.NotificationService
{
    /// <summary>
    /// Sends the pushes queued in <see cref="BroadcastPushQueue"/>, one job at a time. Each push goes
    /// through <see cref="IPushNotificationService"/>, which logs and tracks every outcome and never
    /// throws, so one bad token cannot stop the rest of the broadcast.
    /// </summary>
    public class BroadcastPushSender : BackgroundService
    {
        /// <summary>Concurrent FCM calls per broadcast; each is a separate HTTPS request.</summary>
        internal const int MaxConcurrentSends = 8;

        private readonly BroadcastPushQueue _queue;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<BroadcastPushSender> _logger;

        public BroadcastPushSender(BroadcastPushQueue queue, IServiceScopeFactory scopeFactory, ILogger<BroadcastPushSender> logger)
        {
            _queue = queue;
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                await foreach (var job in _queue.Reader.ReadAllAsync(stoppingToken))
                {
                    try
                    {
                        await SendAsync(job, stoppingToken);
                    }
                    catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                    {
                        _logger.LogError(ex, "Broadcast {BroadcastId} push run failed.", job.BroadcastId);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Shutting down.
            }
        }

        internal async Task SendAsync(BroadcastPushJob job, CancellationToken cancellationToken)
        {
            using var scope = _scopeFactory.CreateScope();
            var pushService = scope.ServiceProvider.GetRequiredService<IPushNotificationService>();

            await Parallel.ForEachAsync(
                job.Recipients,
                new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrentSends, CancellationToken = cancellationToken },
                async (recipient, _) =>
                {
                    // A fresh dictionary per recipient: userId differs, and it is what ties
                    // PushNotificationService's tracked attempt and result to the user.
                    var data = new Dictionary<string, string>
                    {
                        { "type", "general" },
                        { "broadcastId", job.BroadcastId.ToString() },
                        { "userId", recipient.UserId.ToString() }
                    };

                    await pushService.SendPushNotificationAsync(recipient.FcmToken, job.Title, job.Message, data);
                });

            _logger.LogInformation(
                "Broadcast {BroadcastId} push run finished for {RecipientCount} device(s).",
                job.BroadcastId, job.Recipients.Count);
        }
    }
}
