using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Cocorra.BLL.Services.EventTracking;
using Cocorra.BLL.Services.NotificationService;
using Cocorra.DAL.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Cocorra.Tests;

public class PushNotificationServiceTests
{
    private readonly Mock<IEventTracker> _eventTrackerMock = new();
    private readonly PushNotificationService _service;

    public PushNotificationServiceTests()
    {
        _eventTrackerMock.Setup(t => t.NewEventEmissionEnabled).Returns(true);
        _service = new PushNotificationService(
            NullLogger<PushNotificationService>.Instance,
            _eventTrackerMock.Object
        );
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SendPushNotificationAsync_MissingToken_TracksAttemptAndMissingTokenFailure(string? token)
    {
        var targetUserId = Guid.NewGuid();
        var data = new Dictionary<string, string>
        {
            { "type", "friend_request" },
            { "userId", targetUserId.ToString() }
        };

        await _service.SendPushNotificationAsync(token!, "Title", "Body", data);

        // Verify attempt tracked
        _eventTrackerMock.Verify(t => t.Track(
            EventTypes.PushSendAttempted,
            targetUserId,
            It.Is<object>(o => o.ToString()!.Contains("friend_request")),
            It.IsAny<string?>(),
            It.IsAny<Guid?>(),
            It.IsAny<Guid?>(),
            It.IsAny<byte>()
        ), Times.Once);

        // Verify result tracked with missing_token
        _eventTrackerMock.Verify(t => t.Track(
            EventTypes.PushSendResult,
            targetUserId,
            It.Is<object>(o => o.ToString()!.Contains("missing_token")),
            It.IsAny<string?>(),
            It.IsAny<Guid?>(),
            It.IsAny<Guid?>(),
            It.IsAny<byte>()
        ), Times.Once);
    }

    [Fact]
    public async Task SendPushNotificationAsync_UninitialisedFirebase_TracksFirebaseNotInitialisedFailure()
    {
        // When FirebaseApp has not been initialised in test runner, FirebaseMessaging.DefaultInstance is null
        var targetUserId = Guid.NewGuid();
        var data = new Dictionary<string, string>
        {
            { "type", "account_activated" },
            { "userId", targetUserId.ToString() }
        };

        await _service.SendPushNotificationAsync("valid_fcm_token_xyz", "Title", "Body", data);

        // Verify attempt tracked
        _eventTrackerMock.Verify(t => t.Track(
            EventTypes.PushSendAttempted,
            targetUserId,
            It.Is<object>(o => o.ToString()!.Contains("account_activated")),
            It.IsAny<string?>(),
            It.IsAny<Guid?>(),
            It.IsAny<Guid?>(),
            It.IsAny<byte>()
        ), Times.Once);

        // Verify result tracked with firebase_not_initialised
        _eventTrackerMock.Verify(t => t.Track(
            EventTypes.PushSendResult,
            targetUserId,
            It.Is<object>(o => o.ToString()!.Contains("firebase_not_initialised")),
            It.IsAny<string?>(),
            It.IsAny<Guid?>(),
            It.IsAny<Guid?>(),
            It.IsAny<byte>()
        ), Times.Once);
    }

    [Fact]
    public async Task SendPushNotificationAsync_WhenEmissionDisabled_DoesNotTrackEvents()
    {
        _eventTrackerMock.Setup(t => t.NewEventEmissionEnabled).Returns(false);

        await _service.SendPushNotificationAsync("", "Title", "Body", new Dictionary<string, string>());

        _eventTrackerMock.Verify(t => t.Track(
            It.IsAny<string>(),
            It.IsAny<Guid?>(),
            It.IsAny<object>(),
            It.IsAny<string?>(),
            It.IsAny<Guid?>(),
            It.IsAny<Guid?>(),
            It.IsAny<byte>()
        ), Times.Never);
    }
}
