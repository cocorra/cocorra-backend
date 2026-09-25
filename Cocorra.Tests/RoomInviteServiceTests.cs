// Owns: BE-INVITE-030, BE-INVITE-031, BE-INVITE-032, BE-INVITE-033, BE-INVITE-034, BE-INVITE-035, BE-INVITE-036, BE-INVITE-037, BE-INVITE-038, BE-INVITE-039, BE-INVITE-040, BE-INVITE-041, BE-INVITE-042, BE-INVITE-043, BE-INVITE-044, BE-INVITE-045, BE-INVITE-046, BE-INVITE-047, BE-INVITE-048, BE-INVITE-049, BE-INVITE-050, BE-INVITE-051, BE-INVITE-052, BE-INVITE-053, BE-INVITE-054, BE-INVITE-055, BE-INVITE-056, BE-INVITE-057, BE-INVITE-058, BE-INVITE-059, BE-INVITE-060, BE-INVITE-061, BE-INVITE-062, BE-INVITE-063, BE-INVITE-064, BE-INVITE-065, BE-INVITE-066, BE-INVITE-067
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Cocorra.BLL.Base;
using Cocorra.BLL.Services.EventTracking;
using Cocorra.BLL.Services.RoomInviteService;
using Cocorra.BLL.Services.RoomService;
using Cocorra.DAL.Data;
using Cocorra.DAL.DTOS.RoomDto;
using Cocorra.DAL.DTOS.RoomInviteDto;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;
using Cocorra.DAL.Repository.RoomInviteRepository;
using Cocorra.DAL.Repository.RoomRepository;
using Cocorra.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Cocorra.Tests;

public class RoomInviteServiceTests : IDisposable
{
    private static readonly Regex CodeShapeRegex = new("^[A-Za-z0-9_-]{22}$", RegexOptions.Compiled);

    private readonly SqliteTestHost _host;
    private readonly IServiceScope _scope;
    private readonly AppDbContext _context;
    private readonly RoomInviteRepository _inviteRepo;
    private readonly RoomRepository _roomRepo;
    private readonly Mock<IRoomService> _roomServiceMock = new();
    private readonly Mock<IEventTracker> _eventTrackerMock = new();
    private readonly InviteSettings _settings;
    private readonly RoomInviteService _service;

    public RoomInviteServiceTests()
    {
        _host = new SqliteTestHost();
        _scope = _host.CreateScope();
        _context = _scope.ServiceProvider.GetRequiredService<AppDbContext>();

        _inviteRepo = new RoomInviteRepository(_context);
        _roomRepo = new RoomRepository(_context);

        _settings = new InviteSettings
        {
            PublicBaseUrl = "https://cocorraapp.com",
            DefaultLifetimeHours = 24,
            MaxLifetimeHours = 168,
            MaxActivePerInviterPerRoom = 2
        };

        _service = new RoomInviteService(
            _inviteRepo,
            _roomRepo,
            _roomServiceMock.Object,
            _eventTrackerMock.Object,
            Options.Create(_settings));
    }

    public void Dispose()
    {
        _scope.Dispose();
        _host.Dispose();
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

    private async Task<Room> SeedRoomAsync(
        Guid hostId,
        RoomStatus status = RoomStatus.Live,
        bool isPrivate = false,
        string title = "Coaching Session")
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

    private async Task<RoomParticipant> SeedParticipantAsync(
        Guid roomId,
        Guid userId,
        ParticipantStatus status = ParticipantStatus.Active)
    {
        var participant = new RoomParticipant
        {
            RoomId = roomId,
            UserId = userId,
            Status = status,
            JoinedAt = DateTime.UtcNow
        };
        _context.RoomParticipants.Add(participant);
        await _context.SaveChangesAsync();
        return participant;
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

    // =========================================================================
    // CREATE INVITE TESTS
    // =========================================================================

    [Fact]
    public async Task CreateInviteAsync_HostOfLiveRoom_ReturnsOkWithExpectedPropertiesAndPersistsActiveRow()
    {
        var host = await SeedUserAsync("Host");
        var room = await SeedRoomAsync(host.Id, RoomStatus.Live);

        var before = DateTime.UtcNow;
        var result = await _service.CreateInviteAsync(room.Id, host.Id, null);
        var after = DateTime.UtcNow;

        Assert.True(result.Succeeded);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.NotNull(result.Data);

        var data = result.Data!;
        Assert.Matches(CodeShapeRegex, data.InviteCode);
        Assert.Equal($"{_settings.PublicBaseUrl}/invite/{data.InviteCode}", data.InviteUrl);
        Assert.InRange(data.ExpiresAt, before.AddHours(_settings.DefaultLifetimeHours), after.AddHours(_settings.DefaultLifetimeHours));

        var row = await _context.RoomInvites.SingleOrDefaultAsync(i => i.InviteCode == data.InviteCode);
        Assert.NotNull(row);
        Assert.Equal(RoomInviteStatus.Active, row!.Status);
        Assert.Equal(room.Id, row.RoomId);
        Assert.Equal(host.Id, row.InviterUserId);
    }

    [Fact]
    public async Task CreateInviteAsync_HostOfScheduledRoom_ReturnsOk()
    {
        var host = await SeedUserAsync("Host");
        var room = await SeedRoomAsync(host.Id, RoomStatus.Scheduled);

        var result = await _service.CreateInviteAsync(room.Id, host.Id, null);

        Assert.True(result.Succeeded);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.NotNull(result.Data);
        Assert.Matches(CodeShapeRegex, result.Data!.InviteCode);
    }

    [Fact]
    public async Task CreateInviteAsync_ActiveParticipantOfLiveRoom_ReturnsOk()
    {
        var host = await SeedUserAsync("Host");
        var participant = await SeedUserAsync("Participant");
        var room = await SeedRoomAsync(host.Id, RoomStatus.Live);
        await SeedParticipantAsync(room.Id, participant.Id, ParticipantStatus.Active);

        var result = await _service.CreateInviteAsync(room.Id, participant.Id, null);

        Assert.True(result.Succeeded);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.NotNull(result.Data);
    }

    [Fact]
    public async Task CreateInviteAsync_NonParticipant_ReturnsForbiddenAndPersistsNothing()
    {
        var host = await SeedUserAsync("Host");
        var outsider = await SeedUserAsync("Outsider");
        var room = await SeedRoomAsync(host.Id, RoomStatus.Live);

        var result = await _service.CreateInviteAsync(room.Id, outsider.Id, null);

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.Forbidden, result.StatusCode);
        Assert.Equal("Only the host or an active participant can invite people to this room.", result.Message);
        Assert.Empty(await _context.RoomInvites.ToListAsync());
    }

    [Fact]
    public async Task CreateInviteAsync_LeftParticipant_ReturnsForbiddenAndPersistsNothing()
    {
        var host = await SeedUserAsync("Host");
        var leftUser = await SeedUserAsync("LeftUser");
        var room = await SeedRoomAsync(host.Id, RoomStatus.Live);
        await SeedParticipantAsync(room.Id, leftUser.Id, ParticipantStatus.Left);

        var result = await _service.CreateInviteAsync(room.Id, leftUser.Id, null);

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.Forbidden, result.StatusCode);
        Assert.Equal("Only the host or an active participant can invite people to this room.", result.Message);
        Assert.Empty(await _context.RoomInvites.ToListAsync());
    }

