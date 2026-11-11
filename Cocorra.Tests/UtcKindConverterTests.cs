using System;
using System.Threading.Tasks;
using Cocorra.DAL.Data;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;
using Cocorra.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cocorra.Tests;

public class UtcKindConverterTests : IDisposable
{
    private readonly SqliteTestHost _host = new();

    public void Dispose() => _host.Dispose();

    [Theory]
    [InlineData(DateTimeKind.Utc)]
    [InlineData(DateTimeKind.Unspecified)]
    public async Task UserCreatedAt_ReadBackInNewScope_IsUtcWithSameTicks(DateTimeKind kind)
    {
        var userId = Guid.NewGuid();
        var createdAt = new DateTime(2026, 11, 11, 10, 0, 0, kind);

        using (var scope = _host.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Users.Add(User(userId, createdAt));
            await db.SaveChangesAsync();
        }

        using (var scope = _host.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var user = await db.Users.SingleAsync(u => u.Id == userId);

            Assert.Equal(DateTimeKind.Utc, user.CreatedAt.Kind);
            Assert.Equal(createdAt.Ticks, user.CreatedAt.Ticks);
        }
    }

    [Fact]
    public async Task RoomWentLiveAt_ReadBackInNewScope_IsUtcWhenSetAndNullWhenUnset()
    {
        var hostId = Guid.NewGuid();
        var liveRoomId = Guid.NewGuid();
        var scheduledRoomId = Guid.NewGuid();
        var wentLiveAt = new DateTime(2026, 11, 11, 10, 0, 0, DateTimeKind.Utc);

        using (var scope = _host.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Users.Add(User(hostId, new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc)));
            db.Rooms.AddRange(
                Room(liveRoomId, hostId, wentLiveAt),
                Room(scheduledRoomId, hostId, null));
            await db.SaveChangesAsync();
        }

        using (var scope = _host.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var liveRoom = await db.Rooms.SingleAsync(r => r.Id == liveRoomId);
            var scheduledRoom = await db.Rooms.SingleAsync(r => r.Id == scheduledRoomId);

            Assert.NotNull(liveRoom.WentLiveAt);
            Assert.Equal(DateTimeKind.Utc, liveRoom.WentLiveAt!.Value.Kind);
            Assert.Equal(wentLiveAt.Ticks, liveRoom.WentLiveAt.Value.Ticks);
            Assert.Null(scheduledRoom.WentLiveAt);
        }
    }

    private static ApplicationUser User(Guid id, DateTime createdAt) => new()
    {
        Id = id,
        UserName = id.ToString("N"),
        FirstName = "Test",
        LastName = "User",
        CreatedAt = createdAt
    };

    private static Room Room(Guid id, Guid hostId, DateTime? wentLiveAt) => new()
    {
        Id = id,
        HostId = hostId,
        RoomTitle = "Test room",
        Category = RoomCategory.Others,
        Status = wentLiveAt.HasValue ? RoomStatus.Live : RoomStatus.Scheduled,
        CreatedAt = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc),
        StartDate = new DateTime(2026, 11, 11, 10, 0, 0, DateTimeKind.Utc),
        WentLiveAt = wentLiveAt
    };
}
