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
/// DeleteAccountAsync cleared only four of the FK sets that point at AspNetUsers. Every other
/// Restrict FK — RoomParticipants.UserId, Rooms.HostId, Reports.ReporterId,
/// RoomTopicRequests.RequesterId/TargetCoachId and TopicVotes.UserId — was left in place, so
/// the DELETE tripped a constraint and the user was told to "contact support". Anyone who had
/// ever joined a room was affected, which in practice is every real account.
///
/// These tests differ from <see cref="AccountDeletionRoomLifecycleTests"/> in one decisive way:
/// there, UserManager.DeleteAsync is mocked to report success without touching the database, so
/// no constraint is ever evaluated and the bug stayed invisible. Here the mock performs the real
/// delete, which is what makes SQLite enforce the FKs.
/// </summary>
public class AccountDeletionReferentialIntegrityTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _context;

    public AccountDeletionReferentialIntegrityTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:;Foreign Keys=True");
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

    private ApplicationUser AddUser(string tag)
    {
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = $"{tag}@test.local",
            NormalizedUserName = $"{tag}@TEST.LOCAL",
            Email = $"{tag}@test.local",
            NormalizedEmail = $"{tag}@TEST.LOCAL",
            FirstName = tag,
            LastName = "User",
            SecurityStamp = Guid.NewGuid().ToString(),
            VoiceVerificationPath = $"voices/{tag}.mp3",
            ProfilePicturePath = $"profiles/{tag}.jpg"
        };

        _context.Users.Add(user);
        return user;
    }

    private Room AddRoom(Guid hostId, RoomStatus status = RoomStatus.Ended)
    {
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
        return room;
    }

    /// <summary>
    /// Unlike the mock in the lifecycle tests, this one actually removes the row, so every FK
    /// the service failed to clear surfaces as a real constraint violation.
    /// </summary>
    private Mock<UserManager<ApplicationUser>> CreateRealDeletingUserManager(ApplicationUser user)
    {
        var store = new Mock<IUserStore<ApplicationUser>>();
        var manager = new Mock<UserManager<ApplicationUser>>(
            store.Object, null!, null!, null!, null!, null!, null!, null!, null!);

        manager.Setup(m => m.FindByIdAsync(user.Id.ToString())).ReturnsAsync(user);
        manager.Setup(m => m.DeleteAsync(user)).Returns(async () =>
        {
            _context.Users.Remove(user);
            await _context.SaveChangesAsync();
            return IdentityResult.Success;
        });

        return manager;
    }

    private AuthServices CreateService(
        Mock<UserManager<ApplicationUser>> userManager,
        IUploadVoice? uploadVoice = null,
        IUploadImage? uploadImage = null) =>
        new(
            userManager.Object,
            null!,
            new Mock<IConfiguration>().Object,
            uploadVoice ?? new Mock<IUploadVoice>().Object,
            new Mock<IEmailService>().Object,
            uploadImage ?? new Mock<IUploadImage>().Object,
            _context,
            new Mock<IRoomRepository>().Object,
            new Mock<IEventTracker>().Object,
            new Mock<IRoomService>().Object,
            new Mock<IBlockedDevicesService>().Object);

    /// <summary>
    /// Guards the guard: if SQLite were not enforcing foreign keys, every test below would pass
    /// no matter what the service did.
    /// </summary>
    [Fact]
    public async Task Sqlite_EnforcesForeignKeys()
    {
        var user = AddUser("victim");
        AddRoom(user.Id);
        await _context.SaveChangesAsync();

        // Without this the tracked Room would make EF raise a conceptual-null error in memory,
        // which would prove nothing about whether the database itself enforces the constraint.
        _context.ChangeTracker.Clear();

        _context.Users.Remove(await _context.Users.SingleAsync(u => u.Id == user.Id));

        await Assert.ThrowsAsync<DbUpdateException>(() => _context.SaveChangesAsync());
    }

    [Fact]
    public async Task DeleteAccount_SucceedsForAUserWhoJoinedSomeoneElsesRoom()
    {
        var user = AddUser("joiner");
        var other = AddUser("host");
        var room = AddRoom(other.Id);
        await _context.SaveChangesAsync();

        _context.RoomParticipants.Add(new RoomParticipant
        {
            RoomId = room.Id,
            UserId = user.Id,
            Status = ParticipantStatus.Left
        });
        await _context.SaveChangesAsync();

        var result = await CreateService(CreateRealDeletingUserManager(user))
            .DeleteAccountAsync(user.Id);

        Assert.True(result.Succeeded);
        Assert.False(await _context.Users.AnyAsync(u => u.Id == user.Id));
        Assert.False(await _context.RoomParticipants.AnyAsync(p => p.UserId == user.Id));

        // Someone else's room is not collateral damage.
        Assert.True(await _context.Rooms.AnyAsync(r => r.Id == room.Id));
    }

    [Fact]
    public async Task DeleteAccount_SucceedsForAUserWhoFiledAndReceivedReports()
    {
        var user = AddUser("reporter");
        var other = AddUser("other");
        await _context.SaveChangesAsync();

        _context.Reports.Add(new Report
        {
            Id = Guid.NewGuid(),
            ReporterId = user.Id,
            ReportedUserId = other.Id,
            Category = ReportCategory.Harassment,
            Description = "filed by the departing user"
        });

        var reportAgainstUser = new Report
        {
            Id = Guid.NewGuid(),
            ReporterId = other.Id,
            ReportedUserId = user.Id,
            Category = ReportCategory.Spam,
            Description = "filed against the departing user"
        };
        _context.Reports.Add(reportAgainstUser);
        await _context.SaveChangesAsync();

        var result = await CreateService(CreateRealDeletingUserManager(user))
            .DeleteAccountAsync(user.Id);

        Assert.True(result.Succeeded);
        Assert.False(await _context.Reports.AnyAsync(r => r.ReporterId == user.Id));

        // Reports AGAINST the user survive, anonymised — moderation history is not erasable
        // by deleting your account.
        var surviving = await _context.Reports.SingleAsync(r => r.Id == reportAgainstUser.Id);
        Assert.Null(surviving.ReportedUserId);
    }

    [Fact]
    public async Task DeleteAccount_SucceedsForAUserWithTopicRequestsAndVotes()
    {
        var user = AddUser("voter");
        var other = AddUser("other");
        await _context.SaveChangesAsync();

        var ownRequest = new RoomTopicRequest
        {
            Id = Guid.NewGuid(),
            RequesterId = user.Id,
            TopicTitle = "Requested by the departing user"
        };

        // Someone else's request that merely points at this user as the target coach.
        var othersRequest = new RoomTopicRequest
        {
            Id = Guid.NewGuid(),
            RequesterId = other.Id,
            TargetCoachId = user.Id,
            TopicTitle = "Requested by somebody else"
        };

        _context.RoomTopicRequests.AddRange(ownRequest, othersRequest);
        await _context.SaveChangesAsync();

        _context.TopicVotes.Add(new TopicVote { UserId = user.Id, TopicRequestId = othersRequest.Id });
        await _context.SaveChangesAsync();

        var result = await CreateService(CreateRealDeletingUserManager(user))
            .DeleteAccountAsync(user.Id);

        Assert.True(result.Succeeded);
        Assert.False(await _context.TopicVotes.AnyAsync(v => v.UserId == user.Id));
        Assert.False(await _context.RoomTopicRequests.AnyAsync(tr => tr.Id == ownRequest.Id));

        // The other user's request is kept and simply loses its coach, rather than being
        // destroyed along with the departing account.
        var survivor = await _context.RoomTopicRequests.SingleAsync(tr => tr.Id == othersRequest.Id);
        Assert.Null(survivor.TargetCoachId);
    }

    [Fact]
    public async Task DeleteAccount_SucceedsWhenAHostedRoomHasBeenReported()
    {
        var user = AddUser("host");
        var other = AddUser("other");
        var room = AddRoom(user.Id);
        await _context.SaveChangesAsync();

        // Reports.ReportedRoomId has no cascade configured, so it is NO ACTION at the database
        // and blocks the room delete unless it is nulled first.
        var report = new Report
        {
            Id = Guid.NewGuid(),
            ReporterId = other.Id,
            ReportedRoomId = room.Id,
            Category = ReportCategory.InappropriateContent,
            Description = "reported room"
        };
        _context.Reports.Add(report);

        _context.RoomParticipants.Add(new RoomParticipant
        {
            RoomId = room.Id,
            UserId = other.Id,
            Status = ParticipantStatus.Left
        });
        await _context.SaveChangesAsync();

        var result = await CreateService(CreateRealDeletingUserManager(user))
            .DeleteAccountAsync(user.Id);

        Assert.True(result.Succeeded);
        Assert.False(await _context.Users.AnyAsync(u => u.Id == user.Id));
        Assert.False(await _context.Rooms.AnyAsync(r => r.Id == room.Id));

        var surviving = await _context.Reports.SingleAsync(r => r.Id == report.Id);
        Assert.Null(surviving.ReportedRoomId);
    }

    /// <summary>
    /// The voice sample is biometric data under the PDPL, so erasure has to reach MinIO as well.
    /// Deletion previously left both objects in the bucket indefinitely.
    /// </summary>
    [Fact]
    public async Task DeleteAccount_RemovesTheVoiceRecordingAndProfilePicture()
    {
        var user = AddUser("departing");
        await _context.SaveChangesAsync();

        var voice = new Mock<IUploadVoice>();
        var image = new Mock<IUploadImage>();

        var result = await CreateService(CreateRealDeletingUserManager(user), voice.Object, image.Object)
            .DeleteAccountAsync(user.Id);

        Assert.True(result.Succeeded);
        voice.Verify(v => v.DeleteVoice("voices/departing.mp3"), Times.Once);
        image.Verify(i => i.DeleteImage("profiles/departing.jpg"), Times.Once);
    }

    /// <summary>
    /// The objects must survive a failed deletion: destroying them for an account that still
    /// exists would be unrecoverable.
    /// </summary>
    [Fact]
    public async Task DeleteAccount_KeepsAssetsWhenTheDeletionFails()
    {
        var user = AddUser("staying");
        await _context.SaveChangesAsync();

        var store = new Mock<IUserStore<ApplicationUser>>();
        var manager = new Mock<UserManager<ApplicationUser>>(
            store.Object, null!, null!, null!, null!, null!, null!, null!, null!);
        manager.Setup(m => m.FindByIdAsync(user.Id.ToString())).ReturnsAsync(user);
        manager.Setup(m => m.DeleteAsync(user))
            .ReturnsAsync(IdentityResult.Failed(new IdentityError { Description = "nope" }));

        var voice = new Mock<IUploadVoice>();
        var image = new Mock<IUploadImage>();

        var result = await CreateService(manager, voice.Object, image.Object)
            .DeleteAccountAsync(user.Id);

        Assert.False(result.Succeeded);
        voice.Verify(v => v.DeleteVoice(It.IsAny<string>()), Times.Never);
        image.Verify(i => i.DeleteImage(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task DeleteAccount_RollsBackTheFkCleanupWhenTheDeletionFails()
    {
        var user = AddUser("staying");
        var other = AddUser("host");
        var room = AddRoom(other.Id);
        await _context.SaveChangesAsync();

        _context.RoomParticipants.Add(new RoomParticipant
        {
            RoomId = room.Id,
            UserId = user.Id,
            Status = ParticipantStatus.Left
        });
        await _context.SaveChangesAsync();

        var store = new Mock<IUserStore<ApplicationUser>>();
        var manager = new Mock<UserManager<ApplicationUser>>(
            store.Object, null!, null!, null!, null!, null!, null!, null!, null!);
        manager.Setup(m => m.FindByIdAsync(user.Id.ToString())).ReturnsAsync(user);
        manager.Setup(m => m.DeleteAsync(user))
            .ReturnsAsync(IdentityResult.Failed(new IdentityError { Description = "nope" }));

        var result = await CreateService(manager).DeleteAccountAsync(user.Id);

        Assert.False(result.Succeeded);

        // The account survived, so its participation history must survive with it — a half-
        // dismantled account is worse than a failed deletion.
        Assert.True(await _context.RoomParticipants
            .AnyAsync(p => p.UserId == user.Id && p.RoomId == room.Id));
    }
}
