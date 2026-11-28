using System;
using System.Linq;
using System.Threading.Tasks;
using Cocorra.DAL.Data;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;
using Cocorra.DAL.Repository.MessageRepository;
using Cocorra.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cocorra.Tests.PrivateChat;

public class ChatListPersistenceTests : IDisposable
{
    private readonly SqliteTestHost _host = new();

    public void Dispose() => _host.Dispose();

    private async Task<ApplicationUser> SeedUserAsync(string firstName, string lastName)
    {
        using var scope = _host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var id = Guid.NewGuid();
        var user = new ApplicationUser
        {
            Id = id,
            UserName = $"u-{id:N}",
            NormalizedUserName = $"U-{id:N}".ToUpperInvariant(),
            Email = $"u-{id:N}@example.test",
            NormalizedEmail = $"U-{id:N}@EXAMPLE.TEST",
            FirstName = firstName,
            LastName = lastName,
            SecurityStamp = Guid.NewGuid().ToString(),
            Status = UserStatus.Active
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private async Task<Room> SeedAudioRoomAsync(Guid hostId, RoomStatus status)
    {
        using var scope = _host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var room = new Room
        {
            Id = Guid.NewGuid(),
            HostId = hostId,
            RoomTitle = "Evening Audio Room",
            Category = RoomCategory.MentalHealth,
            Status = status,
            StartDate = DateTime.UtcNow
        };
        db.Rooms.Add(room);
        await db.SaveChangesAsync();
        return room;
    }

    private async Task SeedMessageAsync(
        Guid senderId,
        Guid receiverId,
        string content,
        DateTime createdAt,
        Guid? originRoomId = null,
        Guid? originTextRoomId = null,
        bool isRead = false)
    {
        using var scope = _host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Messages.Add(new Message
        {
            Id = Guid.NewGuid(),
            SenderId = senderId,
            ReceiverId = receiverId,
            Content = content,
            CreatedAt = createdAt,
            OriginRoomId = originRoomId,
            OriginTextRoomId = originTextRoomId,
            IsRead = isRead
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task NonFriendsWhoStartedChatInAudioRoom_KeepThreadAndHistoryAfterRoomEnds()
    {
        // Regression: room-originated private chats between non-friends disappeared once the room ended.
        var hostUser = await SeedUserAsync("Host", "One");
        var alice = await SeedUserAsync("Alice", "Alpha");
        var bob = await SeedUserAsync("Bob", "Beta");
        var room = await SeedAudioRoomAsync(hostUser.Id, RoomStatus.Live);

        var t1 = new DateTime(2026, 11, 28, 10, 0, 0, DateTimeKind.Utc);
        var t2 = t1.AddMinutes(2);
        await SeedMessageAsync(alice.Id, bob.Id, "Hey from the room!", t1, originRoomId: room.Id);
        await SeedMessageAsync(bob.Id, alice.Id, "Nice talking to you!", t2, originRoomId: null);

        using (var scope = _host.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Rooms
                .Where(r => r.Id == room.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, RoomStatus.Ended));
        }

        using var readScope = _host.CreateScope();
        var repo = new MessageRepository(readScope.ServiceProvider.GetRequiredService<AppDbContext>());

        var aliceList = await repo.GetRecentChatSummariesAsync(alice.Id);
        var bobList = await repo.GetRecentChatSummariesAsync(bob.Id);

        var aliceThread = Assert.Single(aliceList);
        Assert.Equal(bob.Id, aliceThread.FriendId);
        Assert.Equal("Bob Beta", aliceThread.FullName);
        Assert.False(aliceThread.IsFriend);
        Assert.Equal(room.Id, aliceThread.StartedInRoomId);
        Assert.Null(aliceThread.StartedInTextRoomId);
        Assert.Equal("Nice talking to you!", aliceThread.LastMessage);

        var bobThread = Assert.Single(bobList);
        Assert.Equal(alice.Id, bobThread.FriendId);
        Assert.Equal("Alice Alpha", bobThread.FullName);
        Assert.False(bobThread.IsFriend);
        Assert.Equal(room.Id, bobThread.StartedInRoomId);
        Assert.Null(bobThread.StartedInTextRoomId);
        Assert.Equal("Nice talking to you!", bobThread.LastMessage);

        var aliceHistory = await repo.GetChatHistoryAsync(alice.Id, bob.Id, pageNumber: 1, pageSize: 20);
        var bobHistory = await repo.GetChatHistoryAsync(bob.Id, alice.Id, pageNumber: 1, pageSize: 20);

        Assert.Equal(new[] { "Hey from the room!", "Nice talking to you!" }, aliceHistory.Select(m => m.Content));
        Assert.Equal(new[] { "Hey from the room!", "Nice talking to you!" }, bobHistory.Select(m => m.Content));
    }

    [Fact]
    public async Task GetRecentChatSummariesAsync_TracksStartedInTextRoomIdForNonFriends()
    {
        var alice = await SeedUserAsync("Alice", "One");
        var bob = await SeedUserAsync("Bob", "Two");
        var textRoomId = Guid.NewGuid();
        var t1 = new DateTime(2026, 11, 28, 11, 0, 0, DateTimeKind.Utc);
        await SeedMessageAsync(alice.Id, bob.Id, "Ping from text room", t1, originTextRoomId: textRoomId);

        using var scope = _host.CreateScope();
        var repo = new MessageRepository(scope.ServiceProvider.GetRequiredService<AppDbContext>());

        var summaries = await repo.GetRecentChatSummariesAsync(bob.Id);
        var thread = Assert.Single(summaries);
        Assert.False(thread.IsFriend);
        Assert.Null(thread.StartedInRoomId);
        Assert.Equal(textRoomId, thread.StartedInTextRoomId);
    }

    [Fact]
    public async Task GetRecentChatSummariesAsync_SetsIsFriendTrueOnlyForAcceptedRequests()
    {
        var me = await SeedUserAsync("Me", "User");
        var acceptedAsSender = await SeedUserAsync("Accepted", "Sender");
        var acceptedAsReceiver = await SeedUserAsync("Accepted", "Receiver");
        var pending = await SeedUserAsync("Pending", "User");
        var rejected = await SeedUserAsync("Rejected", "User");

        var baseTime = new DateTime(2026, 11, 28, 12, 0, 0, DateTimeKind.Utc);
        await SeedMessageAsync(me.Id, acceptedAsSender.Id, "m1", baseTime.AddMinutes(1));
        await SeedMessageAsync(acceptedAsReceiver.Id, me.Id, "m2", baseTime.AddMinutes(2));
        await SeedMessageAsync(me.Id, pending.Id, "m3", baseTime.AddMinutes(3));
        await SeedMessageAsync(me.Id, rejected.Id, "m4", baseTime.AddMinutes(4));

        using (var scope = _host.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.FriendRequests.AddRange(
                new FriendRequest { SenderId = me.Id, ReceiverId = acceptedAsSender.Id, Status = FriendRequestStatus.Accepted },
                new FriendRequest { SenderId = acceptedAsReceiver.Id, ReceiverId = me.Id, Status = FriendRequestStatus.Accepted },
                new FriendRequest { SenderId = me.Id, ReceiverId = pending.Id, Status = FriendRequestStatus.Pending },
                new FriendRequest { SenderId = rejected.Id, ReceiverId = me.Id, Status = FriendRequestStatus.Rejected });
            await db.SaveChangesAsync();
        }

        using var readScope = _host.CreateScope();
        var repo = new MessageRepository(readScope.ServiceProvider.GetRequiredService<AppDbContext>());
        var byPartner = (await repo.GetRecentChatSummariesAsync(me.Id)).ToDictionary(x => x.FriendId);

        Assert.Equal(4, byPartner.Count);
        Assert.True(byPartner[acceptedAsSender.Id].IsFriend);
        Assert.True(byPartner[acceptedAsReceiver.Id].IsFriend);
        Assert.False(byPartner[pending.Id].IsFriend);
        Assert.False(byPartner[rejected.Id].IsFriend);
    }

    [Fact]
    public async Task GetRecentChatSummariesAsync_ExcludesBlockedPartnersInEitherDirection()
    {
        var me = await SeedUserAsync("Me", "User");
        var blockedByMe = await SeedUserAsync("Blocked", "ByMe");
        var blockedMe = await SeedUserAsync("Blocked", "Me");
        var allowed = await SeedUserAsync("Allowed", "Partner");

        var baseTime = new DateTime(2026, 11, 28, 12, 0, 0, DateTimeKind.Utc);
        await SeedMessageAsync(me.Id, blockedByMe.Id, "old msg", baseTime.AddMinutes(1));
        await SeedMessageAsync(blockedMe.Id, me.Id, "other msg", baseTime.AddMinutes(2));
        await SeedMessageAsync(me.Id, allowed.Id, "visible msg", baseTime.AddMinutes(3));

        using (var scope = _host.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.UserBlocks.AddRange(
                new UserBlock { BlockerId = me.Id, BlockedId = blockedByMe.Id },
                new UserBlock { BlockerId = blockedMe.Id, BlockedId = me.Id });
            await db.SaveChangesAsync();
        }

        using var readScope = _host.CreateScope();
        var repo = new MessageRepository(readScope.ServiceProvider.GetRequiredService<AppDbContext>());
        var summaries = await repo.GetRecentChatSummariesAsync(me.Id);

        var only = Assert.Single(summaries);
        Assert.Equal(allowed.Id, only.FriendId);
    }
}
