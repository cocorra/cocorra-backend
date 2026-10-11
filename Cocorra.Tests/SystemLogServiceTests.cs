using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Cocorra.BLL.Base;
using Cocorra.BLL.Services.SystemLogs;
using Cocorra.DAL.Data;
using Cocorra.DAL.DTOS.LogsDto;
using Cocorra.DAL.Models;
using Cocorra.DAL.Repository.SystemLogRepository;
using Cocorra.Tests.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cocorra.Tests;

public class SystemLogServiceTests : IDisposable
{
    private readonly SqliteTestHost _host = new();

    public void Dispose() => _host.Dispose();

    private (SystemLogService service, AppDbContext db) CreateService()
    {
        var scope = _host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var repo = new SystemLogRepository(db);
        var service = new SystemLogService(repo);
        return (service, db);
    }

    [Fact]
    public async Task GetSystemLogsAsync_LevelFilterCaseInsensitive_ReturnsOnlyMatchingLevel()
    {
        var (service, db) = CreateService();

        db.SystemLogs.AddRange(
            new SystemLog { Level = "Warning", Category = "CatA", Message = "Warn msg", TimestampUtc = DateTime.UtcNow },
            new SystemLog { Level = "Error", Category = "CatB", Message = "Error msg", TimestampUtc = DateTime.UtcNow },
            new SystemLog { Level = "Critical", Category = "CatC", Message = "Critical msg", TimestampUtc = DateTime.UtcNow }
        );
        await db.SaveChangesAsync();

        var query = new SystemLogQueryDto { Level = "error" };
        var response = await service.GetSystemLogsAsync(query);

        Assert.True(response.Succeeded);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(response.Data);

        var items = response.Data.ToList();
        Assert.Single(items);
        Assert.Equal("Error", items[0].Level);
        Assert.Equal("Error msg", items[0].Message);
    }

    [Fact]
    public async Task GetSystemLogsAsync_InvalidLevel_ReturnsBadRequest()
    {
        var (service, _) = CreateService();

        var query = new SystemLogQueryDto { Level = "Verbose" };
        var response = await service.GetSystemLogsAsync(query);

        Assert.False(response.Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Invalid level", response.Message);
    }

    [Fact]
    public async Task GetSystemLogsAsync_SearchFilter_MatchesMessageOrCategory()
    {
        var (service, db) = CreateService();

        db.SystemLogs.AddRange(
            new SystemLog { Level = "Error", Category = "AuthCategory", Message = "Token expired", TimestampUtc = DateTime.UtcNow },
            new SystemLog { Level = "Warning", Category = "PaymentCategory", Message = "Gateway timeout", TimestampUtc = DateTime.UtcNow },
            new SystemLog { Level = "Error", Category = "UploadCategory", Message = "File corrupted", TimestampUtc = DateTime.UtcNow }
        );
        await db.SaveChangesAsync();

        var messageMatch = await service.GetSystemLogsAsync(new SystemLogQueryDto { Search = "expired" });
        Assert.True(messageMatch.Succeeded);
        Assert.Single(messageMatch.Data!);
        Assert.Equal("Token expired", messageMatch.Data!.First().Message);

        var categoryMatch = await service.GetSystemLogsAsync(new SystemLogQueryDto { Search = "Payment" });
        Assert.True(categoryMatch.Succeeded);
        Assert.Single(categoryMatch.Data!);
        Assert.Equal("Gateway timeout", categoryMatch.Data!.First().Message);

        var noMatch = await service.GetSystemLogsAsync(new SystemLogQueryDto { Search = "NonExistentValue" });
        Assert.True(noMatch.Succeeded);
        Assert.Empty(noMatch.Data!);
    }

    [Fact]
    public async Task GetSystemLogsAsync_FromInclusiveAndToExclusive_FiltersCorrectBoundary()
    {
        var (service, db) = CreateService();

        var from = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 5, 1, 14, 0, 0, DateTimeKind.Utc);

        var beforeFrom = new SystemLog { Level = "Error", Category = "C", Message = "Before", TimestampUtc = from.AddHours(-1) };
        var exactFrom = new SystemLog { Level = "Error", Category = "C", Message = "ExactFrom", TimestampUtc = from };
        var middle = new SystemLog { Level = "Error", Category = "C", Message = "Middle", TimestampUtc = from.AddHours(1) };
        var exactTo = new SystemLog { Level = "Error", Category = "C", Message = "ExactTo", TimestampUtc = to };
        var afterTo = new SystemLog { Level = "Error", Category = "C", Message = "After", TimestampUtc = to.AddHours(1) };

        db.SystemLogs.AddRange(beforeFrom, exactFrom, middle, exactTo, afterTo);
        await db.SaveChangesAsync();

        var query = new SystemLogQueryDto { From = from, To = to };
        var response = await service.GetSystemLogsAsync(query);

        Assert.True(response.Succeeded);
        var items = response.Data!.ToList();

        Assert.Equal(2, items.Count);
        Assert.Contains(items, i => i.Message == "ExactFrom");
        Assert.Contains(items, i => i.Message == "Middle");
        Assert.DoesNotContain(items, i => i.Message == "Before");
        Assert.DoesNotContain(items, i => i.Message == "ExactTo");
        Assert.DoesNotContain(items, i => i.Message == "After");
    }

