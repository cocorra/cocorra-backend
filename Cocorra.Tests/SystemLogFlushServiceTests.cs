using System;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Cocorra.BLL.Services.SystemLogs;
using Cocorra.DAL.Data;
using Cocorra.DAL.Models;
using Cocorra.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Cocorra.Tests;

public class SystemLogFlushServiceTests : IDisposable
{
    private readonly SqliteTestHost _host = new();
    private readonly Mock<ILogger<SystemLogFlushService>> _loggerMock = new();

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task ExecuteAsync_EnqueuedEntriesPersistedAfterStartCompleteWriterAndStop()
    {
        var channel = Channel.CreateBounded<SystemLog>(100);
        var scopeFactory = _host.Services.GetRequiredService<IServiceScopeFactory>();
        var options = Options.Create(new SystemLogOptions { BatchSize = 10 });
        var flushService = new SystemLogFlushService(channel, scopeFactory, _loggerMock.Object, options);

        var log1 = new SystemLog
        {
            Level = "Warning",
            Category = "PaymentService",
            Message = "Slow response detected",
            TimestampUtc = DateTime.UtcNow
        };
        var log2 = new SystemLog
        {
            Level = "Error",
            Category = "AuthService",
            Message = "Invalid credentials attempt",
            TimestampUtc = DateTime.UtcNow
        };

        channel.Writer.TryWrite(log1);
        channel.Writer.TryWrite(log2);

        await flushService.StartAsync(CancellationToken.None);
        channel.Writer.Complete();
        await flushService.StopAsync(CancellationToken.None);

        using var scope = _host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        for (var i = 0; i < 50 && await db.SystemLogs.CountAsync() < 2; i++)
        {
            await Task.Yield();
        }

        var persistedLogs = await db.SystemLogs.AsNoTracking().OrderBy(l => l.Message).ToListAsync();

        Assert.Equal(2, persistedLogs.Count);
        Assert.Equal("Invalid credentials attempt", persistedLogs[0].Message);
        Assert.Equal("Slow response detected", persistedLogs[1].Message);
    }

    [Fact]
    public async Task ExecuteAsync_MultipleBatchesEnqueued_FlushesAllEntriesOnShutdownDrain()
    {
        var channel = Channel.CreateBounded<SystemLog>(100);
        var scopeFactory = _host.Services.GetRequiredService<IServiceScopeFactory>();
        var options = Options.Create(new SystemLogOptions { BatchSize = 2 });
        var flushService = new SystemLogFlushService(channel, scopeFactory, _loggerMock.Object, options);

        for (var i = 1; i <= 5; i++)
        {
            channel.Writer.TryWrite(new SystemLog
            {
                Level = "Error",
                Category = "BatchCategory",
                Message = $"Queued entry {i}",
                TimestampUtc = DateTime.UtcNow
            });
        }

        await flushService.StartAsync(CancellationToken.None);
        channel.Writer.Complete();
        await flushService.StopAsync(CancellationToken.None);

        using var scope = _host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        for (var i = 0; i < 50 && await db.SystemLogs.CountAsync() < 5; i++)
        {
            await Task.Yield();
        }

        var persistedCount = await db.SystemLogs.CountAsync();
        Assert.Equal(5, persistedCount);
    }
}