    [Fact]
    public async Task CreateInviteAsync_ActiveParticipantOfScheduledRoom_ReturnsForbiddenAndPersistsNothing()
    {
        var host = await SeedUserAsync("Host");
        var participant = await SeedUserAsync("Participant");
        var room = await SeedRoomAsync(host.Id, RoomStatus.Scheduled);
        await SeedParticipantAsync(room.Id, participant.Id, ParticipantStatus.Active);

        var result = await _service.CreateInviteAsync(room.Id, participant.Id, null);

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.Forbidden, result.StatusCode);
        Assert.Equal("Only the host or an active participant can invite people to this room.", result.Message);
        Assert.Empty(await _context.RoomInvites.ToListAsync());
    }

    [Fact]
    public async Task CreateInviteAsync_RoomNotFound_ReturnsNotFoundAndPersistsNothing()
    {
        var user = await SeedUserAsync("User");

        var result = await _service.CreateInviteAsync(Guid.NewGuid(), user.Id, null);

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.NotFound, result.StatusCode);
        Assert.Equal("Room not found.", result.Message);
        Assert.Empty(await _context.RoomInvites.ToListAsync());
    }

    [Theory]
    [InlineData(RoomStatus.Ended)]
    [InlineData(RoomStatus.Cancelled)]
    public async Task CreateInviteAsync_EndedOrCancelledRoom_ReturnsBadRequestAndPersistsNothing(RoomStatus status)
    {
        var host = await SeedUserAsync("Host");
        var room = await SeedRoomAsync(host.Id, status);

        var result = await _service.CreateInviteAsync(room.Id, host.Id, null);

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        Assert.Equal("This room is no longer available.", result.Message);
        Assert.Empty(await _context.RoomInvites.ToListAsync());
    }

    [Fact]
    public async Task CreateInviteAsync_PrivateRoom_ReturnsBadRequestAndPersistsNothing()
    {
        var host = await SeedUserAsync("Host");
        var room = await SeedRoomAsync(host.Id, RoomStatus.Live, isPrivate: true);

        var result = await _service.CreateInviteAsync(room.Id, host.Id, null);

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        Assert.Equal("Invites are not available for private rooms.", result.Message);
        Assert.Empty(await _context.RoomInvites.ToListAsync());
    }

    [Fact]
    public async Task CreateInviteAsync_ExpiresInHours500_ClampedToMaxLifetimeHours()
    {
        var host = await SeedUserAsync("Host");
        var room = await SeedRoomAsync(host.Id, RoomStatus.Live);

        var before = DateTime.UtcNow;
        var dto = new CreateRoomInviteDto { ExpiresInHours = 500 };
        var result = await _service.CreateInviteAsync(room.Id, host.Id, dto);
        var after = DateTime.UtcNow;

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Data);

        var maxHours = _settings.MaxLifetimeHours; // 168
        Assert.InRange(result.Data!.ExpiresAt, before.AddHours(maxHours), after.AddHours(maxHours));
    }

    [Fact]
    public async Task CreateInviteAsync_QuotaExceeded_ReturnsBadRequestAndRevokedOrExpiredDoesNotCount()
    {
        var host = await SeedUserAsync("Host");
        var room = await SeedRoomAsync(host.Id, RoomStatus.Live);

        // MaxActivePerInviterPerRoom = 2
        var create1 = await _service.CreateInviteAsync(room.Id, host.Id, null);
        var create2 = await _service.CreateInviteAsync(room.Id, host.Id, null);
        Assert.True(create1.Succeeded);
        Assert.True(create2.Succeeded);

        // Third active invite -> 400
        var create3 = await _service.CreateInviteAsync(room.Id, host.Id, null);
        Assert.False(create3.Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, create3.StatusCode);
        Assert.Contains("You already have 2 active invites", create3.Message);

        // Revoking one invite frees quota
        var revokeResult = await _service.RevokeInviteAsync(create1.Data!.InviteCode, host.Id, isAdmin: false);
        Assert.True(revokeResult.Succeeded);

        var create4 = await _service.CreateInviteAsync(room.Id, host.Id, null);
        Assert.True(create4.Succeeded);

        // Expiring an invite in database also frees quota
        var invite2 = await _context.RoomInvites.SingleAsync(i => i.InviteCode == create2.Data!.InviteCode);
        invite2.ExpiresAt = DateTime.UtcNow.AddMinutes(-10);
        await _context.SaveChangesAsync();

        var create5 = await _service.CreateInviteAsync(room.Id, host.Id, null);
        Assert.True(create5.Succeeded);
    }

    [Fact]
    public async Task CreateInviteAsync_TwoCreates_GenerateTwoDifferentCodes()
    {
        var host = await SeedUserAsync("Host");
        var room = await SeedRoomAsync(host.Id, RoomStatus.Live);

        var first = await _service.CreateInviteAsync(room.Id, host.Id, null);
        var second = await _service.CreateInviteAsync(room.Id, host.Id, null);

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);
        Assert.NotEqual(first.Data!.InviteCode, second.Data!.InviteCode);
    }

    // =========================================================================
    // RESOLVE INVITE TESTS
    // =========================================================================

    [Fact]
    public async Task ResolveInviteAsync_ValidActiveInvite_ReturnsOkWithValidTrue()
    {
        var host = await SeedUserAsync("Host");
        var room = await SeedRoomAsync(host.Id, RoomStatus.Live);
        var invite = await SeedInviteAsync(room.Id, host.Id, RoomInviteStatus.Active);

        var result = await _service.ResolveInviteAsync(invite.InviteCode);

        Assert.True(result.Succeeded);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.NotNull(result.Data);
        Assert.True(result.Data!.Valid);
        Assert.Equal(invite.InviteCode, result.Data.InviteCode);
        Assert.Equal(room.Id, result.Data.RoomId);
        Assert.Equal(RoomInviteStatus.Active, result.Data.Status);
    }

    [Fact]
    public async Task ResolveInviteAsync_UnknownWellFormedCode_ReturnsNotFound()
    {
        var wellFormedCode = InviteCodeGenerator.Generate();
        var result = await _service.ResolveInviteAsync(wellFormedCode);

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.NotFound, result.StatusCode);
        Assert.Equal("Invite not found.", result.Message);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("abcdefghij1234567890123")] // 23 chars
    [InlineData("abcdefghij12345678901+")] // contains '+'
    [InlineData("abcdefghij12345678901=")] // contains '='
    public async Task ResolveInviteAsync_MalformedCode_ReturnsNotFoundWithoutQueryingDatabase(string malformedCode)
    {
        var result = await _service.ResolveInviteAsync(malformedCode);

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.NotFound, result.StatusCode);
        Assert.Equal("Invite not found.", result.Message);
    }

    [Fact]
    public async Task ResolveInviteAsync_ExpiresAtInThePast_ReturnsGoneWithValidFalseAndStatusExpired()
    {
        var host = await SeedUserAsync("Host");
        var room = await SeedRoomAsync(host.Id, RoomStatus.Live);
        var pastExpiry = DateTime.UtcNow.AddMinutes(-30);
        var invite = await SeedInviteAsync(room.Id, host.Id, RoomInviteStatus.Active, expiresAt: pastExpiry);

        var result = await _service.ResolveInviteAsync(invite.InviteCode);

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.Gone, result.StatusCode);
        Assert.Equal("This invite has expired.", result.Message);
        Assert.NotNull(result.Data);
        Assert.False(result.Data!.Valid);
        Assert.Null(result.Data.RoomId);
        Assert.Equal(RoomInviteStatus.Expired, result.Data.Status);
    }

    [Fact]
    public async Task ResolveInviteAsync_RoomEndedWhileInviteActive_ReturnsGoneStatusExpired()
    {
        var host = await SeedUserAsync("Host");
        var room = await SeedRoomAsync(host.Id, RoomStatus.Ended);
        var invite = await SeedInviteAsync(room.Id, host.Id, RoomInviteStatus.Active, expiresAt: DateTime.UtcNow.AddHours(5));

        var result = await _service.ResolveInviteAsync(invite.InviteCode);

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.Gone, result.StatusCode);
        Assert.Equal("This invite has expired.", result.Message);
        Assert.NotNull(result.Data);
        Assert.False(result.Data!.Valid);
        Assert.Null(result.Data.RoomId);
        Assert.Equal(RoomInviteStatus.Expired, result.Data.Status);
    }

    [Fact]
    public async Task ResolveInviteAsync_RevokedOrUsedInvite_ReturnsGoneWithMatchingStatus()
    {
        var host = await SeedUserAsync("Host");
        var room = await SeedRoomAsync(host.Id, RoomStatus.Live);

        var revokedInvite = await SeedInviteAsync(room.Id, host.Id, RoomInviteStatus.Revoked);
        var usedInvite = await SeedInviteAsync(room.Id, host.Id, RoomInviteStatus.Used);

        var revokedResult = await _service.ResolveInviteAsync(revokedInvite.InviteCode);
        Assert.False(revokedResult.Succeeded);
        Assert.Equal(HttpStatusCode.Gone, revokedResult.StatusCode);
        Assert.Equal("This invite has been revoked.", revokedResult.Message);
        Assert.Equal(RoomInviteStatus.Revoked, revokedResult.Data!.Status);
        Assert.Null(revokedResult.Data.RoomId);

        var usedResult = await _service.ResolveInviteAsync(usedInvite.InviteCode);
        Assert.False(usedResult.Succeeded);
        Assert.Equal(HttpStatusCode.Gone, usedResult.StatusCode);
        Assert.Equal("This invite has already been used.", usedResult.Message);
        Assert.Equal(RoomInviteStatus.Used, usedResult.Data!.Status);
        Assert.Null(usedResult.Data.RoomId);
    }

    [Fact]
    public async Task ResolveInviteAsync_CalledTwice_DoesNotModifyStoredRow()
    {
        var host = await SeedUserAsync("Host");
        var room = await SeedRoomAsync(host.Id, RoomStatus.Live);
        var invite = await SeedInviteAsync(room.Id, host.Id, RoomInviteStatus.Active);

        var firstResolve = await _service.ResolveInviteAsync(invite.InviteCode);
        var secondResolve = await _service.ResolveInviteAsync(invite.InviteCode);

        Assert.True(firstResolve.Succeeded);
        Assert.True(secondResolve.Succeeded);

        var stored = await _context.RoomInvites.AsNoTracking().SingleAsync(i => i.Id == invite.Id);
        Assert.Equal(RoomInviteStatus.Active, stored.Status);
        Assert.Null(stored.UsedAt);
        Assert.Null(stored.UsedByUserId);
        Assert.Null(stored.RevokedAt);
        Assert.Null(stored.RevokedByUserId);
    }

    // =========================================================================
    // ACCEPT INVITE TESTS
    // =========================================================================

    [Fact]
    public async Task AcceptInviteAsync_ValidInvite_ReturnsOkConsumesInviteAndCommitsParticipant()
    {
        var host = await SeedUserAsync("Host");
        var joiner = await SeedUserAsync("Joiner");
        var room = await SeedRoomAsync(host.Id, RoomStatus.Live);
        var invite = await SeedInviteAsync(room.Id, host.Id, RoomInviteStatus.Active);

        _roomServiceMock.Setup(r => r.JoinRoomAsync(room.Id, joiner.Id))
            .Returns(async () =>
            {
                _context.RoomParticipants.Add(new RoomParticipant
                {
                    RoomId = room.Id,
                    UserId = joiner.Id,
                    Status = ParticipantStatus.Active,
                    JoinedAt = DateTime.UtcNow
                });
                await _context.SaveChangesAsync();
                return new ResponseHandler().Success(new JoinRoomResultDto
                {
                    LiveKitToken = "livekit-token-sample-12345",
                    RoomName = room.Id.ToString()
                });
            });

        var result = await _service.AcceptInviteAsync(invite.InviteCode, joiner.Id);

        Assert.True(result.Succeeded);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.NotNull(result.Data);

        var acceptResult = Assert.IsType<AcceptRoomInviteResultDto>(result.Data);
        Assert.True(acceptResult.InviteConsumed);
        Assert.False(acceptResult.AlreadyParticipant);
        Assert.Equal(room.Id, acceptResult.RoomId);
        Assert.Equal("livekit-token-sample-12345", acceptResult.Join.LiveKitToken);

        // Verify stored invite was flipped to Used
        var storedInvite = await _context.RoomInvites.AsNoTracking().SingleAsync(i => i.Id == invite.Id);
        Assert.Equal(RoomInviteStatus.Used, storedInvite.Status);
        Assert.Equal(joiner.Id, storedInvite.UsedByUserId);
        Assert.NotNull(storedInvite.UsedAt);

        // Verify participant row was committed by the transaction
        var storedParticipant = await _context.RoomParticipants.AsNoTracking()
            .SingleOrDefaultAsync(p => p.RoomId == room.Id && p.UserId == joiner.Id);
        Assert.NotNull(storedParticipant);
        Assert.Equal(ParticipantStatus.Active, storedParticipant!.Status);
    }

    [Fact]
    public async Task AcceptInviteAsync_AcceptedAgainByAnotherUser_ReturnsGoneUsedAndCallsJoinOnceOverall()
    {
        var host = await SeedUserAsync("Host");
        var joiner1 = await SeedUserAsync("Joiner1");
        var joiner2 = await SeedUserAsync("Joiner2");
        var room = await SeedRoomAsync(host.Id, RoomStatus.Live);
        var invite = await SeedInviteAsync(room.Id, host.Id, RoomInviteStatus.Active);

        _roomServiceMock.Setup(r => r.JoinRoomAsync(room.Id, joiner1.Id))
            .Returns(async () =>
            {
                _context.RoomParticipants.Add(new RoomParticipant
                {
                    RoomId = room.Id,
                    UserId = joiner1.Id,
                    Status = ParticipantStatus.Active,
                    JoinedAt = DateTime.UtcNow
                });
                await _context.SaveChangesAsync();
                return new ResponseHandler().Success(new JoinRoomResultDto
                {
                    LiveKitToken = "token-1",
                    RoomName = room.Id.ToString()
                });
            });

        // First accept succeeds
        var firstResult = await _service.AcceptInviteAsync(invite.InviteCode, joiner1.Id);
        Assert.True(firstResult.Succeeded);

        // Second accept by another user returns 410 Gone (Used)
        var secondResult = await _service.AcceptInviteAsync(invite.InviteCode, joiner2.Id);
        Assert.False(secondResult.Succeeded);
        Assert.Equal(HttpStatusCode.Gone, secondResult.StatusCode);
        Assert.Equal("This invite has already been used.", secondResult.Message);

        // JoinRoomAsync must have been invoked exactly ONCE overall
        _roomServiceMock.Verify(r => r.JoinRoomAsync(It.IsAny<Guid>(), It.IsAny<Guid>()), Times.Once);
    }

    [Fact]
    public async Task AcceptInviteAsync_JoinRoomAsyncFails_PropagatesStatusAndMessageAndLeavesInviteActive()
    {
        var host = await SeedUserAsync("Host");
        var joiner = await SeedUserAsync("Joiner");
        var room = await SeedRoomAsync(host.Id, RoomStatus.Live);
        var invite = await SeedInviteAsync(room.Id, host.Id, RoomInviteStatus.Active);

        _roomServiceMock.Setup(r => r.JoinRoomAsync(room.Id, joiner.Id))
            .ReturnsAsync(new ResponseHandler().BadRequest<JoinRoomResultDto>("Room is full."));

        var result = await _service.AcceptInviteAsync(invite.InviteCode, joiner.Id);

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        Assert.Equal("Room is full.", result.Message);

        // Transaction rolled back: invite remains Active
        var stored = await _context.RoomInvites.AsNoTracking().SingleAsync(i => i.Id == invite.Id);
        Assert.Equal(RoomInviteStatus.Active, stored.Status);
        Assert.Null(stored.UsedAt);
        Assert.Null(stored.UsedByUserId);
    }

    [Fact]
    public async Task AcceptInviteAsync_JoinRoomAsyncReturnsEmptyLiveKitToken_ReturnsBadRequestAndLeavesInviteActive()
    {
        var host = await SeedUserAsync("Host");
        var joiner = await SeedUserAsync("Joiner");
        var room = await SeedRoomAsync(host.Id, RoomStatus.Live);
        var invite = await SeedInviteAsync(room.Id, host.Id, RoomInviteStatus.Active);

        _roomServiceMock.Setup(r => r.JoinRoomAsync(room.Id, joiner.Id))
            .ReturnsAsync(new ResponseHandler().Success(new JoinRoomResultDto
            {
                LiveKitToken = string.Empty, // Empty token represents non-success join for public room
                RoomName = room.Id.ToString()
            }));

        var result = await _service.AcceptInviteAsync(invite.InviteCode, joiner.Id);

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        Assert.Equal("Could not join the room with this invite.", result.Message);

        var stored = await _context.RoomInvites.AsNoTracking().SingleAsync(i => i.Id == invite.Id);
        Assert.Equal(RoomInviteStatus.Active, stored.Status);
        Assert.Null(stored.UsedAt);
        Assert.Null(stored.UsedByUserId);
    }

    [Fact]
    public async Task AcceptInviteAsync_InviterAcceptsOwnInvite_ReturnsBadRequestAndDoesNotConsume()
    {
        var host = await SeedUserAsync("Host");
        var room = await SeedRoomAsync(host.Id, RoomStatus.Live);
        var invite = await SeedInviteAsync(room.Id, host.Id, RoomInviteStatus.Active);

        var result = await _service.AcceptInviteAsync(invite.InviteCode, host.Id);

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        Assert.Equal("You cannot accept your own invite.", result.Message);

        var stored = await _context.RoomInvites.AsNoTracking().SingleAsync(i => i.Id == invite.Id);
        Assert.Equal(RoomInviteStatus.Active, stored.Status);
        Assert.Null(stored.UsedAt);
    }

    [Fact]
    public async Task AcceptInviteAsync_PrivateRoom_ReturnsBadRequestAndDoesNotConsume()
    {
        var host = await SeedUserAsync("Host");
        var joiner = await SeedUserAsync("Joiner");
        var room = await SeedRoomAsync(host.Id, RoomStatus.Live, isPrivate: true);
        var invite = await SeedInviteAsync(room.Id, host.Id, RoomInviteStatus.Active);

        var result = await _service.AcceptInviteAsync(invite.InviteCode, joiner.Id);

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        Assert.Equal("Invites are not available for private rooms.", result.Message);

        var stored = await _context.RoomInvites.AsNoTracking().SingleAsync(i => i.Id == invite.Id);
        Assert.Equal(RoomInviteStatus.Active, stored.Status);
        Assert.Null(stored.UsedAt);
    }

    [Fact]
    public async Task AcceptInviteAsync_ExpiredOrRevoked_ReturnsGoneAndDoesNotConsume()
    {
        var host = await SeedUserAsync("Host");
        var joiner = await SeedUserAsync("Joiner");
        var room = await SeedRoomAsync(host.Id, RoomStatus.Live);

        var expiredInvite = await SeedInviteAsync(room.Id, host.Id, RoomInviteStatus.Active, expiresAt: DateTime.UtcNow.AddMinutes(-10));
        var revokedInvite = await SeedInviteAsync(room.Id, host.Id, RoomInviteStatus.Revoked);

        var expiredResult = await _service.AcceptInviteAsync(expiredInvite.InviteCode, joiner.Id);
        Assert.False(expiredResult.Succeeded);
        Assert.Equal(HttpStatusCode.Gone, expiredResult.StatusCode);

        var revokedResult = await _service.AcceptInviteAsync(revokedInvite.InviteCode, joiner.Id);
        Assert.False(revokedResult.Succeeded);
        Assert.Equal(HttpStatusCode.Gone, revokedResult.StatusCode);
    }

    [Fact]
    public async Task AcceptInviteAsync_CallerAlreadyActiveParticipant_ReturnsOkWithoutConsumingInvite()
    {
        var host = await SeedUserAsync("Host");
        var activeParticipant = await SeedUserAsync("ActiveParticipant");
        var room = await SeedRoomAsync(host.Id, RoomStatus.Live);
        await SeedParticipantAsync(room.Id, activeParticipant.Id, ParticipantStatus.Active);

        var invite = await SeedInviteAsync(room.Id, host.Id, RoomInviteStatus.Active);

        _roomServiceMock.Setup(r => r.JoinRoomAsync(room.Id, activeParticipant.Id))
            .ReturnsAsync(new ResponseHandler().Success(new JoinRoomResultDto
            {
                LiveKitToken = "rejoin-token-123",
                RoomName = room.Id.ToString()
            }));

        var result = await _service.AcceptInviteAsync(invite.InviteCode, activeParticipant.Id);

        Assert.True(result.Succeeded);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);

        var data = Assert.IsType<AcceptRoomInviteResultDto>(result.Data);
        Assert.True(data.AlreadyParticipant);
        Assert.False(data.InviteConsumed);
        Assert.Equal("rejoin-token-123", data.Join.LiveKitToken);

        // Invite MUST still be Active
        var stored = await _context.RoomInvites.AsNoTracking().SingleAsync(i => i.Id == invite.Id);
        Assert.Equal(RoomInviteStatus.Active, stored.Status);
        Assert.Null(stored.UsedAt);
    }

    [Fact]
    public async Task AcceptInviteAsync_UnknownCode_ReturnsNotFound()
    {
        var joiner = await SeedUserAsync("Joiner");
        var unknownCode = InviteCodeGenerator.Generate();

        var result = await _service.AcceptInviteAsync(unknownCode, joiner.Id);

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.NotFound, result.StatusCode);
    }

    // =========================================================================
    // REVOKE INVITE TESTS
    // =========================================================================

    [Fact]
    public async Task RevokeInviteAsync_Inviter_ReturnsOkRevokedWithTimestamps()
    {
        var host = await SeedUserAsync("Host");
        var participant = await SeedUserAsync("InviterParticipant");
        var room = await SeedRoomAsync(host.Id, RoomStatus.Live);
        await SeedParticipantAsync(room.Id, participant.Id, ParticipantStatus.Active);

        var invite = await SeedInviteAsync(room.Id, participant.Id, RoomInviteStatus.Active);

        var result = await _service.RevokeInviteAsync(invite.InviteCode, participant.Id, isAdmin: false);

        Assert.True(result.Succeeded);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.NotNull(result.Data);
        Assert.Equal(RoomInviteStatus.Revoked, result.Data!.Status);

        var stored = await _context.RoomInvites.AsNoTracking().SingleAsync(i => i.Id == invite.Id);
        Assert.Equal(RoomInviteStatus.Revoked, stored.Status);
        Assert.Equal(participant.Id, stored.RevokedByUserId);
        Assert.NotNull(stored.RevokedAt);
    }

    [Fact]
    public async Task RevokeInviteAsync_RoomHost_ReturnsOkRevoked()
    {
        var host = await SeedUserAsync("Host");
        var participant = await SeedUserAsync("InviterParticipant");
        var room = await SeedRoomAsync(host.Id, RoomStatus.Live);
        await SeedParticipantAsync(room.Id, participant.Id, ParticipantStatus.Active);

        var invite = await SeedInviteAsync(room.Id, participant.Id, RoomInviteStatus.Active);

        // Host revokes participant's invite
        var result = await _service.RevokeInviteAsync(invite.InviteCode, host.Id, isAdmin: false);

        Assert.True(result.Succeeded);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal(RoomInviteStatus.Revoked, result.Data!.Status);

        var stored = await _context.RoomInvites.AsNoTracking().SingleAsync(i => i.Id == invite.Id);
        Assert.Equal(RoomInviteStatus.Revoked, stored.Status);
        Assert.Equal(host.Id, stored.RevokedByUserId);
    }

    [Fact]
    public async Task RevokeInviteAsync_AdminFlagTrue_ReturnsOkRevoked()
    {
        var host = await SeedUserAsync("Host");
        var admin = await SeedUserAsync("AdminUser");
        var room = await SeedRoomAsync(host.Id, RoomStatus.Live);
        var invite = await SeedInviteAsync(room.Id, host.Id, RoomInviteStatus.Active);

        var result = await _service.RevokeInviteAsync(invite.InviteCode, admin.Id, isAdmin: true);

        Assert.True(result.Succeeded);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal(RoomInviteStatus.Revoked, result.Data!.Status);

        var stored = await _context.RoomInvites.AsNoTracking().SingleAsync(i => i.Id == invite.Id);
        Assert.Equal(RoomInviteStatus.Revoked, stored.Status);
        Assert.Equal(admin.Id, stored.RevokedByUserId);
    }

    [Fact]
    public async Task RevokeInviteAsync_UnrelatedUser_ReturnsForbiddenAndLeavesInviteActive()
    {
        var host = await SeedUserAsync("Host");
        var bystander = await SeedUserAsync("Bystander");
        var room = await SeedRoomAsync(host.Id, RoomStatus.Live);
        var invite = await SeedInviteAsync(room.Id, host.Id, RoomInviteStatus.Active);

        var result = await _service.RevokeInviteAsync(invite.InviteCode, bystander.Id, isAdmin: false);

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.Forbidden, result.StatusCode);
        Assert.Equal("Only the inviter, the room host or an admin can revoke this invite.", result.Message);

        var stored = await _context.RoomInvites.AsNoTracking().SingleAsync(i => i.Id == invite.Id);
        Assert.Equal(RoomInviteStatus.Active, stored.Status);
        Assert.Null(stored.RevokedAt);
    }

    [Fact]
    public async Task RevokeInviteAsync_AlreadyUsed_ReturnsGone()
    {
        var host = await SeedUserAsync("Host");
        var room = await SeedRoomAsync(host.Id, RoomStatus.Live);
        var invite = await SeedInviteAsync(room.Id, host.Id, RoomInviteStatus.Used);

        var result = await _service.RevokeInviteAsync(invite.InviteCode, host.Id, isAdmin: false);

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.Gone, result.StatusCode);
        Assert.Equal("This invite has already been used.", result.Message);
    }

    // =========================================================================
    // STATS TESTS
    // =========================================================================

    [Fact]
    public async Task GetStatsAsync_DirectlySeededMix_ComputesAllLifecycleStatsAccurately()
    {
        var host = await SeedUserAsync("Host");
        var userA = await SeedUserAsync("UserA");
        var userB = await SeedUserAsync("UserB");

        var liveRoom = await SeedRoomAsync(host.Id, RoomStatus.Live, title: "Live Room");
        var endedRoom = await SeedRoomAsync(host.Id, RoomStatus.Ended, title: "Ended Room");

        var now = DateTime.UtcNow;

        // 1 & 2. Active invites (live room, unexpired)
        await SeedInviteAsync(liveRoom.Id, host.Id, RoomInviteStatus.Active, expiresAt: now.AddHours(10));
        await SeedInviteAsync(liveRoom.Id, host.Id, RoomInviteStatus.Active, expiresAt: now.AddHours(12));

        // 3 & 4. Used invites accepted by userA
        await SeedInviteAsync(liveRoom.Id, host.Id, RoomInviteStatus.Used, usedByUserId: userA.Id, usedAt: now.AddMinutes(-50));
        await SeedInviteAsync(liveRoom.Id, host.Id, RoomInviteStatus.Used, usedByUserId: userA.Id, usedAt: now.AddMinutes(-40));

        // 5. Used invite accepted by userB
        await SeedInviteAsync(liveRoom.Id, host.Id, RoomInviteStatus.Used, usedByUserId: userB.Id, usedAt: now.AddMinutes(-30));

        // 6. Expired invite by time
        await SeedInviteAsync(liveRoom.Id, host.Id, RoomInviteStatus.Active, expiresAt: now.AddMinutes(-10));

        // 7. Expired invite by room status (room ended while invite Active)
        await SeedInviteAsync(endedRoom.Id, host.Id, RoomInviteStatus.Active, expiresAt: now.AddHours(5));

        // 8. Revoked invite
        await SeedInviteAsync(liveRoom.Id, host.Id, RoomInviteStatus.Revoked, revokedByUserId: host.Id, revokedAt: now.AddMinutes(-20));

        _eventTrackerMock.Setup(t => t.NewEventEmissionEnabled).Returns(false);

        var result = await _service.GetStatsAsync(fromUtc: null, toUtc: null, roomId: null, inviterUserId: null);

        Assert.True(result.Succeeded);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.NotNull(result.Data);

        var stats = result.Data!;
        Assert.Equal(8, stats.TotalCreated);
        Assert.Equal(2, stats.Active);
        Assert.Equal(3, stats.Used);
        Assert.Equal(2, stats.Expired); // 1 time-expired + 1 room-ended
        Assert.Equal(1, stats.Revoked);
        Assert.Equal(3, stats.SuccessfulAcceptances);
        Assert.Equal(2, stats.UniqueAcceptedUsers); // userA + userB
    }

    [Fact]
    public async Task GetStatsAsync_FromGreaterThanTo_ReturnsBadRequest()
    {
        var from = DateTime.UtcNow.AddDays(2);
        var to = DateTime.UtcNow.AddDays(1);

        var result = await _service.GetStatsAsync(from, to, roomId: null, inviterUserId: null);

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        Assert.Equal("'from' must be earlier than 'to'.", result.Message);
    }

    [Fact]
    public async Task GetStatsAsync_NewEventEmissionDisabled_ReturnsEventCountsAvailableFalseAndNullEventCounts()
    {
        _eventTrackerMock.Setup(t => t.NewEventEmissionEnabled).Returns(false);

        var result = await _service.GetStatsAsync(fromUtc: null, toUtc: null, roomId: null, inviterUserId: null);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Data);
        Assert.False(result.Data!.EventCountsAvailable);
        Assert.Null(result.Data.Resolved);
        Assert.Null(result.Data.AcceptAttempts);
        Assert.Null(result.Data.FailedAcceptances);
    }

    [Fact]
    public async Task GetStatsAsync_NewEventEmissionEnabled_ReturnsPopulatedEventCountsFromUserEvents()
    {
        var host = await SeedUserAsync("Host");
        var room = await SeedRoomAsync(host.Id, RoomStatus.Live);

        _eventTrackerMock.Setup(t => t.NewEventEmissionEnabled).Returns(true);

        var t0 = DateTime.UtcNow.AddHours(-1);

        var events = new List<UserEvent>
        {
            new() { EventId = Guid.NewGuid(), EventType = EventTypes.RoomInviteResolved, RoomId = room.Id, OccurredAtUtc = t0.AddMinutes(5) },
            new() { EventId = Guid.NewGuid(), EventType = EventTypes.RoomInviteResolved, RoomId = room.Id, OccurredAtUtc = t0.AddMinutes(10) },
            new() { EventId = Guid.NewGuid(), EventType = EventTypes.RoomInviteResolved, RoomId = room.Id, OccurredAtUtc = t0.AddMinutes(15) },
            new() { EventId = Guid.NewGuid(), EventType = EventTypes.RoomInviteAcceptAttempted, RoomId = room.Id, OccurredAtUtc = t0.AddMinutes(20) },
            new() { EventId = Guid.NewGuid(), EventType = EventTypes.RoomInviteAcceptAttempted, RoomId = room.Id, OccurredAtUtc = t0.AddMinutes(25) },
            new() { EventId = Guid.NewGuid(), EventType = EventTypes.RoomInviteAcceptFailed, RoomId = room.Id, OccurredAtUtc = t0.AddMinutes(30) }
        };
        _context.UserEvents.AddRange(events);
        await _context.SaveChangesAsync();

        var result = await _service.GetStatsAsync(fromUtc: null, toUtc: null, roomId: room.Id, inviterUserId: null);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Data);
        Assert.True(result.Data!.EventCountsAvailable);
        Assert.Equal(3, result.Data.Resolved);
        Assert.Equal(2, result.Data.AcceptAttempts);
        Assert.Equal(1, result.Data.FailedAcceptances);
    }

    // =========================================================================
    // EVENT PRIVACY TEST
    // =========================================================================

    [Fact]
    public async Task Events_TrackIsNeverCalledWithPropertiesContainingInviteCode()
    {
        var host = await SeedUserAsync("Host");
        var participant = await SeedUserAsync("Joiner");
        var room = await SeedRoomAsync(host.Id, RoomStatus.Live);

        _eventTrackerMock.Setup(t => t.NewEventEmissionEnabled).Returns(true);

        var trackedProperties = new List<object?>();
        _eventTrackerMock
            .Setup(t => t.Track(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<object?>()))
            .Callback<string, Guid?, object?>((_, _, props) => trackedProperties.Add(props));

        _roomServiceMock.Setup(r => r.JoinRoomAsync(room.Id, participant.Id))
            .ReturnsAsync(new ResponseHandler().Success(new JoinRoomResultDto
            {
                LiveKitToken = "token-123",
                RoomName = room.Id.ToString()
            }));

        // 1. Create invite (emits RoomInviteCreated)
        var createResult = await _service.CreateInviteAsync(room.Id, host.Id, null);
        var inviteCode1 = createResult.Data!.InviteCode;

        // 2. Resolve invite (emits RoomInviteResolved)
        await _service.ResolveInviteAsync(inviteCode1);

        // 3. Accept invite (emits RoomInviteAcceptAttempted and RoomInviteAccepted)
        await _service.AcceptInviteAsync(inviteCode1, participant.Id);

        // 4. Create another invite and revoke it (emits RoomInviteRevoked)
        var createResult2 = await _service.CreateInviteAsync(room.Id, host.Id, null);
        var inviteCode2 = createResult2.Data!.InviteCode;
        await _service.RevokeInviteAsync(inviteCode2, host.Id, isAdmin: false);

        // 5. Try accept on revoked invite (emits RoomInviteAcceptAttempted and RoomInviteAcceptFailed)
        await _service.AcceptInviteAsync(inviteCode2, participant.Id);

        Assert.NotEmpty(trackedProperties);

        foreach (var props in trackedProperties)
        {
            Assert.NotNull(props);
            var json = JsonSerializer.Serialize(props);
            Assert.DoesNotContain(inviteCode1, json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(inviteCode2, json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("inviteCode", json, StringComparison.OrdinalIgnoreCase);
        }
    }
}
