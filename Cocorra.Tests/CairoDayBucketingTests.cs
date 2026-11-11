using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cocorra.BLL.Services.Analytics;
using Cocorra.DAL.Data;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;
using Cocorra.DAL.Repository.AnalyticsRepository;
using Cocorra.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cocorra.Tests;

public class CairoDayBucketingTests : IDisposable
{
    private readonly SqliteTestHost _host = new();

    public void Dispose() => _host.Dispose();

    private static DateTime Utc(int year, int month, int day, int hour, int minute = 0, int second = 0) =>
        new(year, month, day, hour, minute, second, DateTimeKind.Utc);

    private static ApplicationUser User(Guid id, DateTime createdAt) => new()
    {
        Id = id,
        UserName = id.ToString("N"),
        FirstName = "Test",
        LastName = "User",
        CreatedAt = createdAt
    };

    private static ApplicationUser User(DateTime createdAt) => User(Guid.NewGuid(), createdAt);

    [Fact]
    public async Task DailyAggregation_BucketsRegistrationsByCairoMidnight()
    {
        using (var scope = _host.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Users.AddRange(
                User(Utc(2026, 11, 10, 21, 30)),
                User(Utc(2026, 11, 10, 22, 30)),
                User(Utc(2026, 11, 11, 21, 59, 59)),
                User(Utc(2026, 11, 11, 22, 0)));
            await db.SaveChangesAsync();

            await AnalyticsAggregationService.AggregateDailyPlatformMetricsAsync(
                db, new DateTime(2026, 11, 11), CancellationToken.None);
            await db.SaveChangesAsync();

            await AnalyticsAggregationService.AggregateDailyPlatformMetricsAsync(
                db, new DateTime(2026, 11, 10), CancellationToken.None);
            await db.SaveChangesAsync();

            var registrations = await db.DailyPlatformMetrics
                .ToDictionaryAsync(m => m.Date, m => m.NewRegistrations);

            Assert.Equal(2, registrations[new DateTime(2026, 11, 11)]);
            Assert.Equal(1, registrations[new DateTime(2026, 11, 10)]);
        }
    }

    [Fact]
    public async Task DailyAggregation_OnSpringForwardDay_StillCoversTheFullCairoDay()
    {
        using (var scope = _host.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Users.AddRange(
                User(Utc(2026, 4, 23, 21, 59, 59)),
                User(Utc(2026, 4, 23, 22, 0)),
                User(Utc(2026, 4, 24, 20, 59, 59)),
                User(Utc(2026, 4, 24, 21, 0)));
            await db.SaveChangesAsync();

            await AnalyticsAggregationService.AggregateDailyPlatformMetricsAsync(
                db, new DateTime(2026, 4, 24), CancellationToken.None);
            await db.SaveChangesAsync();

            var day = await db.DailyPlatformMetrics.SingleAsync(m => m.Date == new DateTime(2026, 4, 24));

            Assert.Equal(2, day.NewRegistrations);
        }
    }

    [Fact]
    public async Task UserGrowthDaily_AssignsEachRegistrationToItsCairoDate()
    {
        using (var scope = _host.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Users.AddRange(
                User(Utc(2026, 11, 10, 21, 30)),
                User(Utc(2026, 11, 10, 22, 30)));
            await db.SaveChangesAsync();

            var result = await new AnalyticsRepository(db).GetUserGrowthAsync(
                "daily",
                Utc(2026, 11, 9, 22, 0),
                Utc(2026, 11, 11, 21, 59, 59));

            Assert.Equal(2, result.DataPoints.Count());
            Assert.Equal(1, result.DataPoints.Single(p => p.Period == "2026-11-10").NewUsers);
            Assert.Equal(1, result.DataPoints.Single(p => p.Period == "2026-11-11").NewUsers);
        }
    }

