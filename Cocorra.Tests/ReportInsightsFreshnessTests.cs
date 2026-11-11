using System;
using System.Threading.Tasks;
using Cocorra.BLL.Services.Analytics;
using Cocorra.BLL.Services.AnalyticsService;
using Cocorra.DAL.Data;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;
using Cocorra.DAL.Repository.AnalyticsRepository;
using Cocorra.Tests.Helpers;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace Cocorra.Tests;

public class ReportInsightsFreshnessTests : IDisposable
{
    private readonly SqliteTestHost _host = new();

    public void Dispose() => _host.Dispose();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReportSubmittedAfterFirstCall_AppearsInSecondCallTotalReports(bool explicitBounds)
    {
        var reporterId = Guid.NewGuid();

        using (var seedScope = _host.CreateScope())
        {
            var db = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Users.Add(User(reporterId));
            db.Reports.Add(Report(reporterId, DateTime.UtcNow));
            await db.SaveChangesAsync();
        }

        DateTime? from = explicitBounds ? DateTime.UtcNow.AddDays(-1) : null;
        DateTime? to = explicitBounds ? DateTime.UtcNow.AddHours(1) : null;

        using var readScope = _host.CreateScope();
        var readDb = readScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = new AnalyticsService(
            new AnalyticsRepository(readDb),
            new MemoryCache(new MemoryCacheOptions()),
            new MetricRegistry());

        var first = (await service.GetReportInsightsAsync(from, to)).Data!;
        Assert.Equal(1, first.TotalReports);
        Assert.Equal(1, first.OpenReports);

        using (var insertScope = _host.CreateScope())
        {
            var db = insertScope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Reports.Add(Report(reporterId, DateTime.UtcNow));
            await db.SaveChangesAsync();
        }

        var second = (await service.GetReportInsightsAsync(from, to)).Data!;
        Assert.Equal(2, second.TotalReports);
        Assert.Equal(2, second.OpenReports);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReportSubmittedAfterFirstCall_AppearsInSecondCallReportRateTotal(bool explicitBounds)
    {
        var reporterId = Guid.NewGuid();

        using (var seedScope = _host.CreateScope())
        {
            var db = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Users.Add(User(reporterId));
            db.Reports.Add(Report(reporterId, DateTime.UtcNow));
            await db.SaveChangesAsync();
        }

        DateTime? from = explicitBounds ? DateTime.UtcNow.AddDays(-1) : null;
        DateTime? to = explicitBounds ? DateTime.UtcNow.AddHours(1) : null;

        using var readScope = _host.CreateScope();
        var readDb = readScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = new AnalyticsService(
            new AnalyticsRepository(readDb),
            new MemoryCache(new MemoryCacheOptions()),
            new MetricRegistry());

        var first = (await service.GetReportRateByCategoryAsync(from, to)).Data!;
        Assert.Equal(1, first.TotalReports);
        Assert.Equal(1, first.ReportsWithoutRoomContext);

        using (var insertScope = _host.CreateScope())
        {
            var db = insertScope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Reports.Add(Report(reporterId, DateTime.UtcNow));
            await db.SaveChangesAsync();
        }

        var second = (await service.GetReportRateByCategoryAsync(from, to)).Data!;
        Assert.Equal(2, second.TotalReports);
        Assert.Equal(2, second.ReportsWithoutRoomContext);
    }

    private static ApplicationUser User(Guid id) => new()
    {
        Id = id,
        UserName = id.ToString("N"),
        FirstName = "Test",
        LastName = "User",
        CreatedAt = DateTime.UtcNow
    };

    private static Report Report(Guid reporterId, DateTime createdAt) => new()
    {
        Id = Guid.NewGuid(),
        ReporterId = reporterId,
        Category = ReportCategory.Spam,
        Description = "Report submitted during the test",
        Status = "Open",
        CreatedAt = createdAt
    };
}
