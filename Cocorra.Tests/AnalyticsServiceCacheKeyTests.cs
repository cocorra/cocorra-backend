using System;
using System.Text.Json;
using System.Threading.Tasks;
using Cocorra.BLL.Services.Analytics;
using Cocorra.BLL.Services.AnalyticsService;
using Cocorra.DAL.DTOS.AnalyticsDto;
using Cocorra.DAL.Repository.AnalyticsRepository;
using Cocorra.DAL.Time;
using Microsoft.Extensions.Caching.Memory;
using Moq;
using Xunit;

namespace Cocorra.Tests;

public class AnalyticsServiceCacheKeyTests
{
    private static DateTime Utc(int year, int month, int day, int hour, int minute = 0) =>
        new(year, month, day, hour, minute, 0, DateTimeKind.Utc);

    private static (AnalyticsService Service, Mock<IAnalyticsRepository> Repository) CreateService()
    {
        var repository = new Mock<IAnalyticsRepository>();
        repository
            .Setup(r => r.GetParticipationStatsAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(new ParticipationStatsDto());

        var service = new AnalyticsService(
            repository.Object,
            new MemoryCache(new MemoryCacheOptions()),
            new MetricRegistry());

        return (service, repository);
    }

    [Fact]
    public async Task DifferentToBoundsWithinTheSameHour_DoNotShareACacheEntry()
    {
        var (service, repository) = CreateService();
        var from = Utc(2026, 11, 3, 0, 0);

        await service.GetParticipationStatsAsync(from, Utc(2026, 11, 11, 10, 5));
        await service.GetParticipationStatsAsync(from, Utc(2026, 11, 11, 10, 55));

        repository.Verify(
            r => r.GetParticipationStatsAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task IdenticalExplicitBounds_ShareOneCacheEntry()
    {
        var (service, repository) = CreateService();
        var from = Utc(2026, 11, 2, 8, 15);
        var to = Utc(2026, 11, 2, 9, 45);

        await service.GetParticipationStatsAsync(from, to);
        await service.GetParticipationStatsAsync(from, to);

        repository.Verify(
            r => r.GetParticipationStatsAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>()),
            Times.Once);
    }

    [Fact]
    public async Task DifferentFromBoundsOnTheSameDay_DoNotShareACacheEntry()
    {
        var (service, repository) = CreateService();
        var to = Utc(2026, 11, 1, 20, 0);

        await service.GetParticipationStatsAsync(Utc(2026, 11, 1, 10, 0), to);
        await service.GetParticipationStatsAsync(Utc(2026, 11, 1, 11, 0), to);

        repository.Verify(
            r => r.GetParticipationStatsAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task OmittedBounds_ShareOneCacheEntryBecauseToIsKeyedAsNow()
    {
        var (service, repository) = CreateService();

        await service.GetParticipationStatsAsync();
        await service.GetParticipationStatsAsync();

        repository.Verify(
            r => r.GetParticipationStatsAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>()),
            Times.Once);
    }

    [Fact]
    public async Task OmittedFrom_DefaultsToCairoMidnightThirtyDaysAgo()
    {
        var (service, repository) = CreateService();
        DateTime? capturedFrom = null;
        repository
            .Setup(r => r.GetParticipationStatsAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .Callback<DateTime, DateTime>((from, _) => capturedFrom = from)
            .ReturnsAsync(new ParticipationStatsDto());

        await service.GetParticipationStatsAsync(to: Utc(2026, 11, 4, 0, 0));

        var expected = CairoTime.StartOfDayUtc(CairoTime.Today.AddDays(-30));
        Assert.True(capturedFrom.HasValue);
        Assert.Equal(expected, capturedFrom!.Value);
    }

    [Fact]
    public async Task Meta_DeclaresCairoDisplayZone_WithoutAClientSideOffsetSuggestion()
    {
        var (service, _) = CreateService();

        var response = await service.GetParticipationStatsAsync(
            Utc(2026, 11, 5, 10, 0),
            Utc(2026, 11, 5, 11, 0));

        var json = JsonSerializer.Serialize(response.Meta);
        using var document = JsonDocument.Parse(json);
        var display = document.RootElement.GetProperty("display");

        Assert.Equal("Africa/Cairo", display.GetProperty("timeZone").GetString());
        Assert.False(display.TryGetProperty("suggestedDisplayOffsetMinutes", out _));
        Assert.DoesNotContain("suggestedDisplayOffsetMinutes", json);
    }
}
