using Cocorra.BLL.Services.AuthServices;
using Cocorra.BLL.Services.BlockedDevicesService;
using Cocorra.BLL.Services.Email;
using Cocorra.BLL.Services.EventTracking;
using Cocorra.BLL.Services.RoomService;
using Cocorra.BLL.Services.Upload;
using Cocorra.DAL.Data;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;
using Cocorra.DAL.Repository.RoomRepository;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;
using Xunit;

namespace Cocorra.Tests;

/// <summary>
/// DeleteAccountAsync used to end a departing host's rooms by assigning Room.Status directly.
/// That skipped everything else ending a room means — the LiveKit room was never deleted, so
/// the audio bridge kept running; participants stayed Active, which left GET /Room/{id}/Token
/// minting fresh credentials for a room that had ended; and no room_ended event was emitted.
///
/// SQLite rather than the InMemory provider: the method uses ExecuteDeleteAsync, which InMemory
/// does not implement.
/// </summary>
public class AccountDeletionRoomLifecycleTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _context;

    public AccountDeletionRoomLifecycleTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _context = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private static Mock<UserManager<ApplicationUser>> CreateMockUserManager(ApplicationUser user)
    {
        var store = new Mock<IUserStore<ApplicationUser>>();
        var manager = new Mock<UserManager<ApplicationUser>>(
            store.Object, null!, null!, null!, null!, null!, null!, null!, null!);

        manager.Setup(m => m.FindByIdAsync(user.Id.ToString())).ReturnsAsync(user);
        manager.Setup(m => m.DeleteAsync(user)).ReturnsAsync(IdentityResult.Success);

        return manager;
    }

    private AuthServices CreateService(
        Mock<UserManager<ApplicationUser>> userManager, Mock<IRoomService> roomService) =>
        new(
            userManager.Object,
            null!,
            new Mock<IConfiguration>().Object,
            new Mock<IUploadVoice>().Object,
            new Mock<IEmailService>().Object,
            new Mock<IUploadImage>().Object,
            _context,
            new Mock<IRoomRepository>().Object,
            new Mock<IEventTracker>().Object,
            roomService.Object,
            new Mock<IBlockedDevicesService>().Object);

    private async Task<Guid> SeedHostedRoomAsync(Guid hostId, RoomStatus status)
    {
        // Room.HostId is a real FK to AspNetUsers, so the host row has to exist. UserManager is
        // mocked in these tests, which means nothing else puts it there.
        if (!await _context.Users.AnyAsync(u => u.Id == hostId))
        {
            _context.Users.Add(new ApplicationUser
            {
                Id = hostId,
                UserName = $"host-{hostId:N}",
                NormalizedUserName = $"HOST-{hostId:N}".ToUpperInvariant(),
                Email = $"host-{hostId:N}@test.local",
                NormalizedEmail = $"HOST-{hostId:N}@TEST.LOCAL",
                FirstName = "Test",
                LastName = "Host",
                SecurityStamp = Guid.NewGuid().ToString()
            });
        }

        var room = new Room
        {
            Id = Guid.NewGuid(),
            HostId = hostId,
            RoomTitle = "Coaching session",
            Status = status,
            StartDate = DateTime.UtcNow,
            Category = RoomCategory.MentalHealth
        };

        _context.Rooms.Add(room);
        await _context.SaveChangesAsync();

        return room.Id;
    }

    [Theory]
    [InlineData(RoomStatus.Live)]
    [InlineData(RoomStatus.Scheduled)]
    public async Task DeleteAccount_EndsHostedRoomsThroughTheRealLifecycle(RoomStatus status)
    {
        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = "coach" };
        var roomId = await SeedHostedRoomAsync(user.Id, status);

        var roomService = new Mock<IRoomService>();
        var service = CreateService(CreateMockUserManager(user), roomService);

        var result = await service.DeleteAccountAsync(user.Id);

        Assert.True(result.Succeeded);

        // Through EndRoomAsync — which is what deletes the LiveKit room, flips participants to
        // Left and emits room_ended. A direct Status write does none of that.
        roomService.Verify(
            s => s.EndRoomAsync(roomId, user.Id, RoomEndReasons.HostAccountDeleted), Times.Once);
    }

    [Fact]
    public async Task DeleteAccount_LeavesAlreadyEndedRoomsAlone()
    {
        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = "coach" };
        await SeedHostedRoomAsync(user.Id, RoomStatus.Ended);

        var roomService = new Mock<IRoomService>();
        var service = CreateService(CreateMockUserManager(user), roomService);

        var result = await service.DeleteAccountAsync(user.Id);

        Assert.True(result.Succeeded);
        roomService.Verify(
            s => s.EndRoomAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task DeleteAccount_DoesNotEndRoomsHostedBySomeoneElse()
    {
        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = "coach" };
        await SeedHostedRoomAsync(Guid.NewGuid(), RoomStatus.Live);

        var roomService = new Mock<IRoomService>();
        var service = CreateService(CreateMockUserManager(user), roomService);

        var result = await service.DeleteAccountAsync(user.Id);

        Assert.True(result.Succeeded);
        roomService.Verify(
            s => s.EndRoomAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task DeleteAccount_EndsEveryHostedRoom_NotJustTheFirst()
    {
        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = "coach" };
        var first = await SeedHostedRoomAsync(user.Id, RoomStatus.Live);
        var second = await SeedHostedRoomAsync(user.Id, RoomStatus.Scheduled);

        var roomService = new Mock<IRoomService>();
        var service = CreateService(CreateMockUserManager(user), roomService);

        await service.DeleteAccountAsync(user.Id);

        roomService.Verify(
            s => s.EndRoomAsync(first, user.Id, RoomEndReasons.HostAccountDeleted), Times.Once);
        roomService.Verify(
            s => s.EndRoomAsync(second, user.Id, RoomEndReasons.HostAccountDeleted), Times.Once);
    }
}