    [Fact]
    public async Task UserGrowthMonthly_AssignsAnInstantOnCairoDecemberFirstToDecember()
    {
        using (var scope = _host.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Users.Add(User(Utc(2026, 11, 30, 22, 30)));
            await db.SaveChangesAsync();

            var result = await new AnalyticsRepository(db).GetUserGrowthAsync(
                "monthly",
                Utc(2026, 11, 1, 0, 0),
                Utc(2026, 12, 31, 23, 59, 59));

            var point = Assert.Single(result.DataPoints);
            Assert.Equal("2026-12", point.Period);
            Assert.Equal(1, point.NewUsers);
        }
    }

    [Fact]
    public async Task PeakActiveHours_ReturnsEveryCairoHour_AndDistinctUsersIgnoringAnonymousEvents()
    {
        var user1 = Guid.NewGuid();
        var user2 = Guid.NewGuid();

        using (var scope = _host.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Users.AddRange(
                User(user1, Utc(2026, 1, 1, 0, 0)),
                User(user2, Utc(2026, 1, 1, 0, 0)));

            db.UserEvents.AddRange(
                Event(user1, Utc(2026, 1, 15, 18, 10)),
                Event(user1, Utc(2026, 7, 15, 17, 10)),
                Event(user2, Utc(2026, 7, 15, 18, 10)),
                Event(null, Utc(2026, 1, 15, 18, 20)));
            await db.SaveChangesAsync();

            var hours = await new AnalyticsRepository(db).GetPeakActiveHoursAsync(
                Utc(2026, 1, 1, 0, 0),
                Utc(2026, 8, 1, 0, 0));

            Assert.Equal(24, hours.Count);
            Assert.Equal(Enumerable.Range(0, 24), hours.Select(h => h.Hour));

            var hour20 = hours.Single(h => h.Hour == 20);
            Assert.Equal(3, hour20.EventCount);
            Assert.Equal(1, hour20.ActiveUsers);

            var hour21 = hours.Single(h => h.Hour == 21);
            Assert.Equal(1, hour21.EventCount);
            Assert.Equal(1, hour21.ActiveUsers);
        }
    }

    [Fact]
    public async Task SupplyHealth_UsesCairoDatesAndCairoHoursForItsBuckets()
    {
        var hostId = Guid.NewGuid();

        using (var scope = _host.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Users.Add(User(hostId, Utc(2026, 6, 1, 0, 0)));
            db.Rooms.AddRange(
                new Room
                {
                    Id = Guid.NewGuid(),
                    HostId = hostId,
                    RoomTitle = "November room",
                    Category = RoomCategory.Others,
                    Status = RoomStatus.Live,
                    CreatedAt = Utc(2026, 11, 10, 22, 30),
                    StartDate = Utc(2026, 11, 11, 18, 0),
                    WentLiveAt = Utc(2026, 11, 11, 18, 0)
                },
                new Room
                {
                    Id = Guid.NewGuid(),
                    HostId = hostId,
                    RoomTitle = "July room",
                    Category = RoomCategory.Others,
                    Status = RoomStatus.Scheduled,
                    CreatedAt = Utc(2026, 7, 15, 17, 0),
                    StartDate = Utc(2026, 7, 15, 18, 0),
                    WentLiveAt = null
                });
            await db.SaveChangesAsync();

            var result = await new AnalyticsRepository(db).GetSupplyHealthAsync(
                "daily",
                Utc(2026, 7, 1, 0, 0),
                Utc(2026, 12, 1, 0, 0));

            var november = result.ActiveHosts.Single(a => a.Period == "2026-11-11");
            Assert.Equal(1, november.DistinctHosts);
            Assert.Equal(1, november.RoomsCreated);

            Assert.Equal(1, result.ScheduleByHour.Single(s => s.Hour == 20).RoomsStarted);
            Assert.Equal(1, result.ScheduleByHour.Single(s => s.Hour == 21).RoomsStarted);
        }
    }

    private static UserEvent Event(Guid? userId, DateTime occurredAtUtc) => new()
    {
        EventId = Guid.NewGuid(),
        UserId = userId,
        EventType = EventTypes.RoomJoined,
        OccurredAtUtc = occurredAtUtc
    };
}
