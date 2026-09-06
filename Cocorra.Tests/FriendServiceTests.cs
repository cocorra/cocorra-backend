using System;
using System.Net;
using System.Threading.Tasks;
using Cocorra.BLL.Services.EventTracking;
using Cocorra.BLL.Services.FriendService;
using Cocorra.BLL.Services.NotificationService;
using Cocorra.DAL.DTOS.FriendDto;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;
using Cocorra.DAL.Repository.FriendRepository;
using Cocorra.DAL.Repository.NotificationRepository;
using Cocorra.Tests.Helpers;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Cocorra.Tests;

public class FriendServiceTests
{
    private readonly Mock<IFriendRepository> _friendRepoMock = new();
    private readonly Mock<INotificationRepository> _notificationRepoMock = new();
    private readonly Mock<UserManager<ApplicationUser>> _userManagerMock = TestIdentityHelper.CreateMockUserManager();
    private readonly Mock<IPushNotificationService> _pushServiceMock = new();
    private readonly Mock<IEventTracker> _eventTrackerMock = new();
    private readonly Mock<IDbContextTransaction> _transactionMock = new();
    private readonly FriendService _service;

    public FriendServiceTests()
    {
        _friendRepoMock.Setup(r => r.BeginTransaction()).Returns(_transactionMock.Object);
        _service = new FriendService(
            _friendRepoMock.Object,
            _notificationRepoMock.Object,
            _userManagerMock.Object,
            _pushServiceMock.Object,
            _eventTrackerMock.Object,
            NullLogger<FriendService>.Instance
        );
    }

