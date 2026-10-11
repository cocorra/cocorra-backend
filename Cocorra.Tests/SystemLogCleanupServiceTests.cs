using System;
using System.Linq;
using System.Threading;
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

public class SystemLogCleanupServiceTests : IDisposable
{
    private readonly SqliteTestHost _host = new();
    private readonly Mock<ILogger<SystemLogCleanupService>> _loggerMock = new();

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task PerformBatchedPurgeAsync_DeletesRowsOlderThanRetentionDaysAndKeepsNewerRows()
    {
        var scopeFactory = _host.Services.GetRequiredService<IServiceScopeFactory>();
        var options = Options.Create(new SystemLogOptions { RetentionDays = 14 });
        var cleanupService = new SystemLogCleanupService(scopeFactory, _loggerMock.Object, options);

        using (var scope = _host.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var expiredLog1 = new SystemLog
            {
                Level = "Warning",
                Category = "OldCat1",
                Message = "Expired log 1",
                TimestampUtc = DateTime.UtcNow.AddDays(-20)
            };
            var expiredLog2 = new SystemLog
            {
                Level = "Error",
                Category = "OldCat2",
                Message = "Expired log 2",
                TimestampUtc = DateTime.UtcNow.AddDays(-15)
            };
            var retainedLog1 = new SystemLog
            {
                Level = "Error",
                Category = "NewCat1",
                Message = "Retained log 1",
                TimestampUtc = DateTime.UtcNow.AddDays(-10)
            };
            var retainedLog2 = new SystemLog
            {
                Level = "Critical",
                Category = "NewCat2",
                Message = "Retained log 2",
                TimestampUtc = DateTime.UtcNow.AddHours(-1)
            };

            db.SystemLogs.AddRange(expiredLog1, expiredLog2, retainedLog1, retainedLog2);
            await db.SaveChangesAsync();
        }

        var deletedCount = await cleanupService.PerformBatchedPurgeAsync(CancellationToken.None);

        Assert.Equal(2, deletedCount);

        using (var verifyScope = _host.CreateScope())
        {
            var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var remainingLogs = await verifyDb.SystemLogs.AsNoTracking().OrderBy(l => l.Message).ToListAsync();

            Assert.Equal(2, remainingLogs.Count);
            Assert.Equal("Retained log 1", remainingLogs[0].Message);
            Assert.Equal("Retained log 2", remainingLogs[1].Message);
        }
    }
}
