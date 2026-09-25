// Owns: BE-INVITE-010, BE-INVITE-011, BE-INVITE-012, BE-INVITE-013, BE-INVITE-014, BE-INVITE-015, BE-INVITE-016, BE-INVITE-017, BE-INVITE-018, BE-INVITE-019, BE-INVITE-020, BE-INVITE-021, BE-INVITE-022, BE-INVITE-023
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Cocorra.BLL.Services.RoomInviteService;
using Cocorra.DAL.Data;
using Cocorra.DAL.DTOS.RoomInviteDto;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;
using Cocorra.DAL.Repository.RoomInviteRepository;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Cocorra.Tests;

public class RoomInviteRepositoryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _context;
    private readonly RoomInviteRepository _repo;

    public RoomInviteRepositoryTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:;Foreign Keys=True");
        _connection.Open();

        _context = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();

        _repo = new RoomInviteRepository(_context);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<ApplicationUser> SeedUserAsync(string name = "TestUser")
    {
        var id = Guid.NewGuid();
        var user = new ApplicationUser
        {
            Id = id,
            UserName = $"{name.ToLowerInvariant()}-{id:N}",
            NormalizedUserName = $"{name.ToUpperInvariant()}-{id:N}",
            Email = $"{name.ToLowerInvariant()}-{id:N}@test.local",
            NormalizedEmail = $"{name.ToUpperInvariant()}-{id:N}@TEST.LOCAL",
            FirstName = name,
            LastName = "User",
            SecurityStamp = Guid.NewGuid().ToString()
        };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();
        return user;
    }

    private async Task<Room> SeedRoomAsync(Guid hostId, RoomStatus status = RoomStatus.Live, bool isPrivate = false, string title = "Coaching Session")
    {
        var room = new Room
        {
            Id = Guid.NewGuid(),
            HostId = hostId,
            RoomTitle = title,
            Category = RoomCategory.MentalHealth,
            Status = status,
            IsPrivate = isPrivate,
            StartDate = DateTime.UtcNow
        };
        _context.Rooms.Add(room);
        await _context.SaveChangesAsync();
        return room;
    }

    private async Task<RoomInvite> SeedInviteAsync(
        Guid roomId,
        Guid inviterUserId,
        RoomInviteStatus status = RoomInviteStatus.Active,
        DateTime? expiresAt = null,
        DateTime? createdAt = null,
        Guid? usedByUserId = null,
        DateTime? usedAt = null,
        Guid? revokedByUserId = null,
        DateTime? revokedAt = null,
        string? code = null)
    {
        var invite = new RoomInvite
        {
            Id = Guid.NewGuid(),
            InviteCode = code ?? InviteCodeGenerator.Generate(),
            RoomId = roomId,
            InviterUserId = inviterUserId,
            Status = status,
            ExpiresAt = expiresAt ?? DateTime.UtcNow.AddHours(24),
            CreatedAt = createdAt ?? DateTime.UtcNow,
            UsedByUserId = usedByUserId,
            UsedAt = usedAt,
            RevokedByUserId = revokedByUserId,
            RevokedAt = revokedAt
        };
        _context.RoomInvites.Add(invite);
        await _context.SaveChangesAsync();
        return invite;
    }

    [Fact]
    public async Task GetByCodeAsync_ExistingCode_ReturnsUntrackedInvite()
    {
        var host = await SeedUserAsync("Host");
        var room = await SeedRoomAsync(host.Id);
        var seeded = await SeedInviteAsync(room.Id, host.Id);

        _context.ChangeTracker.Clear();

        var result = await _repo.GetByCodeAsync(seeded.InviteCode);

        Assert.NotNull(result);
        Assert.Equal(seeded.Id, result!.Id);
        Assert.Equal(seeded.InviteCode, result.InviteCode);
        Assert.Equal(seeded.RoomId, result.RoomId);
        Assert.Equal(seeded.InviterUserId, result.InviterUserId);
        Assert.Equal(seeded.Status, result.Status);

        // Untracked assertion: ChangeTracker should not have this entity
        var tracked = _context.ChangeTracker.Entries<RoomInvite>()
            .FirstOrDefault(e => e.Entity.Id == seeded.Id);
        Assert.Null(tracked);
    }

    [Fact]
    public async Task GetByCodeAsync_NonExistentCode_ReturnsNull()
    {
        var result = await _repo.GetByCodeAsync("non_existent_code_12345");
        Assert.Null(result);
    }

    [Fact]
    public async Task CountUsableAsync_ReturnsOnlyActiveUnexpiredInvitesForSpecificInviterAndRoom()
    {
        var host = await SeedUserAsync("Host");
        var otherUser = await SeedUserAsync("OtherInviter");
        var room1 = await SeedRoomAsync(host.Id, RoomStatus.Live, title: "Room 1");
        var room2 = await SeedRoomAsync(host.Id, RoomStatus.Live, title: "Room 2");

        var now = DateTime.UtcNow;

        // 1 & 2: Active, unexpired for (host, room1) -> MUST COUNT
        await SeedInviteAsync(room1.Id, host.Id, RoomInviteStatus.Active, expiresAt: now.AddHours(2));
        await SeedInviteAsync(room1.Id, host.Id, RoomInviteStatus.Active, expiresAt: now.AddHours(5));

        // 3: Expired for (host, room1) -> MUST NOT COUNT
        await SeedInviteAsync(room1.Id, host.Id, RoomInviteStatus.Active, expiresAt: now.AddHours(-1));

        // 4: Used for (host, room1) -> MUST NOT COUNT
        await SeedInviteAsync(room1.Id, host.Id, RoomInviteStatus.Used, expiresAt: now.AddHours(2));

        // 5: Revoked for (host, room1) -> MUST NOT COUNT
        await SeedInviteAsync(room1.Id, host.Id, RoomInviteStatus.Revoked, expiresAt: now.AddHours(2));

        // 6: Active, unexpired for DIFFERENT user (otherUser, room1) -> MUST NOT COUNT
        await SeedInviteAsync(room1.Id, otherUser.Id, RoomInviteStatus.Active, expiresAt: now.AddHours(2));

        // 7: Active, unexpired for DIFFERENT room (host, room2) -> MUST NOT COUNT
        await SeedInviteAsync(room2.Id, host.Id, RoomInviteStatus.Active, expiresAt: now.AddHours(2));

        var count = await _repo.CountUsableAsync(host.Id, room1.Id, now);

        Assert.Equal(2, count);
    }

    [Fact]
    public async Task AddWithUniqueCodeAsync_InsertsWithCodeFromGenerator()
    {
        var host = await SeedUserAsync("Host");
        var room = await SeedRoomAsync(host.Id);

        var newInvite = new RoomInvite
        {
            RoomId = room.Id,
            InviterUserId = host.Id,
            Status = RoomInviteStatus.Active,
            ExpiresAt = DateTime.UtcNow.AddHours(24),
            CreatedAt = DateTime.UtcNow
        };

        var result = await _repo.AddWithUniqueCodeAsync(newInvite, InviteCodeGenerator.Generate);

        Assert.NotNull(result);
        Assert.NotEqual(Guid.Empty, result.Id);
        Assert.NotEmpty(result.InviteCode);
        Assert.Equal(InviteCodeGenerator.CodeLength, result.InviteCode.Length);

        var persisted = await _context.RoomInvites.FindAsync(result.Id);
        Assert.NotNull(persisted);
        Assert.Equal(result.InviteCode, persisted!.InviteCode);
    }

    [Fact]
    public async Task TryConsumeAsync_ActiveAndUnexpired_FlipsToUsedAndReturnsTrue()
    {
        var host = await SeedUserAsync("Host");
        var participant = await SeedUserAsync("Participant");
        var room = await SeedRoomAsync(host.Id);
        var now = DateTime.UtcNow;
        var invite = await SeedInviteAsync(room.Id, host.Id, RoomInviteStatus.Active, expiresAt: now.AddHours(2));

        var ok = await _repo.TryConsumeAsync(invite.Id, participant.Id, now);

        Assert.True(ok);

        var stored = await _context.RoomInvites.AsNoTracking().SingleAsync(i => i.Id == invite.Id);
        Assert.Equal(RoomInviteStatus.Used, stored.Status);
        Assert.Equal(participant.Id, stored.UsedByUserId);
        Assert.NotNull(stored.UsedAt);
    }

    [Fact]
    public async Task TryConsumeAsync_ExpiredInvite_ReturnsFalseAndLeavesRowUnchanged()
    {
        var host = await SeedUserAsync("Host");
        var participant = await SeedUserAsync("Participant");
        var room = await SeedRoomAsync(host.Id);
        var now = DateTime.UtcNow;
        var invite = await SeedInviteAsync(room.Id, host.Id, RoomInviteStatus.Active, expiresAt: now.AddMinutes(-5));

        var ok = await _repo.TryConsumeAsync(invite.Id, participant.Id, now);

        Assert.False(ok);

        var stored = await _context.RoomInvites.AsNoTracking().SingleAsync(i => i.Id == invite.Id);
        Assert.Equal(RoomInviteStatus.Active, stored.Status);
        Assert.Null(stored.UsedByUserId);
        Assert.Null(stored.UsedAt);
    }

    [Fact]
    public async Task TryConsumeAsync_AlreadyUsedOrRevoked_ReturnsFalse()
    {
        var host = await SeedUserAsync("Host");
        var participant = await SeedUserAsync("Participant");
        var room = await SeedRoomAsync(host.Id);
        var now = DateTime.UtcNow;

        var usedInvite = await SeedInviteAsync(room.Id, host.Id, RoomInviteStatus.Used, expiresAt: now.AddHours(2));
        var revokedInvite = await SeedInviteAsync(room.Id, host.Id, RoomInviteStatus.Revoked, expiresAt: now.AddHours(2));

        var usedResult = await _repo.TryConsumeAsync(usedInvite.Id, participant.Id, now);
        var revokedResult = await _repo.TryConsumeAsync(revokedInvite.Id, participant.Id, now);

        Assert.False(usedResult);
        Assert.False(revokedResult);
    }

    [Fact]
    public async Task TryRevokeAsync_ActiveInvite_FlipsToRevokedAndReturnsTrue()
    {
        var host = await SeedUserAsync("Host");
        var room = await SeedRoomAsync(host.Id);
        var now = DateTime.UtcNow;
        var invite = await SeedInviteAsync(room.Id, host.Id, RoomInviteStatus.Active);

        var ok = await _repo.TryRevokeAsync(invite.Id, host.Id, now);

        Assert.True(ok);

        var stored = await _context.RoomInvites.AsNoTracking().SingleAsync(i => i.Id == invite.Id);
        Assert.Equal(RoomInviteStatus.Revoked, stored.Status);
        Assert.Equal(host.Id, stored.RevokedByUserId);
        Assert.NotNull(stored.RevokedAt);
    }

    [Fact]
    public async Task TryRevokeAsync_AlreadyUsedOrRevoked_ReturnsFalse()
    {
        var host = await SeedUserAsync("Host");
        var room = await SeedRoomAsync(host.Id);
        var now = DateTime.UtcNow;

        var usedInvite = await SeedInviteAsync(room.Id, host.Id, RoomInviteStatus.Used);
        var revokedInvite = await SeedInviteAsync(room.Id, host.Id, RoomInviteStatus.Revoked);

        Assert.False(await _repo.TryRevokeAsync(usedInvite.Id, host.Id, now));
        Assert.False(await _repo.TryRevokeAsync(revokedInvite.Id, host.Id, now));
    }

    [Fact]
    public async Task ExecuteInTransactionAsync_Rollback_ClearsChangeTrackerAndLeavesInviteActive()
    {
        var host = await SeedUserAsync("Host");
        var participant = await SeedUserAsync("Participant");
        var room = await SeedRoomAsync(host.Id);
        var now = DateTime.UtcNow;
        var invite = await SeedInviteAsync(room.Id, host.Id, RoomInviteStatus.Active, expiresAt: now.AddHours(24));

        var outcome = await _repo.ExecuteInTransactionAsync(async () =>
        {
            var consumed = await _repo.TryConsumeAsync(invite.Id, participant.Id, now);
            Assert.True(consumed);
            // Simulate operation failure requiring rollback:
            return (Commit: false, Result: "aborted");
        });

        Assert.Equal("aborted", outcome);

        // After rollback: invite must remain Active, without UsedAt or UsedByUserId
        var stored = await _context.RoomInvites.AsNoTracking().SingleAsync(i => i.Id == invite.Id);
        Assert.Equal(RoomInviteStatus.Active, stored.Status);
        Assert.Null(stored.UsedAt);
        Assert.Null(stored.UsedByUserId);
    }

    [Fact]
    public async Task Concurrency_TwoIndependentContextsOnSameSqliteFile_ExactlyOneConsumeSucceeds()
    {
        var tempFile = Path.GetTempFileName();
        var connectionString = $"Data Source={tempFile};Default Timeout=30;";

        using var connection1 = new SqliteConnection(connectionString);
        using var connection2 = new SqliteConnection(connectionString);
        await connection1.OpenAsync();
        await connection2.OpenAsync();

        var options1 = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection1).Options;
        var options2 = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection2).Options;

        using var context1 = new AppDbContext(options1);
        using var context2 = new AppDbContext(options2);

        try
        {
            await context1.Database.EnsureCreatedAsync();

            var hostId = Guid.NewGuid();
            var user1Id = Guid.NewGuid();
            var user2Id = Guid.NewGuid();
            var roomId = Guid.NewGuid();
            var inviteId = Guid.NewGuid();

            var host = new ApplicationUser { Id = hostId, UserName = "host", Email = "host@test.local", SecurityStamp = Guid.NewGuid().ToString() };
            var user1 = new ApplicationUser { Id = user1Id, UserName = "user1", Email = "u1@test.local", SecurityStamp = Guid.NewGuid().ToString() };
            var user2 = new ApplicationUser { Id = user2Id, UserName = "user2", Email = "u2@test.local", SecurityStamp = Guid.NewGuid().ToString() };
            var room = new Room { Id = roomId, HostId = hostId, RoomTitle = "Concurrent Room", Status = RoomStatus.Live, StartDate = DateTime.UtcNow, Category = RoomCategory.MentalHealth };
            var invite = new RoomInvite
            {
                Id = inviteId,
                InviteCode = InviteCodeGenerator.Generate(),
                RoomId = roomId,
                InviterUserId = hostId,
                Status = RoomInviteStatus.Active,
                ExpiresAt = DateTime.UtcNow.AddHours(24),
                CreatedAt = DateTime.UtcNow
            };

            context1.Users.AddRange(host, user1, user2);
            context1.Rooms.Add(room);
            context1.RoomInvites.Add(invite);
            await context1.SaveChangesAsync();

            var repo1 = new RoomInviteRepository(context1);
            var repo2 = new RoomInviteRepository(context2);
            var now = DateTime.UtcNow;

            var task1 = repo1.ExecuteInTransactionAsync(async () =>
            {
                var ok = await repo1.TryConsumeAsync(inviteId, user1Id, now);
                await Task.Delay(100);
                return (ok, ok);
            });

            var task2 = repo2.ExecuteInTransactionAsync(async () =>
            {
                var ok = await repo2.TryConsumeAsync(inviteId, user2Id, now);
                await Task.Delay(100);
                return (ok, ok);
            });

            var results = await Task.WhenAll(task1, task2);

            // Assert exactly one returned true
            var wonCount = results.Count(r => r);
            Assert.Equal(1, wonCount);

            // Stored row's UsedByUserId must match the winner
            var stored = await context1.RoomInvites.AsNoTracking().SingleAsync(i => i.Id == inviteId);
            Assert.Equal(RoomInviteStatus.Used, stored.Status);

            var expectedWinnerId = results[0] ? user1Id : user2Id;
            Assert.Equal(expectedWinnerId, stored.UsedByUserId);
            Assert.NotNull(stored.UsedAt);
        }
        finally
        {
            connection1.Close();
            connection2.Close();
            if (File.Exists(tempFile))
            {
                try { File.Delete(tempFile); } catch { }
            }
        }
    }

    [Fact]
    public async Task GetLifecycleStatsAsync_ComputesAuthoritativeCountsAndUniqueAcceptedUsers()
    {
        var host = await SeedUserAsync("Host");
        var userA = await SeedUserAsync("UserA");
        var userB = await SeedUserAsync("UserB");

        var liveRoom = await SeedRoomAsync(host.Id, RoomStatus.Live, title: "Live Room");
        var endedRoom = await SeedRoomAsync(host.Id, RoomStatus.Ended, title: "Ended Room");
        var cancelledRoom = await SeedRoomAsync(host.Id, RoomStatus.Cancelled, title: "Cancelled Room");

        var now = DateTime.UtcNow;

        // 1. Active invite (live room, unexpired) -> Active
        await SeedInviteAsync(liveRoom.Id, host.Id, RoomInviteStatus.Active, expiresAt: now.AddHours(10));

        // 2. Used invite accepted by userA -> Used
        await SeedInviteAsync(liveRoom.Id, host.Id, RoomInviteStatus.Used, usedByUserId: userA.Id, usedAt: now.AddMinutes(-50));

        // 3. Another Used invite accepted by userA -> Used (same user accepted twice, UniqueAcceptedUsers counts once)
        await SeedInviteAsync(liveRoom.Id, host.Id, RoomInviteStatus.Used, usedByUserId: userA.Id, usedAt: now.AddMinutes(-40));

        // 4. Used invite accepted by userB -> Used (second distinct user)
        await SeedInviteAsync(liveRoom.Id, host.Id, RoomInviteStatus.Used, usedByUserId: userB.Id, usedAt: now.AddMinutes(-30));

        // 5. Expired by time: Status = Active, ExpiresAt in past, live room -> Expired
        await SeedInviteAsync(liveRoom.Id, host.Id, RoomInviteStatus.Active, expiresAt: now.AddMinutes(-10));

        // 6. Expired by room status: Status = Active, future ExpiresAt, but room is Ended -> Expired
        await SeedInviteAsync(endedRoom.Id, host.Id, RoomInviteStatus.Active, expiresAt: now.AddHours(5));

        // 7. Expired by room status: Status = Active, future ExpiresAt, but room is Cancelled -> Expired
        await SeedInviteAsync(cancelledRoom.Id, host.Id, RoomInviteStatus.Active, expiresAt: now.AddHours(5));

        // 8. Revoked invite -> Revoked
        await SeedInviteAsync(liveRoom.Id, host.Id, RoomInviteStatus.Revoked, revokedByUserId: host.Id, revokedAt: now.AddMinutes(-20));

        var stats = await _repo.GetLifecycleStatsAsync(fromUtc: null, toUtc: null, roomId: null, inviterUserId: null, nowUtc: now);

        Assert.Equal(8, stats.TotalCreated);
        Assert.Equal(1, stats.Active);
        Assert.Equal(3, stats.Used);
        Assert.Equal(3, stats.Expired); // 1 time-expired + 1 ended room + 1 cancelled room
        Assert.Equal(1, stats.Revoked);
        Assert.Equal(3, stats.SuccessfulAcceptances);
        Assert.Equal(2, stats.UniqueAcceptedUsers); // userA and userB
    }

    [Fact]
    public async Task GetLifecycleStatsAsync_AppliesFiltersCorrectly()
    {
        var hostA = await SeedUserAsync("HostA");
        var hostB = await SeedUserAsync("HostB");
        var user = await SeedUserAsync("AcceptingUser");

        var roomA = await SeedRoomAsync(hostA.Id, RoomStatus.Live, title: "Room A");
        var roomB = await SeedRoomAsync(hostB.Id, RoomStatus.Live, title: "Room B");

        var baseTime = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

        // Room A, Host A, Day 1
        await SeedInviteAsync(roomA.Id, hostA.Id, RoomInviteStatus.Active, createdAt: baseTime);

        // Room A, Host A, Day 2
        await SeedInviteAsync(roomA.Id, hostA.Id, RoomInviteStatus.Used, createdAt: baseTime.AddDays(1), usedByUserId: user.Id);

        // Room A, Host B, Day 2
        await SeedInviteAsync(roomA.Id, hostB.Id, RoomInviteStatus.Active, createdAt: baseTime.AddDays(1));

        // Room B, Host A, Day 3
        await SeedInviteAsync(roomB.Id, hostA.Id, RoomInviteStatus.Active, createdAt: baseTime.AddDays(2));

        // Filter by Room A only -> 3 invites
        var statsRoomA = await _repo.GetLifecycleStatsAsync(null, null, roomA.Id, null, baseTime.AddDays(10));
        Assert.Equal(3, statsRoomA.TotalCreated);

        // Filter by Inviter Host A only -> 3 invites
        var statsHostA = await _repo.GetLifecycleStatsAsync(null, null, null, hostA.Id, baseTime.AddDays(10));
        Assert.Equal(3, statsHostA.TotalCreated);

        // Filter by Date window [baseTime, baseTime.AddDays(1)] -> 3 invites
        var statsWindow = await _repo.GetLifecycleStatsAsync(baseTime, baseTime.AddDays(1), null, null, baseTime.AddDays(10));
        Assert.Equal(3, statsWindow.TotalCreated);

        // Combined: Room A + Host A + Day 1 only -> 1 invite
        var statsSpecific = await _repo.GetLifecycleStatsAsync(baseTime.AddHours(-1), baseTime.AddHours(1), roomA.Id, hostA.Id, baseTime.AddDays(10));
        Assert.Equal(1, statsSpecific.TotalCreated);
    }

    [Fact]
    public async Task CountEventsAsync_CountsMatchingUserEventsByEventTypeWithinWindow()
    {
        var host = await SeedUserAsync("Host");
        var room1 = await SeedRoomAsync(host.Id, title: "Room 1");
        var room2 = await SeedRoomAsync(host.Id, title: "Room 2");

        var t0 = new DateTime(2026, 9, 20, 10, 0, 0, DateTimeKind.Utc);

        var events = new List<UserEvent>
        {
            // In window, Room 1: 2 resolved, 1 attempted, 1 failed
            new() { EventId = Guid.NewGuid(), EventType = EventTypes.RoomInviteResolved, RoomId = room1.Id, OccurredAtUtc = t0.AddMinutes(5) },
            new() { EventId = Guid.NewGuid(), EventType = EventTypes.RoomInviteResolved, RoomId = room1.Id, OccurredAtUtc = t0.AddMinutes(10) },
            new() { EventId = Guid.NewGuid(), EventType = EventTypes.RoomInviteAcceptAttempted, RoomId = room1.Id, OccurredAtUtc = t0.AddMinutes(15) },
            new() { EventId = Guid.NewGuid(), EventType = EventTypes.RoomInviteAcceptFailed, RoomId = room1.Id, OccurredAtUtc = t0.AddMinutes(20) },

            // In window, Room 2: 1 resolved
            new() { EventId = Guid.NewGuid(), EventType = EventTypes.RoomInviteResolved, RoomId = room2.Id, OccurredAtUtc = t0.AddMinutes(12) },

            // Outside window (earlier): Room 1 resolved
            new() { EventId = Guid.NewGuid(), EventType = EventTypes.RoomInviteResolved, RoomId = room1.Id, OccurredAtUtc = t0.AddHours(-2) },

            // Unrelated event type
            new() { EventId = Guid.NewGuid(), EventType = EventTypes.RoomJoined, RoomId = room1.Id, OccurredAtUtc = t0.AddMinutes(5) }
        };

        _context.UserEvents.AddRange(events);
        await _context.SaveChangesAsync();

        var queryTypes = new[]
        {
            EventTypes.RoomInviteResolved,
            EventTypes.RoomInviteAcceptAttempted,
            EventTypes.RoomInviteAcceptFailed
        };

        // Window encompassing t0 .. t0+30m for Room 1
        var countsRoom1 = await _repo.CountEventsAsync(queryTypes, t0, t0.AddMinutes(30), room1.Id);

        Assert.Equal(2, countsRoom1[EventTypes.RoomInviteResolved]);
        Assert.Equal(1, countsRoom1[EventTypes.RoomInviteAcceptAttempted]);
        Assert.Equal(1, countsRoom1[EventTypes.RoomInviteAcceptFailed]);

        // Across both rooms within the window (RoomId == null)
        var countsAllRooms = await _repo.CountEventsAsync(queryTypes, t0, t0.AddMinutes(30), null);
        Assert.Equal(3, countsAllRooms[EventTypes.RoomInviteResolved]); // 2 from room1 + 1 from room2
    }
}
