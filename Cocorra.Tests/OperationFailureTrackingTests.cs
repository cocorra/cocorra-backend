using System.Security.Claims;
using Cocorra.API.Hubs;
using Cocorra.BLL.Services.ChatService;
using Cocorra.BLL.Services.EventTracking;
using Cocorra.BLL.Services.LiveKit;
using Cocorra.BLL.Services.RoomService;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;
using Cocorra.DAL.Repository.RoomRepository;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Cocorra.Tests;

/// <summary>
/// AN-041 — <c>operation_failed</c>, as redefined in
/// <c>docs/dashboard-discovery/27-operation-failure-decision.md</c>.
///
/// The event was declared with zero emission sites, which is the worst state available: its
/// presence implied coverage that did not exist, and a query for it returned an empty set
/// indistinguishable from "no failures occurred". These tests pin the redefined scope so the
/// event cannot quietly become a general error log — the failure mode the redefinition exists
/// to prevent.
/// </summary>
public class OperationFailureTrackingTests
{
    private readonly Mock<IRoomRepository> _roomRepoMock = new();
    private readonly Mock<IRoomService> _roomServiceMock = new();
    private readonly Mock<IChatService> _chatServiceMock = new();
    private readonly Mock<ILiveKitService> _liveKitServiceMock = new();
    private readonly Mock<IEventTracker> _eventTrackerMock = new();
    private readonly Mock<IHubCallerClients> _clientsMock = new();
    private readonly Mock<IGroupManager> _groupManagerMock = new();
    private readonly Mock<ISingleClientProxy> _callerProxyMock = new();
    private readonly Mock<IClientProxy> _groupProxyMock = new();
    private readonly Mock<HubCallerContext> _contextMock = new();

    private readonly List<(string EventType, Guid? UserId, object? Properties)> _tracked = [];

    private readonly LiveKitSettings _settings = new()
    {
        ServerUrl = "wss://test.livekit.dev",
        ApiKey = "key",
        ApiSecret = "secret"
    };

    public OperationFailureTrackingTests()
    {
        _clientsMock.Setup(c => c.Caller).Returns(_callerProxyMock.Object);
        _clientsMock.Setup(c => c.Group(It.IsAny<string>())).Returns(_groupProxyMock.Object);
        _contextMock.Setup(c => c.ConnectionAborted).Returns(CancellationToken.None);

        _eventTrackerMock
            .Setup(t => t.Track(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<object?>()))
            .Callback<string, Guid?, object?>((type, user, props) => _tracked.Add((type, user, props)));
    }

    private RoomHub CreateHub(Guid userId, bool newEventsEnabled)
    {
        _eventTrackerMock.SetupGet(t => t.NewEventEmissionEnabled).Returns(newEventsEnabled);
        _eventTrackerMock.SetupGet(t => t.HighFrequencyEventsEnabled).Returns(newEventsEnabled);

        var hub = new RoomHub(
            _roomRepoMock.Object,
            _roomServiceMock.Object,
            _chatServiceMock.Object,
            _liveKitServiceMock.Object,
            Options.Create(_settings),
            _eventTrackerMock.Object,
            NullLogger<RoomHub>.Instance);

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "TestAuth");
        _contextMock.Setup(c => c.User).Returns(new ClaimsPrincipal(identity));
        _contextMock.Setup(c => c.ConnectionId).Returns("conn-an041");
        _contextMock.Setup(c => c.UserIdentifier).Returns(userId.ToString());