    [Fact]
    public async Task GetSystemLogsAsync_FromLaterThanTo_ReturnsBadRequest()
    {
        var (service, _) = CreateService();

        var query = new SystemLogQueryDto
        {
            From = DateTime.UtcNow.AddDays(1),
            To = DateTime.UtcNow
        };
        var response = await service.GetSystemLogsAsync(query);

        Assert.False(response.Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("'from' must not be later than 'to'", response.Message);
    }

    [Fact]
    public async Task GetSystemLogsAsync_Ordering_NewestFirstWithIdTiebreak()
    {
        var (service, db) = CreateService();

        var sharedTime = new DateTime(2026, 6, 1, 10, 0, 0, DateTimeKind.Utc);
        var newerTime = sharedTime.AddMinutes(10);

        var log1 = new SystemLog { Level = "Error", Category = "C", Message = "Log1", TimestampUtc = sharedTime };
        var log2 = new SystemLog { Level = "Error", Category = "C", Message = "Log2", TimestampUtc = sharedTime };
        var log3 = new SystemLog { Level = "Error", Category = "C", Message = "Log3", TimestampUtc = sharedTime };
        var logNewest = new SystemLog { Level = "Error", Category = "C", Message = "LogNewest", TimestampUtc = newerTime };

        db.SystemLogs.AddRange(log1, log2, log3, logNewest);
        await db.SaveChangesAsync();

        var response = await service.GetSystemLogsAsync(new SystemLogQueryDto());
        Assert.True(response.Succeeded);

        var items = response.Data!.ToList();
        Assert.Equal(4, items.Count);

        Assert.Equal(logNewest.Id, items[0].Id);
        Assert.Equal(log3.Id, items[1].Id);
        Assert.Equal(log2.Id, items[2].Id);
        Assert.Equal(log1.Id, items[3].Id);
    }

    [Fact]
    public async Task GetSystemLogsAsync_PaginationMetadata_CalculatesPagesCorrectlyAcrossThreePages()
    {
        var (service, db) = CreateService();

        var baseTime = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 1; i <= 5; i++)
        {
            db.SystemLogs.Add(new SystemLog
            {
                Level = "Error",
                Category = "Cat",
                Message = $"Log {i}",
                TimestampUtc = baseTime.AddMinutes(i)
            });
        }
        await db.SaveChangesAsync();

        // Page 1 of 3 (pageSize: 2)
        var page1Response = await service.GetSystemLogsAsync(new SystemLogQueryDto { PageNumber = 1, PageSize = 2 });
        var paged1 = Assert.IsAssignableFrom<PagedResponse<SystemLogDto>>(page1Response);
        Assert.Equal(5, paged1.TotalCount);
        Assert.Equal(3, paged1.TotalPages);
        Assert.Equal(1, paged1.CurrentPage);
        Assert.True(paged1.HasNextPage);
        Assert.False(paged1.HasPreviousPage);
        Assert.Equal(2, paged1.Data!.Count());

        // Page 2 of 3 (pageSize: 2)
        var page2Response = await service.GetSystemLogsAsync(new SystemLogQueryDto { PageNumber = 2, PageSize = 2 });
        var paged2 = Assert.IsAssignableFrom<PagedResponse<SystemLogDto>>(page2Response);
        Assert.Equal(5, paged2.TotalCount);
        Assert.Equal(3, paged2.TotalPages);
        Assert.Equal(2, paged2.CurrentPage);
        Assert.True(paged2.HasNextPage);
        Assert.True(paged2.HasPreviousPage);
        Assert.Equal(2, paged2.Data!.Count());

        // Page 3 of 3 (pageSize: 2)
        var page3Response = await service.GetSystemLogsAsync(new SystemLogQueryDto { PageNumber = 3, PageSize = 2 });
        var paged3 = Assert.IsAssignableFrom<PagedResponse<SystemLogDto>>(page3Response);
        Assert.Equal(5, paged3.TotalCount);
        Assert.Equal(3, paged3.TotalPages);
        Assert.Equal(3, paged3.CurrentPage);
        Assert.False(paged3.HasNextPage);
        Assert.True(paged3.HasPreviousPage);
        Assert.Single(paged3.Data!);
    }

    [Fact]
    public async Task GetSystemLogsAsync_ReturnedTimestamps_HaveUtcDateTimeKind()
    {
        var (service, db) = CreateService();

        db.SystemLogs.Add(new SystemLog
        {
            Level = "Warning",
            Category = "KindCategory",
            Message = "Utc test",
            TimestampUtc = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc)
        });
        await db.SaveChangesAsync();

        var response = await service.GetSystemLogsAsync(new SystemLogQueryDto());
        Assert.True(response.Succeeded);

        var log = Assert.Single(response.Data!);
        Assert.Equal(DateTimeKind.Utc, log.TimestampUtc.Kind);
    }
}