    [Fact]
    public async Task SearchUserByIdAsync_TargetNotFound_ReturnsNotFound()
    {
        var currentUserId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();
        _userManagerMock.Setup(m => m.FindByIdAsync(targetUserId.ToString())).ReturnsAsync((ApplicationUser?)null);

        var result = await _service.SearchUserByIdAsync(currentUserId, targetUserId);

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.NotFound, result.StatusCode);
        Assert.Equal("User not found.", result.Message);
    }

    [Theory]
    [InlineData(FriendRequestStatus.Accepted, "Friends")]
    [InlineData(FriendRequestStatus.Pending, "RequestSent")]
    public async Task SearchUserByIdAsync_TargetFound_ReturnsCorrectStatus(FriendRequestStatus status, string expectedStatus)
    {
        var currentUserId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();
        var targetUser = new ApplicationUser
        {
            Id = targetUserId,
            FirstName = "Alice",
            LastName = "Smith",
            Email = "alice@cocorra.com",
            ProfilePicturePath = "pic.jpg"
        };
        _userManagerMock.Setup(m => m.FindByIdAsync(targetUserId.ToString())).ReturnsAsync(targetUser);

        var request = new FriendRequest
        {
            SenderId = currentUserId,
            ReceiverId = targetUserId,
            Status = status
        };
        _friendRepoMock.Setup(r => r.GetFriendshipRelationAsync(currentUserId, targetUserId))
            .ReturnsAsync(request);

        var result = await _service.SearchUserByIdAsync(currentUserId, targetUserId);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Data);
        Assert.Equal(expectedStatus, result.Data.FriendshipStatus);
        Assert.Equal("Alice Smith", result.Data.FullName);
    }

    [Fact]
    public async Task SendFriendRequestAsync_SelfRequest_ReturnsBadRequest()
    {
        var userId = Guid.NewGuid();
        var result = await _service.SendFriendRequestAsync(userId, userId);

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        Assert.Equal("You cannot send a friend request to yourself.", result.Message);
    }

    [Fact]
    public async Task SendFriendRequestAsync_TargetNotFound_ReturnsNotFound()
    {
        var currentUserId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();
        _userManagerMock.Setup(m => m.FindByIdAsync(targetUserId.ToString())).ReturnsAsync((ApplicationUser?)null);

        var result = await _service.SendFriendRequestAsync(currentUserId, targetUserId);

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.NotFound, result.StatusCode);
    }

    [Fact]
    public async Task SendFriendRequestAsync_AlreadyFriends_ReturnsBadRequest()
    {
        var currentUserId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();
        _userManagerMock.Setup(m => m.FindByIdAsync(targetUserId.ToString()))
            .ReturnsAsync(new ApplicationUser { Id = targetUserId });

        var existing = new FriendRequest
        {
            SenderId = currentUserId,
            ReceiverId = targetUserId,
            Status = FriendRequestStatus.Accepted
        };
        _friendRepoMock.Setup(r => r.GetFriendshipRelationAsync(currentUserId, targetUserId))
            .ReturnsAsync(existing);

        var result = await _service.SendFriendRequestAsync(currentUserId, targetUserId);

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        Assert.Equal("You are already friends.", result.Message);
    }

    [Fact]
    public async Task SendFriendRequestAsync_ValidRequest_SendsNotificationAndTracksEvent()
    {
        var currentUserId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();
        var currentUser = new ApplicationUser { Id = currentUserId, FirstName = "Sender", LastName = "User" };
        var targetUser = new ApplicationUser { Id = targetUserId, FcmToken = "target_token" };

        _userManagerMock.Setup(m => m.FindByIdAsync(targetUserId.ToString())).ReturnsAsync(targetUser);
        _userManagerMock.Setup(m => m.FindByIdAsync(currentUserId.ToString())).ReturnsAsync(currentUser);
        _friendRepoMock.Setup(r => r.GetFriendshipRelationAsync(currentUserId, targetUserId))
            .ReturnsAsync((FriendRequest?)null);

        var result = await _service.SendFriendRequestAsync(currentUserId, targetUserId);

        Assert.True(result.Succeeded);
        Assert.Equal("Friend request sent successfully.", result.Data);

        _friendRepoMock.Verify(r => r.AddAsync(It.Is<FriendRequest>(f =>
            f.SenderId == currentUserId &&
            f.ReceiverId == targetUserId &&
            f.Status == FriendRequestStatus.Pending)), Times.Once);

        _notificationRepoMock.Verify(n => n.AddAsync(It.Is<Notification>(notif =>
            notif.UserId == targetUserId &&
            notif.Type == NotificationType.FriendRequest)), Times.Once);

        _pushServiceMock.Verify(p => p.SendPushNotificationAsync(
            "target_token",
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<System.Collections.Generic.Dictionary<string, string>>()
        ), Times.Once);

        _eventTrackerMock.Verify(t => t.Track(
            EventTypes.FriendRequestSent,
            currentUserId,
            It.IsAny<object>()), Times.Once);

        _transactionMock.Verify(t => t.Commit(), Times.Once);
    }

    [Fact]
    public async Task RespondToFriendRequestAsync_NoPendingRequest_ReturnsBadRequest()
    {
        var currentUserId = Guid.NewGuid();
        var senderId = Guid.NewGuid();
        _friendRepoMock.Setup(r => r.GetPendingRequestAsync(senderId, currentUserId))
            .ReturnsAsync((FriendRequest?)null);

        var result = await _service.RespondToFriendRequestAsync(currentUserId, senderId, accept: true);

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
    }

    [Fact]
    public async Task RespondToFriendRequestAsync_AcceptRequest_UpdatesStatusAndNotifiesSender()
    {
        var currentUserId = Guid.NewGuid();
        var senderId = Guid.NewGuid();
        var request = new FriendRequest
        {
            SenderId = senderId,
            ReceiverId = currentUserId,
            Status = FriendRequestStatus.Pending
        };
        var currentUser = new ApplicationUser { Id = currentUserId, FirstName = "Acceptor" };
        var senderUser = new ApplicationUser { Id = senderId, FcmToken = "sender_fcm" };

        _friendRepoMock.Setup(r => r.GetPendingRequestAsync(senderId, currentUserId))
            .ReturnsAsync(request);
        _userManagerMock.Setup(m => m.FindByIdAsync(currentUserId.ToString())).ReturnsAsync(currentUser);
        _userManagerMock.Setup(m => m.FindByIdAsync(senderId.ToString())).ReturnsAsync(senderUser);

        var result = await _service.RespondToFriendRequestAsync(currentUserId, senderId, accept: true);

        Assert.True(result.Succeeded);
        Assert.Equal("Friend request accepted.", result.Data);
        Assert.Equal(FriendRequestStatus.Accepted, request.Status);

        _friendRepoMock.Verify(r => r.UpdateAsync(request), Times.Once);
        _notificationRepoMock.Verify(n => n.AddAsync(It.Is<Notification>(notif =>
            notif.UserId == senderId &&
            notif.Type == NotificationType.FriendAccept)), Times.Once);

        _eventTrackerMock.Verify(t => t.Track(
            EventTypes.FriendRequestAccepted,
            currentUserId,
            It.IsAny<object>()), Times.Once);

        _transactionMock.Verify(t => t.Commit(), Times.Once);
    }

    [Fact]
    public async Task RespondToFriendRequestAsync_DeclineRequest_UpdatesToRejected()
    {
        var currentUserId = Guid.NewGuid();
        var senderId = Guid.NewGuid();
        var request = new FriendRequest
        {
            SenderId = senderId,
            ReceiverId = currentUserId,
            Status = FriendRequestStatus.Pending
        };

        _friendRepoMock.Setup(r => r.GetPendingRequestAsync(senderId, currentUserId))
            .ReturnsAsync(request);

        var result = await _service.RespondToFriendRequestAsync(currentUserId, senderId, accept: false);

        Assert.True(result.Succeeded);
        Assert.Equal("Friend request rejected.", result.Data);
        Assert.Equal(FriendRequestStatus.Rejected, request.Status);
        _friendRepoMock.Verify(r => r.UpdateAsync(request), Times.Once);
        _transactionMock.Verify(t => t.Commit(), Times.Once);
    }

    [Fact]
    public async Task RemoveFriendOrCancelRequestAsync_NotFound_ReturnsBadRequest()
    {
        var currentUserId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();
        _friendRepoMock.Setup(r => r.GetFriendshipRelationAsync(currentUserId, targetUserId))
            .ReturnsAsync((FriendRequest?)null);

        var result = await _service.RemoveFriendOrCancelRequestAsync(currentUserId, targetUserId);

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
    }

    [Fact]
    public async Task RemoveFriendOrCancelRequestAsync_ValidRelation_DeletesRelation()
    {
        var currentUserId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();
        var relation = new FriendRequest
        {
            SenderId = currentUserId,
            ReceiverId = targetUserId,
            Status = FriendRequestStatus.Accepted
        };
        _friendRepoMock.Setup(r => r.GetFriendshipRelationAsync(currentUserId, targetUserId))
            .ReturnsAsync(relation);

        var result = await _service.RemoveFriendOrCancelRequestAsync(currentUserId, targetUserId);

        Assert.True(result.Succeeded);
        Assert.Equal("Action completed successfully.", result.Data);
        _friendRepoMock.Verify(r => r.DeleteAsync(relation), Times.Once);
    }
}
