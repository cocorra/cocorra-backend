using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Cocorra.DAL.Data;
using Cocorra.DAL.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cocorra.BLL.Services.EventTracking
{
    public class EventFlushService : BackgroundService
    {
        private readonly Channel<UserEvent> _queue;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<EventFlushService> _logger;

        public EventFlushService(
            Channel<UserEvent> queue, 
            IServiceScopeFactory scopeFactory, 
            ILogger<EventFlushService> logger)
        {
            _queue = queue;
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken ct)
        {
            var batch = new List<UserEvent>(capacity: 100);

            try
            {
                while (await _queue.Reader.WaitToReadAsync(ct))
                {
                    while (batch.Count < 100 && _queue.Reader.TryRead(out var evt))
                    {
                        batch.Add(evt);
                    }

                    if (batch.Count == 0)
                    {
                        continue;
                    }

                    try
                    {
                        using var scope = _scopeFactory.CreateScope();
                        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                        db.UserEvents.AddRange(batch);
                        await db.SaveChangesAsync(ct);
                    }
                    catch (Exception dbEx)
                    {
                        _logger.LogError(dbEx, "Failed to persist batch of {BatchCount} user events.", batch.Count);
                    }
                    finally
                    {
                        batch.Clear();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("EventFlushService is stopping.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in EventFlushService execution loop.");
            }
        }
    }
}
