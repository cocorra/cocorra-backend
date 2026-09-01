using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Cocorra.BLL.Services.EventTracking;
using Cocorra.DAL.Data;
using Cocorra.DAL.Models;
using Cocorra.DAL.Models.Analytics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Cocorra.Tests
{
    public class PipelineHardeningTests : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly IServiceProvider _serviceProvider;

        public PipelineHardeningTests()
        {
            _connection = new SqliteConnection("DataSource=:memory:");
            _connection.Open();

            var services = new ServiceCollection();
            services.AddDbContext<AppDbContext>(options => options.UseSqlite(_connection));
            _serviceProvider = services.BuildServiceProvider();

            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Database.EnsureCreated();
        }

        public void Dispose()
        {
            _connection.Dispose();
        }

        [Fact]
        public void DeterministicEventKey_YieldsStableEventId()
        {
            var queue = Channel.CreateUnbounded<UserEvent>();
            var tracker = new EventTracker(queue, NullLogger<EventTracker>.Instance, new Microsoft.AspNetCore.Http.HttpContextAccessor(), new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

            var userId = Guid.NewGuid();
            var eventKey = $"activation_completed:{userId}";

            tracker.Track(EventTypes.ActivationCompleted, userId, null, eventKey: eventKey);
            tracker.Track(EventTypes.ActivationCompleted, userId, null, eventKey: eventKey);

            Assert.True(queue.Reader.TryRead(out var evt1));
            Assert.True(queue.Reader.TryRead(out var evt2));

            Assert.NotNull(evt1);
            Assert.NotNull(evt2);
            Assert.Equal(evt1.EventId, evt2.EventId);
        }

        [Fact]
        public async Task Test31_BatchWithOneDuplicate_PersistsRemaining99Rows_WithoutFailing()
        {
            // Arrange — Seed 1 existing event in DB
            var duplicateGuid = Guid.NewGuid();
            using (var scope = _serviceProvider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.UserEvents.Add(new UserEvent
                {
                    EventId = duplicateGuid,
                    EventType = EventTypes.RoomJoined,
                    OccurredAtUtc = DateTime.UtcNow
                });
                await db.SaveChangesAsync();
            }

            // Create a 100-event batch: 1 duplicate + 99 brand new events
            var batch = new List<UserEvent>
            {
                new UserEvent { EventId = duplicateGuid, EventType = EventTypes.RoomJoined, OccurredAtUtc = DateTime.UtcNow }
            };

            for (int i = 1; i < 100; i++)
            {
                batch.Add(new UserEvent
                {
                    EventId = Guid.NewGuid(),
                    EventType = EventTypes.RoomJoined,
                    OccurredAtUtc = DateTime.UtcNow
                });
            }

            var queue = Channel.CreateUnbounded<UserEvent>();
            foreach (var evt in batch)
            {
                queue.Writer.TryWrite(evt);
            }
            queue.Writer.Complete();

            var scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();
            var options = Options.Create(new EventTrackingOptions { EventFlushBatchSize = 100, EventFlushMaxRetries = 2 });
            var flushService = new EventFlushService(queue, scopeFactory, NullLogger<EventFlushService>.Instance, options);

            // Act
            await flushService.StartAsync(CancellationToken.None);
            await Task.Delay(500); // Allow flush loop to process and exit
            await flushService.StopAsync(CancellationToken.None);

            // Assert — exactly 100 events total in DB (1 original + 99 new ones)
            using (var scope = _serviceProvider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var total = await db.UserEvents.CountAsync();
                Assert.Equal(100, total);
            }
        }
    }
}
