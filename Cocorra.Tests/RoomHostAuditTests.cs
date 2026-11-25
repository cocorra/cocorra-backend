using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Cocorra.API.Hubs;
using Cocorra.BLL.Services.AuditLogs;
using Cocorra.BLL.Services.ChatService;
using Cocorra.BLL.Services.EventTracking;
using Cocorra.BLL.Services.LiveKit;
using Cocorra.BLL.Services.NotificationService;
using Cocorra.BLL.Services.RoomService;
using Cocorra.BLL.Services.Upload;
using Cocorra.DAL.Data;
using Cocorra.DAL.DTOS.RoomDto;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;
using Cocorra.DAL.Repository.AuditLogRepository;
using Cocorra.DAL.Repository.RoomRepository;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Cocorra.Tests;

public class RoomHostAuditTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _services;
    private readonly Mock<IMediator> _mediatorMock = new();
    private readonly Mock<IUploadImage> _uploadImageMock = new();
    private readonly Mock<IPushNotificationService> _pushServiceMock = new();
    private readonly Mock<ILiveKitService> _liveKitServiceMock = new();
    private readonly Mock<IEventTracker> _eventTrackerMock = new();
    private readonly Mock<IHttpContextAccessor> _httpContextAccessorMock = new();
    private readonly LiveKitSettings _liveKitSettings = new()
    {
        ServerUrl = "wss://test.livekit.dev",
        ApiKey = "key",
        ApiSecret = "secret"
    };

    public RoomHostAuditTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var serviceCollection = new ServiceCollection();
        serviceCollection.AddLogging();
        serviceCollection.AddDbContext<AppDbContext>(options => options.UseSqlite(_connection));
        serviceCollection.AddIdentity<ApplicationUser, IdentityRole<Guid>>(options =>
        {
            options.User.RequireUniqueEmail = false;
        })
        .AddEntityFrameworkStores<AppDbContext>()
        .AddDefaultTokenProviders();

        _services = serviceCollection.BuildServiceProvider();

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Database.EnsureCreated();

        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        foreach (var role in AppRoles.All)
        {
            roleManager.CreateAsync(new IdentityRole<Guid>(role)).GetAwaiter().GetResult();
        }
    }

    public void Dispose()
    {
        _services.Dispose();
        _connection.Dispose();
    }

    private async Task<ApplicationUser> CreateUserAsync(string role, string firstName, string lastName)
    {
        using var scope = _services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var id = Guid.NewGuid();
        var user = new ApplicationUser
        {
            Id = id,
            UserName = $"{firstName.ToLowerInvariant()}-{id:N}",
            Email = $"{firstName.ToLowerInvariant()}-{id:N}@cocorra.test",
            FirstName = firstName,
            LastName = lastName,
            Status = UserStatus.Active
        };

        var created = await userManager.CreateAsync(user);
        Assert.True(created.Succeeded);
        var roled = await userManager.AddToRoleAsync(user, role);
        Assert.True(roled.Succeeded);

        return user;
    }

    private (IServiceScope Scope, RoomService Service) CreateRoomService()
    {
        var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var auditLog = new AuditLogService(
            new AuditLogRepository(db),
            _httpContextAccessorMock.Object,
            NullLogger<AuditLogService>.Instance);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AppSettings:BaseUrl"] = "https://api.example.com" })
            .Build();

        var service = new RoomService(
            new RoomRepository(db),
            _mediatorMock.Object,
            _uploadImageMock.Object,
            config,
            _pushServiceMock.Object,
            userManager,
            _liveKitServiceMock.Object,
            Options.Create(_liveKitSettings),
            _eventTrackerMock.Object,
            NullLogger<RoomService>.Instance,
            auditLog);

        return (scope, service);
    }

    private (IServiceScope Scope, RoomHub Hub, Mock<IClientProxy> GroupProxy) CreateRoomHub(
        Guid callerId,
        string[] callerRoles,
        IAuditLogService? auditLogOverride = null)
    {
        var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var auditLog = auditLogOverride ?? new AuditLogService(
            new AuditLogRepository(db),
            _httpContextAccessorMock.Object,
            NullLogger<AuditLogService>.Instance);

        var hub = new RoomHub(
            new RoomRepository(db),
            new Mock<IRoomService>().Object,
            new Mock<IChatService>().Object,
            _liveKitServiceMock.Object,
            Options.Create(_liveKitSettings),
            _eventTrackerMock.Object,
            Options.Create(new RoomLifecycleSettings()),
            NullLogger<RoomHub>.Instance,
            auditLog);

        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, callerId.ToString()) };
        foreach (var role in callerRoles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        var contextMock = new Mock<HubCallerContext>();
        contextMock.Setup(c => c.User).Returns(new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth")));
        contextMock.Setup(c => c.ConnectionId).Returns($"room-conn-{Guid.NewGuid():N}");
        contextMock.Setup(c => c.UserIdentifier).Returns(callerId.ToString());
        contextMock.Setup(c => c.ConnectionAborted).Returns(CancellationToken.None);

        var clientsMock = new Mock<IHubCallerClients>();
        var groupProxyMock = new Mock<IClientProxy>();
        var callerProxyMock = new Mock<ISingleClientProxy>();
        clientsMock.Setup(c => c.Group(It.IsAny<string>())).Returns(groupProxyMock.Object);
        clientsMock.Setup(c => c.Caller).Returns(callerProxyMock.Object);

        hub.Context = contextMock.Object;
        hub.Clients = clientsMock.Object;
        hub.Groups = new Mock<IGroupManager>().Object;

        return (scope, hub, groupProxyMock);
    }

    private async Task<Room> SeedRoomAsync(Guid hostId, string title, RoomStatus status, bool isPrivate = false)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var room = new Room
        {
            Id = Guid.NewGuid(),
            HostId = hostId,
            RoomTitle = title,
            Description = "Room description",
            TotalCapacity = 30,
            StageCapacity = 5,
            DefaultSpeakerDurationMinutes = 5,
            DurationHours = 2,
            IsPrivate = isPrivate,
            Status = status,
            StartDate = DateTime.UtcNow.AddHours(status == RoomStatus.Scheduled ? 1 : -1),
            WentLiveAt = status == RoomStatus.Live ? DateTime.UtcNow.AddMinutes(-10) : null,
            Category = RoomCategory.MentalHealth
        };
        db.Rooms.Add(room);
        await db.SaveChangesAsync();
        return room;
    }

    private async Task<RoomParticipant> SeedParticipantAsync(
        Guid roomId,
        Guid userId,
        ParticipantStatus status = ParticipantStatus.Active,
        bool isOnStage = false,
        bool isMuted = true,
        bool isHandRaised = false)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var participant = new RoomParticipant
        {
            RoomId = roomId,
            UserId = userId,
            Status = status,
            IsOnStage = isOnStage,
            IsMuted = isMuted,
            IsHandRaised = isHandRaised,
            JoinedAt = DateTime.UtcNow
        };
        db.RoomParticipants.Add(participant);
        await db.SaveChangesAsync();
        return participant;
    }

    private async Task<List<AuditLog>> ReadAuditLogsAsync()
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.AuditLogs.AsNoTracking().OrderBy(a => a.Id).ToListAsync();
    }

    private async Task<RoomParticipant> ReadParticipantAsync(Guid roomId, Guid userId)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.RoomParticipants.AsNoTracking().SingleAsync(p => p.RoomId == roomId && p.UserId == userId);
    }

    private async Task<Room> ReadRoomAsync(Guid roomId)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Rooms.AsNoTracking().SingleAsync(r => r.Id == roomId);
    }

    [Fact]
    public async Task CreateRoomAsync_ValidRoom_PersistsRoomAndWritesRoomCreatedAudit()
    {
        var host = await CreateUserAsync(AppRoles.Coach, "Coach", "Maya");
        var dto = new CreateRoomDto
        {
            RoomTitle = "Evening Reflection",
            Description = "Open discussion",
            DurationHours = 2,
            TotalCapacity = 40,
            StageCapacity = 4,
            DefaultSpeakerDurationMinutes = 5,
            Category = RoomCategory.MentalHealth
        };

        Guid roomId;
        var (scope, service) = CreateRoomService();
        using (scope)
        {
            var result = await service.CreateRoomAsync(dto, host.Id);

            Assert.True(result.Succeeded);
            roomId = result.Data;
        }

        var room = await ReadRoomAsync(roomId);
        Assert.Equal("Evening Reflection", room.RoomTitle);

        var audit = Assert.Single(await ReadAuditLogsAsync());
        Assert.Equal(AuditActions.RoomCreated, audit.Action);
        Assert.Equal(AuditCategories.Room, audit.Category);
        Assert.Equal(AuditEntityTypes.Room, audit.EntityType);
        Assert.Equal(roomId.ToString(), audit.EntityId);
        Assert.Equal(host.Id, audit.ActorUserId);
        Assert.Null(audit.TargetUserId);

        using var doc = JsonDocument.Parse(audit.DetailsJson!);
        Assert.Equal("Evening Reflection", doc.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public async Task StartScheduledRoomAsync_ByHost_TransitionsToLiveAndWritesRoomStartedAudit()
    {
        var host = await CreateUserAsync(AppRoles.Coach, "Coach", "Nour");
        var room = await SeedRoomAsync(host.Id, "Scheduled Session", RoomStatus.Scheduled);

        var (scope, service) = CreateRoomService();
        using (scope)
        {
            var result = await service.StartScheduledRoomAsync(room.Id, host.Id);
            Assert.True(result.Succeeded);
        }

        var persisted = await ReadRoomAsync(room.Id);
        Assert.Equal(RoomStatus.Live, persisted.Status);

        var audit = Assert.Single(await ReadAuditLogsAsync());
        Assert.Equal(AuditActions.RoomStarted, audit.Action);
        Assert.Equal(AuditCategories.Room, audit.Category);
        Assert.Equal(AuditEntityTypes.Room, audit.EntityType);
        Assert.Equal(room.Id.ToString(), audit.EntityId);
        Assert.Equal(host.Id, audit.ActorUserId);
        Assert.Null(audit.TargetUserId);

        using var doc = JsonDocument.Parse(audit.DetailsJson!);
        Assert.Equal("Scheduled Session", doc.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public async Task ApproveUserAsync_ByHost_ActivatesParticipantAndWritesRoomUserApprovedAudit()
    {
        var host = await CreateUserAsync(AppRoles.Coach, "Coach", "Layla");
        var guest = await CreateUserAsync(AppRoles.User, "Guest", "Omar");
        var room = await SeedRoomAsync(host.Id, "Private Circle", RoomStatus.Live, isPrivate: true);
        await SeedParticipantAsync(room.Id, guest.Id, status: ParticipantStatus.PendingApproval);

        var (scope, service) = CreateRoomService();
        using (scope)
        {
            var result = await service.ApproveUserAsync(room.Id, guest.Id, host.Id);
            Assert.True(result.Succeeded);
        }

        var participant = await ReadParticipantAsync(room.Id, guest.Id);
        Assert.Equal(ParticipantStatus.Active, participant.Status);

        var audit = Assert.Single(await ReadAuditLogsAsync());
        Assert.Equal(AuditActions.RoomUserApproved, audit.Action);
        Assert.Equal(AuditCategories.Room, audit.Category);
        Assert.Equal(AuditEntityTypes.Room, audit.EntityType);
        Assert.Equal(room.Id.ToString(), audit.EntityId);
        Assert.Equal(host.Id, audit.ActorUserId);
        Assert.Equal(guest.Id, audit.TargetUserId);

        using var doc = JsonDocument.Parse(audit.DetailsJson!);
        Assert.Equal("Private Circle", doc.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public async Task EndRoomAsync_HostEnded_EndsRoomAndWritesRoomEndedAuditWithActorRole()
    {
        var host = await CreateUserAsync(AppRoles.Coach, "Coach", "Karim");
        var room = await SeedRoomAsync(host.Id, "Live Coaching", RoomStatus.Live);

        var (scope, service) = CreateRoomService();
        using (scope)
        {
            var result = await service.EndRoomAsync(room.Id, host.Id, RoomEndReasons.HostEnded);
            Assert.True(result.Succeeded);
        }

        var persisted = await ReadRoomAsync(room.Id);
        Assert.Equal(RoomStatus.Ended, persisted.Status);

        var audit = Assert.Single(await ReadAuditLogsAsync());
        Assert.Equal(AuditActions.RoomEnded, audit.Action);
        Assert.Equal(AuditCategories.Room, audit.Category);
        Assert.Equal(AuditEntityTypes.Room, audit.EntityType);
        Assert.Equal(room.Id.ToString(), audit.EntityId);
        Assert.Equal(host.Id, audit.ActorUserId);
        Assert.Equal(AppRoles.Coach, audit.ActorRole);
        Assert.Null(audit.TargetUserId);

        using var doc = JsonDocument.Parse(audit.DetailsJson!);
        Assert.Equal("Live Coaching", doc.RootElement.GetProperty("title").GetString());
    }

    [Theory]
    [InlineData(RoomEndReasons.DurationElapsed)]
    [InlineData(RoomEndReasons.HostDisconnected)]
    [InlineData(RoomEndReasons.HostAccountDeleted)]
    public async Task EndRoomAsync_SystemEndReasons_EndsRoomAndWritesNoRoomEndedAudit(string systemEndReason)
    {
        var host = await CreateUserAsync(AppRoles.Coach, "Coach", "SystemEnd");
        var room = await SeedRoomAsync(host.Id, "Auto Ended Room", RoomStatus.Live);

        var (scope, service) = CreateRoomService();
        using (scope)
        {
            var result = await service.EndRoomAsync(room.Id, host.Id, systemEndReason);
            Assert.True(result.Succeeded);
        }

        var persisted = await ReadRoomAsync(room.Id);
        Assert.Equal(RoomStatus.Ended, persisted.Status);
        Assert.Empty(await ReadAuditLogsAsync());
    }

    [Fact]
    public async Task RoomService_NonHostOperations_RefusedAndWriteNoAudit()
    {
        var host = await CreateUserAsync(AppRoles.Coach, "Coach", "Owner");
        var intruder = await CreateUserAsync(AppRoles.User, "Other", "User");
        var guest = await CreateUserAsync(AppRoles.User, "Guest", "Pending");
        var scheduledRoom = await SeedRoomAsync(host.Id, "Scheduled", RoomStatus.Scheduled);
        var liveRoom = await SeedRoomAsync(host.Id, "Live", RoomStatus.Live, isPrivate: true);
        await SeedParticipantAsync(liveRoom.Id, guest.Id, status: ParticipantStatus.PendingApproval);

        var (scope, service) = CreateRoomService();
        using (scope)
        {
            Assert.False((await service.StartScheduledRoomAsync(scheduledRoom.Id, intruder.Id)).Succeeded);
            Assert.False((await service.ApproveUserAsync(liveRoom.Id, guest.Id, intruder.Id)).Succeeded);
            Assert.False((await service.EndRoomAsync(liveRoom.Id, intruder.Id)).Succeeded);
        }

        Assert.Equal(RoomStatus.Scheduled, (await ReadRoomAsync(scheduledRoom.Id)).Status);
        Assert.Equal(RoomStatus.Live, (await ReadRoomAsync(liveRoom.Id)).Status);
        Assert.Equal(ParticipantStatus.PendingApproval, (await ReadParticipantAsync(liveRoom.Id, guest.Id)).Status);
        Assert.Empty(await ReadAuditLogsAsync());
    }

    [Fact]
    public async Task RoomHub_ApproveToStage_ByHost_WritesAuditWithActorRoleAndTargetUserId()
    {
        var host = await CreateUserAsync(AppRoles.Coach, "Coach", "Hana");
        var speaker = await CreateUserAsync(AppRoles.User, "Speaker", "Youssef");
        var room = await SeedRoomAsync(host.Id, "Stage Room", RoomStatus.Live);
        await SeedParticipantAsync(room.Id, speaker.Id, isOnStage: false, isHandRaised: true);

        var (scope, hub, _) = CreateRoomHub(host.Id, new[] { AppRoles.Coach });
        using (scope)
        {
            await hub.ApproveToStage(room.Id.ToString(), speaker.Id.ToString());
        }

        var participant = await ReadParticipantAsync(room.Id, speaker.Id);
        Assert.True(participant.IsOnStage);
        Assert.False(participant.IsHandRaised);

        var audit = Assert.Single(await ReadAuditLogsAsync());
        Assert.Equal(AuditActions.RoomStageApproved, audit.Action);
        Assert.Equal(AuditCategories.Room, audit.Category);
        Assert.Equal(AuditEntityTypes.Room, audit.EntityType);
        Assert.Equal(room.Id.ToString(), audit.EntityId);
        Assert.Equal(host.Id, audit.ActorUserId);
        Assert.Equal(AppRoles.Coach, audit.ActorRole);
        Assert.Equal(speaker.Id, audit.TargetUserId);

        using var doc = JsonDocument.Parse(audit.DetailsJson!);
        Assert.Equal("Stage Room", doc.RootElement.GetProperty("title").GetString());
        Assert.True(doc.RootElement.GetProperty("viaHandRaise").GetBoolean());
    }

    [Fact]
    public async Task RoomHub_MoveToAudience_ByHost_WritesAuditWithActorRoleAndTargetUserId()
    {
        var host = await CreateUserAsync(AppRoles.Coach, "Coach", "Hana");
        var speaker = await CreateUserAsync(AppRoles.User, "Speaker", "Tariq");
        var room = await SeedRoomAsync(host.Id, "Demote Room", RoomStatus.Live);
        await SeedParticipantAsync(room.Id, speaker.Id, isOnStage: true, isMuted: false);

        var (scope, hub, _) = CreateRoomHub(host.Id, new[] { AppRoles.Admin, AppRoles.Coach });
        using (scope)
        {
            await hub.MoveToAudience(room.Id.ToString(), speaker.Id.ToString());
        }

        var participant = await ReadParticipantAsync(room.Id, speaker.Id);
        Assert.False(participant.IsOnStage);
        Assert.True(participant.IsMuted);

        var audit = Assert.Single(await ReadAuditLogsAsync());
        Assert.Equal(AuditActions.RoomMovedToAudience, audit.Action);
        Assert.Equal(AuditCategories.Room, audit.Category);
        Assert.Equal(AuditEntityTypes.Room, audit.EntityType);
        Assert.Equal(room.Id.ToString(), audit.EntityId);
        Assert.Equal(host.Id, audit.ActorUserId);
        Assert.Equal($"{AppRoles.Admin},{AppRoles.Coach}", audit.ActorRole);
        Assert.Equal(speaker.Id, audit.TargetUserId);

        using var doc = JsonDocument.Parse(audit.DetailsJson!);
        Assert.Equal("Demote Room", doc.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public async Task RoomHub_KickUser_ByHost_WritesAuditWithActorRoleAndTargetUserId()
    {
        var host = await CreateUserAsync(AppRoles.Coach, "Coach", "Hana");
        var target = await CreateUserAsync(AppRoles.User, "Target", "Member");
        var room = await SeedRoomAsync(host.Id, "Kick Room", RoomStatus.Live);
        await SeedParticipantAsync(room.Id, target.Id, status: ParticipantStatus.Active, isOnStage: true);

        var (scope, hub, _) = CreateRoomHub(host.Id, new[] { AppRoles.Coach });
        using (scope)
        {
            await hub.KickUser(room.Id.ToString(), target.Id.ToString());
        }

        var participant = await ReadParticipantAsync(room.Id, target.Id);
        Assert.Equal(ParticipantStatus.Kicked, participant.Status);
        Assert.False(participant.IsOnStage);

        var audit = Assert.Single(await ReadAuditLogsAsync());
        Assert.Equal(AuditActions.RoomUserKicked, audit.Action);
        Assert.Equal(AuditCategories.Room, audit.Category);
        Assert.Equal(AuditEntityTypes.Room, audit.EntityType);
        Assert.Equal(room.Id.ToString(), audit.EntityId);
        Assert.Equal(host.Id, audit.ActorUserId);
        Assert.Equal(AppRoles.Coach, audit.ActorRole);
        Assert.Equal(target.Id, audit.TargetUserId);

        using var doc = JsonDocument.Parse(audit.DetailsJson!);
        Assert.Equal("Kick Room", doc.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public async Task RoomHub_GrantExtraTime_ByHost_WritesAuditWithActorRoleAndTargetUserId()
    {
        var host = await CreateUserAsync(AppRoles.Coach, "Coach", "Hana");
        var speaker = await CreateUserAsync(AppRoles.User, "Speaker", "Salma");
        var room = await SeedRoomAsync(host.Id, "Timer Room", RoomStatus.Live);
        await SeedParticipantAsync(room.Id, speaker.Id, isOnStage: true);

        var (scope, hub, _) = CreateRoomHub(host.Id, new[] { AppRoles.Coach });
        using (scope)
        {
            await hub.GrantExtraTime(room.Id.ToString(), speaker.Id.ToString(), 5);
        }

        var participant = await ReadParticipantAsync(room.Id, speaker.Id);
        Assert.Equal(5, participant.ExtraMinutesGranted);

        var audit = Assert.Single(await ReadAuditLogsAsync());
        Assert.Equal(AuditActions.RoomExtraTimeGranted, audit.Action);
        Assert.Equal(AuditCategories.Room, audit.Category);
        Assert.Equal(AuditEntityTypes.Room, audit.EntityType);
        Assert.Equal(room.Id.ToString(), audit.EntityId);
        Assert.Equal(host.Id, audit.ActorUserId);
        Assert.Equal(AppRoles.Coach, audit.ActorRole);
        Assert.Equal(speaker.Id, audit.TargetUserId);

        using var doc = JsonDocument.Parse(audit.DetailsJson!);
        Assert.Equal("Timer Room", doc.RootElement.GetProperty("title").GetString());
        Assert.Equal(5, doc.RootElement.GetProperty("addedMinutes").GetInt32());
        Assert.Equal(5, doc.RootElement.GetProperty("totalExtraMinutes").GetInt32());
    }

    [Fact]
    public async Task RoomHub_NonHostAttemptingHostActions_ThrowsLeavesStateUntouchedAndWritesNoAudit()
    {
        var host = await CreateUserAsync(AppRoles.Coach, "Coach", "Owner");
        var intruder = await CreateUserAsync(AppRoles.User, "Intruder", "User");
        var target = await CreateUserAsync(AppRoles.User, "Target", "Speaker");
        var room = await SeedRoomAsync(host.Id, "Protected Room", RoomStatus.Live);
        await SeedParticipantAsync(room.Id, target.Id, status: ParticipantStatus.Active, isOnStage: true, isHandRaised: false);

        var (scope, hub, _) = CreateRoomHub(intruder.Id, new[] { AppRoles.User });
        using (scope)
        {
            await Assert.ThrowsAsync<HubException>(() => hub.ApproveToStage(room.Id.ToString(), target.Id.ToString()));
            await Assert.ThrowsAsync<HubException>(() => hub.MoveToAudience(room.Id.ToString(), target.Id.ToString()));
            await Assert.ThrowsAsync<HubException>(() => hub.KickUser(room.Id.ToString(), target.Id.ToString()));
            await Assert.ThrowsAsync<HubException>(() => hub.GrantExtraTime(room.Id.ToString(), target.Id.ToString(), 5));
        }

        var participant = await ReadParticipantAsync(room.Id, target.Id);
        Assert.Equal(ParticipantStatus.Active, participant.Status);
        Assert.True(participant.IsOnStage);
        Assert.Equal(0, participant.ExtraMinutesGranted);
        Assert.Empty(await ReadAuditLogsAsync());
    }

    [Fact]
    public async Task RoomHub_WhenAuditServiceThrows_HostActionsStillSucceedAndPersistState()
    {
        var host = await CreateUserAsync(AppRoles.Coach, "Coach", "Resilient");
        var speaker = await CreateUserAsync(AppRoles.User, "Speaker", "One");
        var room = await SeedRoomAsync(host.Id, "Resilience Room", RoomStatus.Live);
        await SeedParticipantAsync(room.Id, speaker.Id, status: ParticipantStatus.Active, isOnStage: false, isHandRaised: true);

        var throwingAudit = new Mock<IAuditLogService>();
        throwingAudit
            .Setup(a => a.RecordAsync(It.IsAny<AuditEntry>()))
            .ThrowsAsync(new InvalidOperationException("Audit store unavailable"));

        var (scope, hub, groupProxy) = CreateRoomHub(host.Id, new[] { AppRoles.Coach }, throwingAudit.Object);
        using (scope)
        {
            await hub.ApproveToStage(room.Id.ToString(), speaker.Id.ToString());
            Assert.True((await ReadParticipantAsync(room.Id, speaker.Id)).IsOnStage);

            await hub.GrantExtraTime(room.Id.ToString(), speaker.Id.ToString(), 5);
            Assert.Equal(5, (await ReadParticipantAsync(room.Id, speaker.Id)).ExtraMinutesGranted);

            await hub.MoveToAudience(room.Id.ToString(), speaker.Id.ToString());
            Assert.False((await ReadParticipantAsync(room.Id, speaker.Id)).IsOnStage);

            await hub.KickUser(room.Id.ToString(), speaker.Id.ToString());
            Assert.Equal(ParticipantStatus.Kicked, (await ReadParticipantAsync(room.Id, speaker.Id)).Status);
        }

        throwingAudit.Verify(a => a.RecordAsync(It.IsAny<AuditEntry>()), Times.Exactly(4));
        groupProxy.Verify(p => p.SendCoreAsync("StageUpdated", It.IsAny<object?[]>(), default), Times.Exactly(2));
        groupProxy.Verify(p => p.SendCoreAsync("ExtraTimeGranted", It.IsAny<object?[]>(), default), Times.Once);
        groupProxy.Verify(p => p.SendCoreAsync("UserKicked", It.IsAny<object?[]>(), default), Times.Once);
    }
}
