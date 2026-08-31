using System.Security.Claims;
using Cocorra.API.Hubs;
using Cocorra.BLL.Base;
using Cocorra.BLL.Services.ChatService;
using Cocorra.BLL.Services.EventTracking;
using Cocorra.BLL.Services.LiveKit;
using Cocorra.BLL.Services.RoomService;
using Cocorra.DAL.DTOS.ChatDto;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;
using Cocorra.DAL.Repository.RoomRepository;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Cocorra.Tests;

public class RoomHubTests
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
    private readonly Mock<IClientProxy> _userProxyMock = new();
    private readonly Mock<HubCallerContext> _contextMock = new();

    private readonly LiveKitSettings _settings = new()
    {
        ServerUrl = "wss://test.livekit.dev",
        ApiKey = "key",
        ApiSecret = "secret"
    };

    public RoomHubTests()
    {
        _clientsMock.Setup(c => c.Caller).Returns(_callerProxyMock.Object);
        _clientsMock.Setup(c => c.Group(It.IsAny<string>())).Returns(_groupProxyMock.Object);
        _clientsMock.Setup(c => c.User(It.IsAny<string>())).Returns(_userProxyMock.Object);
        _contextMock.Setup(c => c.ConnectionAborted).Returns(CancellationToken.None);
    }

    private RoomHub CreateHub(Guid? userId = null, string connectionId = "conn-123")
    {
        var hub = new RoomHub(
            _roomRepoMock.Object,
            _roomServiceMock.Object,
            _chatServiceMock.Object,
            _liveKitServiceMock.Object,
            Options.Create(_settings),
            _eventTrackerMock.Object,
            NullLogger<RoomHub>.Instance
        );

        var uid = userId ?? Guid.NewGuid();
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, uid.ToString())
        };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        var principal = new ClaimsPrincipal(identity);

        _contextMock.Setup(c => c.User).Returns(principal);
        _contextMock.Setup(c => c.ConnectionId).Returns(connectionId);
        _contextMock.Setup(c => c.UserIdentifier).Returns(uid.ToString());

        hub.Context = _contextMock.Object;
        hub.Clients = _clientsMock.Object;
        hub.Groups = _groupManagerMock.Object;

        return hub;
    }

    // ===================================================================
    // JoinRoom Tests
    // ===================================================================

    [Fact]
    public async Task JoinRoom_RoomNotLive_ThrowsHubException()
    {
        var roomId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var hub = CreateHub(userId);

        _roomRepoMock.Setup(r => r.GetByIdAsync(roomId))
            .ReturnsAsync(new Room { Id = roomId, Status = RoomStatus.Scheduled });

        await Assert.ThrowsAsync<HubException>(() => hub.JoinRoom(roomId.ToString()));
    }

    [Fact]
    public async Task JoinRoom_NotParticipant_ThrowsHubException()
    {
        var roomId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var hub = CreateHub(userId);

        _roomRepoMock.Setup(r => r.GetByIdAsync(roomId))
            .ReturnsAsync(new Room { Id = roomId, Status = RoomStatus.Live });
        _roomRepoMock.Setup(r => r.GetParticipantAsync(roomId, userId))
            .ReturnsAsync((RoomParticipant?)null);

        await Assert.ThrowsAsync<HubException>(() => hub.JoinRoom(roomId.ToString()));
    }

    [Fact]
    public async Task JoinRoom_PendingApproval_ThrowsHubException()
    {
        var roomId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var hub = CreateHub(userId);

        _roomRepoMock.Setup(r => r.GetByIdAsync(roomId))
            .ReturnsAsync(new Room { Id = roomId, Status = RoomStatus.Live });
        _roomRepoMock.Setup(r => r.GetParticipantAsync(roomId, userId))
            .ReturnsAsync(new RoomParticipant { Status = ParticipantStatus.PendingApproval });

        await Assert.ThrowsAsync<HubException>(() => hub.JoinRoom(roomId.ToString()));
    }

    [Fact]
    public async Task JoinRoom_Kicked_ThrowsHubException()
    {
        var roomId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var hub = CreateHub(userId);

        _roomRepoMock.Setup(r => r.GetByIdAsync(roomId))
            .ReturnsAsync(new Room { Id = roomId, Status = RoomStatus.Live });
        _roomRepoMock.Setup(r => r.GetParticipantAsync(roomId, userId))
            .ReturnsAsync(new RoomParticipant { Status = ParticipantStatus.Kicked });

        await Assert.ThrowsAsync<HubException>(() => hub.JoinRoom(roomId.ToString()));
    }

    [Fact]
    public async Task JoinRoom_Success_EntersGroup_BroadcastsUserJoined_AndSendsLiveKitToken()
    {
        var roomId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var hub = CreateHub(userId, "conn-user-1");

        var room = new Room { Id = roomId, Status = RoomStatus.Live, HostId = Guid.NewGuid() };
        var participant = new RoomParticipant
        {
            RoomId = roomId,
            UserId = userId,
            Status = ParticipantStatus.Active,
            User = new ApplicationUser { FirstName = "Alice", LastName = "Smith" }
        };

        _roomRepoMock.Setup(r => r.GetByIdAsync(roomId)).ReturnsAsync(room);
        _roomRepoMock.Setup(r => r.GetParticipantAsync(roomId, userId)).ReturnsAsync(participant);
        _liveKitServiceMock.Setup(l => l.GenerateToken(roomId, userId, "Alice Smith", false))
            .Returns("livekit-jwt-token");

        await hub.JoinRoom(roomId.ToString());

        // Added to SignalR group
        _groupManagerMock.Verify(g => g.AddToGroupAsync("conn-user-1", roomId.ToString(), default), Times.Once);

        // Track event
        _eventTrackerMock.Verify(e => e.Track(EventTypes.RoomJoined, (Guid?)userId, It.IsAny<object>()), Times.Once);

        // Group broadcast UserJoined
        _groupProxyMock.Verify(g => g.SendCoreAsync("UserJoined", It.IsAny<object[]>(), default), Times.Once);

        // Caller receives LiveKitToken
        _callerProxyMock.Verify(c => c.SendCoreAsync("LiveKitToken", It.IsAny<object[]>(), default), Times.Once);
    }

    // ===================================================================
    // LeaveRoom Tests
    // ===================================================================

    [Fact]
    public async Task LeaveRoom_CleansUpAndBroadcastsUserLeft()
    {
        var roomId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var hub = CreateHub(userId, "conn-leave");

        await hub.LeaveRoom(roomId.ToString());

        _roomServiceMock.Verify(s => s.LeaveRoomCleanupAsync(roomId, userId), Times.Once);
        _eventTrackerMock.Verify(e => e.Track(EventTypes.RoomLeft, (Guid?)userId, It.IsAny<object>()), Times.Once);
        _groupManagerMock.Verify(g => g.RemoveFromGroupAsync("conn-leave", roomId.ToString(), default), Times.Once);
        _groupProxyMock.Verify(g => g.SendCoreAsync("UserLeft", It.IsAny<object[]>(), default), Times.Once);
    }

    // ===================================================================
    // RaiseHand & LowerHand Tests
    // ===================================================================

    [Fact]
    public async Task RaiseHand_Success_SetsHandRaised_AndBroadcasts()
    {
        var roomId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var hub = CreateHub(userId);

        var participant = new RoomParticipant
        {
            RoomId = roomId,
            UserId = userId,
            IsOnStage = false,
            IsHandRaised = false,
            User = new ApplicationUser { FirstName = "Bob" }
        };

        _roomRepoMock.Setup(r => r.GetParticipantAsync(roomId, userId)).ReturnsAsync(participant);

        await hub.RaiseHand(roomId.ToString());

        Assert.True(participant.IsHandRaised);
        _roomRepoMock.Verify(r => r.UpdateParticipantAsync(participant), Times.Once);
        _groupProxyMock.Verify(g => g.SendCoreAsync("HandRaised", It.IsAny<object[]>(), default), Times.Once);
    }

    [Fact]
    public async Task LowerHand_Success_ClearsHandRaised_AndBroadcasts()
    {
        var roomId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var hub = CreateHub(userId);

        var participant = new RoomParticipant
        {
            RoomId = roomId,
            UserId = userId,
            IsHandRaised = true,
            User = new ApplicationUser { FirstName = "Bob" }
        };

        _roomRepoMock.Setup(r => r.GetParticipantAsync(roomId, userId)).ReturnsAsync(participant);

        await hub.LowerHand(roomId.ToString());

        Assert.False(participant.IsHandRaised);
        _roomRepoMock.Verify(r => r.UpdateParticipantAsync(participant), Times.Once);
        _groupProxyMock.Verify(g => g.SendCoreAsync("HandLowered", It.IsAny<object[]>(), default), Times.Once);
    }

    // ===================================================================
    // Stage Management: ApproveToStage, MoveToAudience, GrantExtraTime
    // ===================================================================

    [Fact]
    public async Task ApproveToStage_NonHost_ThrowsHubException()
    {
        var roomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        var nonHostId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();
        var hub = CreateHub(nonHostId);

        _roomRepoMock.Setup(r => r.GetByIdAsync(roomId))
            .ReturnsAsync(new Room { Id = roomId, HostId = hostId, StageCapacity = 5 });

        await Assert.ThrowsAsync<HubException>(() => hub.ApproveToStage(roomId.ToString(), targetUserId.ToString()));
    }

    [Fact]
    public async Task ApproveToStage_FullStage_ThrowsHubException()
    {
        var roomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();
        var hub = CreateHub(hostId);

        _roomRepoMock.Setup(r => r.GetByIdAsync(roomId))
            .ReturnsAsync(new Room { Id = roomId, HostId = hostId, StageCapacity = 2 });
        _roomRepoMock.Setup(r => r.GetStageSpeakersAsync(roomId))
            .ReturnsAsync(new List<RoomParticipant> { new(), new() });

        await Assert.ThrowsAsync<HubException>(() => hub.ApproveToStage(roomId.ToString(), targetUserId.ToString()));
    }

    [Fact]
    public async Task ApproveToStage_Success_PromotesUserAndBroadcasts()
    {
        var roomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();
        var hub = CreateHub(hostId);

        var participant = new RoomParticipant
        {
            RoomId = roomId,
            UserId = targetUserId,
            IsOnStage = false,
            IsHandRaised = true,
            User = new ApplicationUser { FirstName = "Speaker" }
        };

        _roomRepoMock.Setup(r => r.GetByIdAsync(roomId))
            .ReturnsAsync(new Room { Id = roomId, HostId = hostId, StageCapacity = 5 });
        _roomRepoMock.Setup(r => r.GetStageSpeakersAsync(roomId))
            .ReturnsAsync(new List<RoomParticipant>());
        _roomRepoMock.Setup(r => r.GetParticipantAsync(roomId, targetUserId))
            .ReturnsAsync(participant);

        await hub.ApproveToStage(roomId.ToString(), targetUserId.ToString());

        Assert.True(participant.IsOnStage);
        Assert.False(participant.IsHandRaised);
        Assert.True(participant.IsMuted);
        _liveKitServiceMock.Verify(l => l.UpdateStagePermissionAsync(roomId, targetUserId, true), Times.Once);
        _groupProxyMock.Verify(g => g.SendCoreAsync("StageUpdated", It.IsAny<object[]>(), default), Times.Once);
    }

    [Fact]
    public async Task MoveToAudience_Success_DemotesSpeakerAndUpdatesMic()
    {
        var roomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();
        var hub = CreateHub(hostId);

        var participant = new RoomParticipant
        {
            RoomId = roomId,
            UserId = targetUserId,
            IsOnStage = true,
            IsMuted = false,
            LastUnmutedAt = DateTime.UtcNow.AddMinutes(-2),
            TotalSpokenSeconds = 0,
            User = new ApplicationUser { FirstName = "Speaker" }
        };

        _roomRepoMock.Setup(r => r.GetByIdAsync(roomId))
            .ReturnsAsync(new Room { Id = roomId, HostId = hostId });
        _roomRepoMock.Setup(r => r.GetParticipantAsync(roomId, targetUserId))
            .ReturnsAsync(participant);

        await hub.MoveToAudience(roomId.ToString(), targetUserId.ToString());

        Assert.False(participant.IsOnStage);
        Assert.True(participant.IsMuted);
        Assert.True(participant.TotalSpokenSeconds > 0);
        _liveKitServiceMock.Verify(l => l.UpdateStagePermissionAsync(roomId, targetUserId, false), Times.Once);
        _groupProxyMock.Verify(g => g.SendCoreAsync("StageUpdated", It.IsAny<object[]>(), default), Times.Once);
        _groupProxyMock.Verify(g => g.SendCoreAsync("MicStatusChanged", It.IsAny<object[]>(), default), Times.Once);
    }

    // ===================================================================
    // ToggleMic Tests
    // ===================================================================

    [Fact]
    public async Task ToggleMic_UnmuteWhenTimeUp_ThrowsHubException()
    {
        var roomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var hub = CreateHub(userId);

        var room = new Room { Id = roomId, HostId = hostId, DefaultSpeakerDurationMinutes = 5 };
        var participant = new RoomParticipant
        {
            RoomId = roomId,
            UserId = userId,
            IsOnStage = true,
            IsMuted = true,
            TotalSpokenSeconds = 300 // 5 minutes used up
        };

        _roomRepoMock.Setup(r => r.GetByIdAsync(roomId)).ReturnsAsync(room);
        _roomRepoMock.Setup(r => r.GetParticipantAsync(roomId, userId)).ReturnsAsync(participant);

        await Assert.ThrowsAsync<HubException>(() => hub.ToggleMic(roomId.ToString(), false));
    }

    [Fact]
    public async Task ToggleMic_UnmuteSuccess_SetsLastUnmutedAt_AndTracksMicActivated()
    {
        var roomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var hub = CreateHub(userId);

        var room = new Room { Id = roomId, HostId = hostId, DefaultSpeakerDurationMinutes = 5 };
        var participant = new RoomParticipant
        {
            RoomId = roomId,
            UserId = userId,
            IsOnStage = true,
            IsMuted = true,
            TotalSpokenSeconds = 0,
            User = new ApplicationUser { FirstName = "Speaker" }
        };

        _roomRepoMock.Setup(r => r.GetByIdAsync(roomId)).ReturnsAsync(room);
        _roomRepoMock.Setup(r => r.GetParticipantAsync(roomId, userId)).ReturnsAsync(participant);

        await hub.ToggleMic(roomId.ToString(), false);

        Assert.False(participant.IsMuted);
        Assert.NotNull(participant.LastUnmutedAt);
        _eventTrackerMock.Verify(e => e.Track(EventTypes.MicActivated, (Guid?)userId, It.IsAny<object>()), Times.Once);
        _groupProxyMock.Verify(g => g.SendCoreAsync("MicStatusChanged", It.IsAny<object[]>(), default), Times.Once);
    }

    // ===================================================================
    // GrantExtraTime Tests
    // ===================================================================

    [Fact]
    public async Task GrantExtraTime_Success_AddsMinutesAndBroadcasts()
    {
        var roomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();
        var hub = CreateHub(hostId);

        var participant = new RoomParticipant
        {
            RoomId = roomId,
            UserId = targetUserId,
            IsOnStage = true,
            ExtraMinutesGranted = 0,
            User = new ApplicationUser { FirstName = "Speaker" }
        };

        _roomRepoMock.Setup(r => r.GetByIdAsync(roomId))
            .ReturnsAsync(new Room { Id = roomId, HostId = hostId });
        _roomRepoMock.Setup(r => r.GetParticipantAsync(roomId, targetUserId))
            .ReturnsAsync(participant);

        await hub.GrantExtraTime(roomId.ToString(), targetUserId.ToString(), 5);

        Assert.Equal(5, participant.ExtraMinutesGranted);
        _groupProxyMock.Verify(g => g.SendCoreAsync("ExtraTimeGranted", It.IsAny<object[]>(), default), Times.Once);
    }

    [Fact]
    public async Task GrantExtraTime_InvalidMinutes_ThrowsHubException()
    {
        var roomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();
        var hub = CreateHub(hostId);

        _roomRepoMock.Setup(r => r.GetByIdAsync(roomId))
            .ReturnsAsync(new Room { Id = roomId, HostId = hostId });

        await Assert.ThrowsAsync<HubException>(() => hub.GrantExtraTime(roomId.ToString(), targetUserId.ToString(), 0));
        await Assert.ThrowsAsync<HubException>(() => hub.GrantExtraTime(roomId.ToString(), targetUserId.ToString(), 35));
    }

    // ===================================================================
    // KickUser & EndRoom Tests
    // ===================================================================

    [Fact]
    public async Task KickUser_HostCannotKickSelf_ThrowsHubException()
    {
        var roomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        var hub = CreateHub(hostId);

        await Assert.ThrowsAsync<HubException>(() => hub.KickUser(roomId.ToString(), hostId.ToString()));
    }

    [Fact]
    public async Task KickUser_Success_SetsStatusKickedAndBroadcasts()
    {
        var roomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();
        var hub = CreateHub(hostId);

        var participant = new RoomParticipant
        {
            RoomId = roomId,
            UserId = targetUserId,
            Status = ParticipantStatus.Active,
            User = new ApplicationUser { FirstName = "Troll" }
        };

        _roomRepoMock.Setup(r => r.GetByIdAsync(roomId))
            .ReturnsAsync(new Room { Id = roomId, HostId = hostId });
        _roomRepoMock.Setup(r => r.GetParticipantAsync(roomId, targetUserId))
            .ReturnsAsync(participant);

        await hub.KickUser(roomId.ToString(), targetUserId.ToString());

        Assert.Equal(ParticipantStatus.Kicked, participant.Status);
        _groupProxyMock.Verify(g => g.SendCoreAsync("UserKicked", It.IsAny<object[]>(), default), Times.Once);
    }

    [Fact]
    public async Task EndRoom_Success_CallsServiceAndBroadcastsRoomEnded()
    {
        var roomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        var hub = CreateHub(hostId);

        _roomServiceMock.Setup(s => s.EndRoomAsync(roomId, hostId))
            .ReturnsAsync(new Response<string> { Succeeded = true });

        await hub.EndRoom(roomId.ToString());

        _roomServiceMock.Verify(s => s.EndRoomAsync(roomId, hostId), Times.Once);
        _groupProxyMock.Verify(g => g.SendCoreAsync("RoomEnded", It.IsAny<object[]>(), default), Times.Once);
    }

    // ===================================================================
    // Room Chat Tests: Group & Private
    // ===================================================================

    [Fact]
    public async Task SendRoomGroupMessage_ActiveMember_BroadcastsReceiveRoomMessage()
    {
        var roomId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var hub = CreateHub(userId);

        var participant = new RoomParticipant
        {
            RoomId = roomId,
            UserId = userId,
            Status = ParticipantStatus.Active,
            User = new ApplicationUser { FirstName = "Alice", LastName = "Smith" }
        };

        _roomRepoMock.Setup(r => r.GetParticipantAsync(roomId, userId)).ReturnsAsync(participant);

        await hub.SendRoomGroupMessage(roomId.ToString(), "Hello everyone!");

        _groupProxyMock.Verify(g => g.SendCoreAsync("ReceiveRoomMessage", It.IsAny<object[]>(), default), Times.Once);
    }

    [Fact]
    public async Task SendRoomGroupMessage_EmptyMessage_SendsErrorToCaller()
    {
        var roomId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var hub = CreateHub(userId);

        await hub.SendRoomGroupMessage(roomId.ToString(), "   ");

        _callerProxyMock.Verify(c => c.SendCoreAsync("SendMessageError", It.IsAny<object[]>(), default), Times.Once);
    }

    [Fact]
    public async Task SendRoomPrivateMessage_Success_DeliversToUserAndConfirmsToCaller()
    {
        var userId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();
        var hub = CreateHub(userId);

        var messageDto = new MessageDto
        {
            Id = Guid.NewGuid(),
            SenderId = userId,
            ReceiverId = targetUserId,
            Content = "Secret msg"
        };

        _chatServiceMock.Setup(s => s.SaveMessageAsync(userId, targetUserId, "Secret msg"))
            .ReturnsAsync(new Response<MessageDto>
            {
                Succeeded = true,
                StatusCode = System.Net.HttpStatusCode.OK,
                Data = messageDto
            });

        await hub.SendRoomPrivateMessage(targetUserId, "Secret msg");

        _userProxyMock.Verify(u => u.SendCoreAsync("ReceivePrivateMessage", It.IsAny<object[]>(), default), Times.Once);
        _callerProxyMock.Verify(c => c.SendCoreAsync("PrivateMessageSent", It.IsAny<object[]>(), default), Times.Once);
    }
}
