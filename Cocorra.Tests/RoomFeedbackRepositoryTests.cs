using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Cocorra.DAL.Data;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;
using Cocorra.DAL.Repository.RoomFeedbackRepository;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Cocorra.Tests;

public class RoomFeedbackRepositoryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _context;
    private readonly RoomFeedbackRepository _repo;

    public RoomFeedbackRepositoryTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _context = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();

        _repo = new RoomFeedbackRepository(_context);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<ApplicationUser> SeedUserAsync(string firstName = "Test", string lastName = "User")
    {
        var id = Guid.NewGuid();
        var user = new ApplicationUser
        {
            Id = id,
            UserName = $"user-{id:N}",
            NormalizedUserName = $"USER-{id:N}".ToUpperInvariant(),
            Email = $"user-{id:N}@test.local",
            NormalizedEmail = $"USER-{id:N}@TEST.LOCAL",
            FirstName = firstName,
            LastName = lastName,
            SecurityStamp = Guid.NewGuid().ToString()
        };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();
        return user;
    }

    private async Task<Room> SeedRoomAsync(Guid hostId, string title = "Coaching Session")
    {
        var room = new Room
        {
            Id = Guid.NewGuid(),
            HostId = hostId,
            RoomTitle = title,
            Category = RoomCategory.MentalHealth,
            Status = RoomStatus.Live,
            StartDate = DateTime.UtcNow
        };
        _context.Rooms.Add(room);
        await _context.SaveChangesAsync();
        return room;
    }

    [Fact]
    public async Task UpsertAsync_FirstCall_InsertsRowAndReturnsIsUpdateFalse()
    {
        var host = await SeedUserAsync("Host", "One");
        var room = await SeedRoomAsync(host.Id);
        var user = await SeedUserAsync("Participant", "One");

        var (feedback, isUpdate) = await _repo.UpsertAsync(room.Id, user.Id, 5, "First feedback");

        Assert.False(isUpdate);
        Assert.NotNull(feedback);
        Assert.Equal(room.Id, feedback.RoomId);
        Assert.Equal(user.Id, feedback.UserId);
        Assert.Equal(5, feedback.Rating);
        Assert.Equal("First feedback", feedback.Comment);
        Assert.Null(feedback.UpdatedAt);

        var rows = await _context.RoomFeedbacks.AsNoTracking().ToListAsync();
        Assert.Single(rows);
        Assert.Equal(room.Id, rows[0].RoomId);
        Assert.Equal(user.Id, rows[0].UserId);
        Assert.Equal(5, rows[0].Rating);
        Assert.Equal("First feedback", rows[0].Comment);
        Assert.Null(rows[0].UpdatedAt);
    }

    [Fact]
    public async Task UpsertAsync_SecondCallSameRoomAndUser_UpdatesRowAndReturnsIsUpdateTrue()
    {
        var host = await SeedUserAsync("Host", "One");
        var room = await SeedRoomAsync(host.Id);
        var user = await SeedUserAsync("Participant", "One");

        var (firstFeedback, isFirstUpdate) = await _repo.UpsertAsync(room.Id, user.Id, 3, "Initial thought");
        Assert.False(isFirstUpdate);

        var (updatedFeedback, isSecondUpdate) = await _repo.UpsertAsync(room.Id, user.Id, 5, "Updated thought");

        Assert.True(isSecondUpdate);
        Assert.Equal(5, updatedFeedback.Rating);
        Assert.Equal("Updated thought", updatedFeedback.Comment);
        Assert.NotNull(updatedFeedback.UpdatedAt);

        var rows = await _context.RoomFeedbacks.AsNoTracking().ToListAsync();
        Assert.Single(rows);
        Assert.Equal(firstFeedback.Id, rows[0].Id);
        Assert.Equal(5, rows[0].Rating);
        Assert.Equal("Updated thought", rows[0].Comment);
        Assert.NotNull(rows[0].UpdatedAt);
    }

    [Fact]
    public async Task UpsertAsync_DifferentUsersSameRoom_CreatesSeparateRows()
    {
        var host = await SeedUserAsync("Host", "One");
        var room = await SeedRoomAsync(host.Id);
        var user1 = await SeedUserAsync("User", "One");
        var user2 = await SeedUserAsync("User", "Two");

        var (feedback1, isUpdate1) = await _repo.UpsertAsync(room.Id, user1.Id, 4, "Comment 1");
        var (feedback2, isUpdate2) = await _repo.UpsertAsync(room.Id, user2.Id, 5, "Comment 2");

        Assert.False(isUpdate1);
        Assert.False(isUpdate2);
        Assert.NotEqual(feedback1.Id, feedback2.Id);

        var rows = await _context.RoomFeedbacks.AsNoTracking()
            .Where(f => f.RoomId == room.Id)
            .ToListAsync();

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.UserId == user1.Id && r.Rating == 4);
        Assert.Contains(rows, r => r.UserId == user2.Id && r.Rating == 5);
    }

    [Fact]
    public async Task GetByRoomAndUserAsync_ReturnsFeedbackWhenExists_AndNullWhenMissing()
    {
        var host = await SeedUserAsync("Host", "One");
        var room = await SeedRoomAsync(host.Id);
        var user = await SeedUserAsync("User", "One");

        await _repo.UpsertAsync(room.Id, user.Id, 4, "Found me");

        var existing = await _repo.GetByRoomAndUserAsync(room.Id, user.Id);
        Assert.NotNull(existing);
        Assert.Equal(4, existing!.Rating);
        Assert.Equal("Found me", existing.Comment);

        var notFound = await _repo.GetByRoomAndUserAsync(room.Id, Guid.NewGuid());
        Assert.Null(notFound);
    }

    [Fact]
    public async Task GetRatingCountsAsync_GroupsByRatingCorrectly()
    {
        var host = await SeedUserAsync("Host", "One");
        var room1 = await SeedRoomAsync(host.Id, "Room 1");
        var room2 = await SeedRoomAsync(host.Id, "Room 2");

        var user1 = await SeedUserAsync("User", "1");
        var user2 = await SeedUserAsync("User", "2");
        var user3 = await SeedUserAsync("User", "3");
        var user4 = await SeedUserAsync("User", "4");

        // Room 1: two 5s, one 3
        await _repo.UpsertAsync(room1.Id, user1.Id, 5, null);
        await _repo.UpsertAsync(room1.Id, user2.Id, 5, null);
        await _repo.UpsertAsync(room1.Id, user3.Id, 3, null);

        // Room 2: one 5 (must not leak into Room 1 counts)
        await _repo.UpsertAsync(room2.Id, user4.Id, 5, null);

        var counts = await _repo.GetRatingCountsAsync(room1.Id);

        Assert.Equal(2, counts.Count);
        Assert.Equal(2, counts[5]);
        Assert.Equal(1, counts[3]);
        Assert.False(counts.ContainsKey(1));
        Assert.False(counts.ContainsKey(2));
        Assert.False(counts.ContainsKey(4));

        var room2Counts = await _repo.GetRatingCountsAsync(room2.Id);
        Assert.Single(room2Counts);
        Assert.Equal(1, room2Counts[5]);

        var emptyCounts = await _repo.GetRatingCountsAsync(Guid.NewGuid());
        Assert.Empty(emptyCounts);
    }

    [Fact]
    public async Task GetPagedAsync_FiltersByRoomIdAndRating()
    {
        var host = await SeedUserAsync("Host", "One");
        var room1 = await SeedRoomAsync(host.Id, "Room 1");
        var room2 = await SeedRoomAsync(host.Id, "Room 2");

        var user1 = await SeedUserAsync("User", "1");
        var user2 = await SeedUserAsync("User", "2");
        var user3 = await SeedUserAsync("User", "3");
        var user4 = await SeedUserAsync("User", "4");

        await _repo.UpsertAsync(room1.Id, user1.Id, 5, "R1 U1");
        await _repo.UpsertAsync(room1.Id, user2.Id, 4, "R1 U2");
        await _repo.UpsertAsync(room1.Id, user3.Id, 5, "R1 U3");
        await _repo.UpsertAsync(room2.Id, user4.Id, 5, "R2 U4");

        // Filter by RoomId only
        var (room1Count, room1Items) = await _repo.GetPagedAsync(room1.Id, null, 1, 10);
        Assert.Equal(3, room1Count);
        Assert.Equal(3, room1Items.Count);
        Assert.All(room1Items, item => Assert.Equal(room1.Id, item.RoomId));

        // Filter by Rating only (across all rooms)
        var (rating5Count, rating5Items) = await _repo.GetPagedAsync(null, 5, 1, 10);
        Assert.Equal(3, rating5Count);
        Assert.Equal(3, rating5Items.Count);
        Assert.All(rating5Items, item => Assert.Equal(5, item.Rating));

        // Filter by both RoomId and Rating
        var (bothCount, bothItems) = await _repo.GetPagedAsync(room1.Id, 5, 1, 10);
        Assert.Equal(2, bothCount);
        Assert.Equal(2, bothItems.Count);
        Assert.All(bothItems, item =>
        {
            Assert.Equal(room1.Id, item.RoomId);
            Assert.Equal(5, item.Rating);
        });

        // Filter with no matches
        var (emptyCount, emptyItems) = await _repo.GetPagedAsync(room1.Id, 1, 1, 10);
        Assert.Equal(0, emptyCount);
        Assert.Empty(emptyItems);
    }

    [Fact]
    public async Task GetPagedAsync_OrdersNewestCreatedAtFirst()
    {
        var host = await SeedUserAsync("Host", "One");
        var room = await SeedRoomAsync(host.Id);

        var user1 = await SeedUserAsync("User", "1");
        var user2 = await SeedUserAsync("User", "2");
        var user3 = await SeedUserAsync("User", "3");

        var f1 = new RoomFeedback
        {
            Id = Guid.NewGuid(),
            RoomId = room.Id,
            UserId = user1.Id,
            Rating = 3,
            CreatedAt = DateTime.UtcNow.AddMinutes(-30)
        };
        var f2 = new RoomFeedback
        {
            Id = Guid.NewGuid(),
            RoomId = room.Id,
            UserId = user2.Id,
            Rating = 5,
            CreatedAt = DateTime.UtcNow.AddMinutes(-10) // newest
        };
        var f3 = new RoomFeedback
        {
            Id = Guid.NewGuid(),
            RoomId = room.Id,
            UserId = user3.Id,
            Rating = 4,
            CreatedAt = DateTime.UtcNow.AddMinutes(-20) // middle
        };

        _context.RoomFeedbacks.AddRange(f1, f2, f3);
        await _context.SaveChangesAsync();

        var (totalCount, items) = await _repo.GetPagedAsync(room.Id, null, 1, 10);

        Assert.Equal(3, totalCount);
        Assert.Equal(3, items.Count);
        Assert.Equal(f2.Id, items[0].Id);
        Assert.Equal(f3.Id, items[1].Id);
        Assert.Equal(f1.Id, items[2].Id);
    }

    [Fact]
    public async Task GetPagedAsync_PagesCorrectly_TotalCountIsPrePagingCount()
    {
        var host = await SeedUserAsync("Host", "One");
        var room = await SeedRoomAsync(host.Id);

        for (var i = 1; i <= 5; i++)
        {
            var user = await SeedUserAsync($"User{i}", $"Test{i}");
            var feedback = new RoomFeedback
            {
                Id = Guid.NewGuid(),
                RoomId = room.Id,
                UserId = user.Id,
                Rating = 5,
                Comment = $"Comment {i}",
                CreatedAt = DateTime.UtcNow.AddMinutes(i)
            };
            _context.RoomFeedbacks.Add(feedback);
        }
        await _context.SaveChangesAsync();

        // Page 1 of size 2 -> 2 items, totalCount 5
        var (total1, items1) = await _repo.GetPagedAsync(room.Id, null, pageNumber: 1, pageSize: 2);
        Assert.Equal(5, total1);
        Assert.Equal(2, items1.Count);

        // Page 2 of size 2 -> 2 items, totalCount 5
        var (total2, items2) = await _repo.GetPagedAsync(room.Id, null, pageNumber: 2, pageSize: 2);
        Assert.Equal(5, total2);
        Assert.Equal(2, items2.Count);
        Assert.NotEqual(items1[0].Id, items2[0].Id);

        // Page 3 of size 2 -> 1 item, totalCount 5
        var (total3, items3) = await _repo.GetPagedAsync(room.Id, null, pageNumber: 3, pageSize: 2);
        Assert.Equal(5, total3);
        Assert.Single(items3);
    }

    [Fact]
    public async Task GetPagedAsync_IncludesRoomAndUserNavigations()
    {
        var host = await SeedUserAsync("Host", "One");
        var room = await SeedRoomAsync(host.Id, "Nav Room Title");
        var user = await SeedUserAsync("NavFirstName", "NavLastName");

        await _repo.UpsertAsync(room.Id, user.Id, 5, "Nav check comment");

        var (totalCount, items) = await _repo.GetPagedAsync(room.Id, null, 1, 10);

        Assert.Equal(1, totalCount);
        var item = Assert.Single(items);

        Assert.NotNull(item.Room);
        Assert.Equal(room.Id, item.Room!.Id);
        Assert.Equal("Nav Room Title", item.Room.RoomTitle);

        Assert.NotNull(item.User);
        Assert.Equal(user.Id, item.User!.Id);
        Assert.Equal("NavFirstName", item.User.FirstName);
        Assert.Equal("NavLastName", item.User.LastName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public async Task DirectInsert_RatingOutOfRange_ThrowsDbUpdateExceptionOnCheckConstraint(int invalidRating)
    {
        var host = await SeedUserAsync("Host", "One");
        var room = await SeedRoomAsync(host.Id);
        var user = await SeedUserAsync("User", "One");

        var invalidFeedback = new RoomFeedback
        {
            Id = Guid.NewGuid(),
            RoomId = room.Id,
            UserId = user.Id,
            Rating = invalidRating,
            Comment = "Invalid rating test"
        };

        _context.RoomFeedbacks.Add(invalidFeedback);

        await Assert.ThrowsAsync<DbUpdateException>(() => _context.SaveChangesAsync());
    }
}