        hub.Context = _contextMock.Object;
        hub.Clients = _clientsMock.Object;
        hub.Groups = _groupManagerMock.Object;
        return hub;
    }

    private (string Operation, string Reason, Guid RoomId) ReadFailure(object? properties)
    {
        Assert.NotNull(properties);
        var t = properties!.GetType();
        return (
            (string)t.GetProperty("operation")!.GetValue(properties)!,
            (string)t.GetProperty("reason")!.GetValue(properties)!,
            (Guid)t.GetProperty("roomId")!.GetValue(properties)!);
    }

    private List<(string EventType, Guid? UserId, object? Properties)> Failures()
        => _tracked.Where(e => e.EventType == EventTypes.OperationFailed).ToList();

    // ── Room join rejections ─────────────────────────────────────────────────

    [Theory]
    [InlineData(RoomStatus.Scheduled)]
    [InlineData(RoomStatus.Ended)]
    public async Task JoinRoom_RoomNotLive_EmitsRoomNotLive(RoomStatus status)
    {
        var roomId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var hub = CreateHub(userId, newEventsEnabled: true);

        _roomRepoMock.Setup(r => r.GetByIdAsync(roomId))
            .ReturnsAsync(new Room { Id = roomId, Status = status });

        await Assert.ThrowsAsync<HubException>(() => hub.JoinRoom(roomId.ToString()));

        var failure = Assert.Single(Failures());
        var (operation, reason, trackedRoomId) = ReadFailure(failure.Properties);

        Assert.Equal(userId, failure.UserId);
        Assert.Equal(TrackedOperations.RoomJoin, operation);
        Assert.Equal(OperationFailureReasons.RoomNotLive, reason);
        Assert.Equal(roomId, trackedRoomId);
    }

    [Fact]
    public async Task JoinRoom_NoParticipantRow_EmitsNotAParticipant()
    {
        var roomId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var hub = CreateHub(userId, newEventsEnabled: true);

        _roomRepoMock.Setup(r => r.GetByIdAsync(roomId))
            .ReturnsAsync(new Room { Id = roomId, Status = RoomStatus.Live });
        _roomRepoMock.Setup(r => r.GetParticipantAsync(roomId, userId))
            .ReturnsAsync((RoomParticipant?)null);

        await Assert.ThrowsAsync<HubException>(() => hub.JoinRoom(roomId.ToString()));

        var (_, reason, _) = ReadFailure(Assert.Single(Failures()).Properties);
        Assert.Equal(OperationFailureReasons.NotAParticipant, reason);
    }

    [Fact]
    public async Task JoinRoom_PendingApproval_EmitsPendingHostApproval()
    {
        var roomId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var hub = CreateHub(userId, newEventsEnabled: true);

        _roomRepoMock.Setup(r => r.GetByIdAsync(roomId))
            .ReturnsAsync(new Room { Id = roomId, Status = RoomStatus.Live });
        _roomRepoMock.Setup(r => r.GetParticipantAsync(roomId, userId))
            .ReturnsAsync(new RoomParticipant { RoomId = roomId, UserId = userId, Status = ParticipantStatus.PendingApproval });

        await Assert.ThrowsAsync<HubException>(() => hub.JoinRoom(roomId.ToString()));

        var (_, reason, _) = ReadFailure(Assert.Single(Failures()).Properties);
        Assert.Equal(OperationFailureReasons.PendingHostApproval, reason);
    }

    [Theory]
    [InlineData(ParticipantStatus.Kicked)]
    [InlineData(ParticipantStatus.Rejected)]
    public async Task JoinRoom_BlockedParticipant_EmitsBlockedFromRoom(ParticipantStatus status)
    {
        var roomId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var hub = CreateHub(userId, newEventsEnabled: true);

        _roomRepoMock.Setup(r => r.GetByIdAsync(roomId))
            .ReturnsAsync(new Room { Id = roomId, Status = RoomStatus.Live });
        _roomRepoMock.Setup(r => r.GetParticipantAsync(roomId, userId))
            .ReturnsAsync(new RoomParticipant { RoomId = roomId, UserId = userId, Status = status });

        await Assert.ThrowsAsync<HubException>(() => hub.JoinRoom(roomId.ToString()));

        var (_, reason, _) = ReadFailure(Assert.Single(Failures()).Properties);
        Assert.Equal(OperationFailureReasons.BlockedFromRoom, reason);
    }

    // ── Stage capacity: the highest-value site ───────────────────────────────

    [Fact]
    public async Task ApproveToStage_StageFull_EmitsAgainstTheParticipantNotTheHost()
    {
        var roomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var hub = CreateHub(hostId, newEventsEnabled: true);

        _roomRepoMock.Setup(r => r.GetByIdAsync(roomId))
            .ReturnsAsync(new Room { Id = roomId, HostId = hostId, Status = RoomStatus.Live, StageCapacity = 2 });
        _roomRepoMock.Setup(r => r.GetStageSpeakersAsync(roomId))
            .ReturnsAsync([
                new RoomParticipant { RoomId = roomId, UserId = Guid.NewGuid(), IsOnStage = true },
                new RoomParticipant { RoomId = roomId, UserId = Guid.NewGuid(), IsOnStage = true }
            ]);

        await Assert.ThrowsAsync<HubException>(() => hub.ApproveToStage(roomId.ToString(), targetId.ToString()));

        var failure = Assert.Single(Failures());
        var (operation, reason, trackedRoomId) = ReadFailure(failure.Properties);

        // The convention that matters: this is what happened to the LISTENER. Tracking it
        // against the host — the caller — would make the stage funnel unreadable, in the same
        // way room_join_approved is unreadable for that purpose.
        Assert.Equal(targetId, failure.UserId);
        Assert.Equal(TrackedOperations.StagePromotion, operation);
        Assert.Equal(OperationFailureReasons.StageAtCapacity, reason);
        Assert.Equal(roomId, trackedRoomId);
    }

    // ── Flag gating and vocabulary discipline ────────────────────────────────

    [Fact]
    public async Task NoOperationFailedEvent_IsEmittedWhileTheFlagIsOff()
    {
        var roomId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var hub = CreateHub(userId, newEventsEnabled: false);

        _roomRepoMock.Setup(r => r.GetByIdAsync(roomId))
            .ReturnsAsync(new Room { Id = roomId, Status = RoomStatus.Scheduled });

        await Assert.ThrowsAsync<HubException>(() => hub.JoinRoom(roomId.ToString()));

        Assert.Empty(Failures());
    }

    [Fact]
    public async Task FailureProperties_CarryNoExceptionMessage()
    {
        var roomId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var hub = CreateHub(userId, newEventsEnabled: true);

        _roomRepoMock.Setup(r => r.GetByIdAsync(roomId))
            .ReturnsAsync(new Room { Id = roomId, Status = RoomStatus.Scheduled });

        await Assert.ThrowsAsync<HubException>(() => hub.JoinRoom(roomId.ToString()));

        // Exception text is written by developers for developers, is not a closed set, and can
        // carry user data into a table with 180-day retention. The payload must expose only the
        // declared fields.
        var props = Assert.Single(Failures()).Properties!;
        var names = props.GetType().GetProperties().Select(p => p.Name).ToList();

        Assert.Equal(["operation", "reason", "roomId", "extra"], names);
    }

    [Fact]
    public void EveryDeclaredReason_IsDistinctAndSnakeCased()
    {
        // A closed vocabulary is only closed if it stays enumerable. Duplicated or
        // inconsistently formatted values are how a queryable set becomes a free-text column.
        var reasons = typeof(OperationFailureReasons)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral)
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        Assert.NotEmpty(reasons);
        Assert.Equal(reasons.Count, reasons.Distinct().Count());
        Assert.All(reasons, r => Assert.Matches("^[a-z]+(_[a-z]+)*$", r));
    }
}
