using System;
using System.Collections.Generic;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Cocorra.BLL.Base;
using Cocorra.BLL.Services.EventTracking;
using Cocorra.BLL.Services.RoomFeedbackService;
using Cocorra.DAL.DTOS.RoomFeedbackDto;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;
using Cocorra.DAL.Repository.RoomFeedbackRepository;
using Cocorra.DAL.Repository.RoomRepository;
using Moq;
using Xunit;

namespace Cocorra.Tests;

public class RoomFeedbackServiceTests
{
    private readonly Mock<IRoomFeedbackRepository> _feedbackRepoMock = new();
    private readonly Mock<IRoomRepository> _roomRepoMock = new();
    private readonly Mock<IEventTracker> _eventTrackerMock = new();
    private readonly RoomFeedbackService _service;

    public RoomFeedbackServiceTests()
    {
        _service = new RoomFeedbackService(
            _feedbackRepoMock.Object,
            _roomRepoMock.Object,
            _eventTrackerMock.Object);
    }

    [Fact]
    public async Task SubmitFeedbackAsync_RoomNotFound_ReturnsNotFoundAndNeverCallsUpsert()
    {
        var roomId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var dto = new SubmitRoomFeedbackDto { Rating = 5, Comment = "Great session" };

        _roomRepoMock.Setup(r => r.GetByIdAsync(roomId)).ReturnsAsync((Room?)null);

        var result = await _service.SubmitFeedbackAsync(roomId, userId, dto);

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.NotFound, result.StatusCode);
        Assert.Equal("Room not found.", result.Message);
        _feedbackRepoMock.Verify(f => f.UpsertAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task SubmitFeedbackAsync_CallerIsRoomHost_ReturnsBadRequestAndNeverCallsUpsert()
    {
        var roomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        var room = new Room { Id = roomId, HostId = hostId, Status = RoomStatus.Ended };
        var dto = new SubmitRoomFeedbackDto { Rating = 5, Comment = "Self review" };

        _roomRepoMock.Setup(r => r.GetByIdAsync(roomId)).ReturnsAsync(room);

        var result = await _service.SubmitFeedbackAsync(roomId, hostId, dto);

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        Assert.Equal("Hosts cannot rate their own room.", result.Message);
        _feedbackRepoMock.Verify(f => f.UpsertAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task SubmitFeedbackAsync_NoParticipantRow_ReturnsForbiddenAndNeverCallsUpsert()
    {
        var roomId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var room = new Room { Id = roomId, HostId = Guid.NewGuid(), Status = RoomStatus.Ended };
        var dto = new SubmitRoomFeedbackDto { Rating = 4 };

        _roomRepoMock.Setup(r => r.GetByIdAsync(roomId)).ReturnsAsync(room);
        _roomRepoMock.Setup(r => r.GetParticipantAsync(roomId, userId)).ReturnsAsync((RoomParticipant?)null);

        var result = await _service.SubmitFeedbackAsync(roomId, userId, dto);

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.Forbidden, result.StatusCode);
        Assert.Equal("Only participants of this room can submit feedback.", result.Message);
        _feedbackRepoMock.Verify(f => f.UpsertAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<string?>()), Times.Never);
    }

    [Theory]
    [InlineData(ParticipantStatus.PendingApproval)]
    [InlineData(ParticipantStatus.Rejected)]
    public async Task SubmitFeedbackAsync_ParticipantNotAdmitted_ReturnsForbiddenAndNeverCallsUpsert(ParticipantStatus status)
    {
        var roomId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var room = new Room { Id = roomId, HostId = Guid.NewGuid(), Status = RoomStatus.Ended };
        var participant = new RoomParticipant { RoomId = roomId, UserId = userId, Status = status };
        var dto = new SubmitRoomFeedbackDto { Rating = 4 };

        _roomRepoMock.Setup(r => r.GetByIdAsync(roomId)).ReturnsAsync(room);
        _roomRepoMock.Setup(r => r.GetParticipantAsync(roomId, userId)).ReturnsAsync(participant);

        var result = await _service.SubmitFeedbackAsync(roomId, userId, dto);

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.Forbidden, result.StatusCode);
        Assert.Equal("Only participants of this room can submit feedback.", result.Message);
        _feedbackRepoMock.Verify(f => f.UpsertAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task SubmitFeedbackAsync_ParticipantActiveAndRoomLive_ReturnsBadRequestTooEarlyAndNeverCallsUpsert()
    {
        var roomId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var room = new Room { Id = roomId, HostId = Guid.NewGuid(), Status = RoomStatus.Live };
        var participant = new RoomParticipant { RoomId = roomId, UserId = userId, Status = ParticipantStatus.Active };
        var dto = new SubmitRoomFeedbackDto { Rating = 5 };

        _roomRepoMock.Setup(r => r.GetByIdAsync(roomId)).ReturnsAsync(room);
        _roomRepoMock.Setup(r => r.GetParticipantAsync(roomId, userId)).ReturnsAsync(participant);

        var result = await _service.SubmitFeedbackAsync(roomId, userId, dto);

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        Assert.Equal("Feedback is available after you leave the room or it ends.", result.Message);
        _feedbackRepoMock.Verify(f => f.UpsertAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task SubmitFeedbackAsync_ParticipantActiveAndRoomEnded_ReturnsSuccessAndCallsUpsert()
    {
        var roomId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var room = new Room { Id = roomId, HostId = Guid.NewGuid(), Status = RoomStatus.Ended };
        var participant = new RoomParticipant { RoomId = roomId, UserId = userId, Status = ParticipantStatus.Active };
        var dto = new SubmitRoomFeedbackDto { Rating = 4, Comment = "Good room" };
        var feedback = new RoomFeedback
        {
            Id = Guid.NewGuid(),
            RoomId = roomId,
            UserId = userId,
            Rating = 4,
            Comment = "Good room",
            CreatedAt = DateTime.UtcNow
        };

        _roomRepoMock.Setup(r => r.GetByIdAsync(roomId)).ReturnsAsync(room);
        _roomRepoMock.Setup(r => r.GetParticipantAsync(roomId, userId)).ReturnsAsync(participant);
        _feedbackRepoMock.Setup(f => f.UpsertAsync(roomId, userId, 4, "Good room"))
            .ReturnsAsync((feedback, false));

        var result = await _service.SubmitFeedbackAsync(roomId, userId, dto);

        Assert.True(result.Succeeded);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal("Feedback submitted.", result.Message);
        Assert.NotNull(result.Data);
        Assert.Equal(feedback.Id, result.Data!.Id);
        Assert.Equal(4, result.Data.Rating);
        Assert.Equal("Good room", result.Data.Comment);

        _feedbackRepoMock.Verify(f => f.UpsertAsync(roomId, userId, 4, "Good room"), Times.Once);
    }

    [Theory]
    [InlineData(ParticipantStatus.Left)]
    [InlineData(ParticipantStatus.Kicked)]
    public async Task SubmitFeedbackAsync_ParticipantLeftOrKickedAndRoomLive_ReturnsSuccess(ParticipantStatus status)
    {
        var roomId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var room = new Room { Id = roomId, HostId = Guid.NewGuid(), Status = RoomStatus.Live };
        var participant = new RoomParticipant { RoomId = roomId, UserId = userId, Status = status };
        var dto = new SubmitRoomFeedbackDto { Rating = 3, Comment = "Left early" };
        var feedback = new RoomFeedback
        {
            Id = Guid.NewGuid(),
            RoomId = roomId,
            UserId = userId,
            Rating = 3,
            Comment = "Left early",
            CreatedAt = DateTime.UtcNow
        };

        _roomRepoMock.Setup(r => r.GetByIdAsync(roomId)).ReturnsAsync(room);
        _roomRepoMock.Setup(r => r.GetParticipantAsync(roomId, userId)).ReturnsAsync(participant);
        _feedbackRepoMock.Setup(f => f.UpsertAsync(roomId, userId, 3, "Left early"))
            .ReturnsAsync((feedback, false));

        var result = await _service.SubmitFeedbackAsync(roomId, userId, dto);

        Assert.True(result.Succeeded);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        _feedbackRepoMock.Verify(f => f.UpsertAsync(roomId, userId, 3, "Left early"), Times.Once);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(-1)]
    public async Task SubmitFeedbackAsync_InvalidRating_ReturnsBadRequestAndNeverQueriesRoom(int rating)
    {
        var roomId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var dto = new SubmitRoomFeedbackDto { Rating = rating, Comment = "Invalid rating" };

        var result = await _service.SubmitFeedbackAsync(roomId, userId, dto);

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        Assert.Equal("Rating must be between 1 and 5.", result.Message);
        _roomRepoMock.Verify(r => r.GetByIdAsync(It.IsAny<Guid>()), Times.Never);
        _feedbackRepoMock.Verify(f => f.UpsertAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task SubmitFeedbackAsync_WhitespaceComment_PassesNullCommentToUpsert()
    {
        var roomId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var room = new Room { Id = roomId, HostId = Guid.NewGuid(), Status = RoomStatus.Ended };
        var participant = new RoomParticipant { RoomId = roomId, UserId = userId, Status = ParticipantStatus.Active };
        var dto = new SubmitRoomFeedbackDto { Rating = 5, Comment = "   " };
        var feedback = new RoomFeedback
        {
            Id = Guid.NewGuid(),
            RoomId = roomId,
            UserId = userId,
            Rating = 5,
            Comment = null,
            CreatedAt = DateTime.UtcNow
        };

        _roomRepoMock.Setup(r => r.GetByIdAsync(roomId)).ReturnsAsync(room);
        _roomRepoMock.Setup(r => r.GetParticipantAsync(roomId, userId)).ReturnsAsync(participant);
        _feedbackRepoMock.Setup(f => f.UpsertAsync(roomId, userId, 5, null))
            .ReturnsAsync((feedback, false));

        var result = await _service.SubmitFeedbackAsync(roomId, userId, dto);

        Assert.True(result.Succeeded);
        _feedbackRepoMock.Verify(f => f.UpsertAsync(roomId, userId, 5, null), Times.Once);
    }

    [Fact]
    public async Task SubmitFeedbackAsync_CommentWithWhitespace_PassesTrimmedCommentToUpsert()
    {
        var roomId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var room = new Room { Id = roomId, HostId = Guid.NewGuid(), Status = RoomStatus.Ended };
        var participant = new RoomParticipant { RoomId = roomId, UserId = userId, Status = ParticipantStatus.Active };
        var dto = new SubmitRoomFeedbackDto { Rating = 5, Comment = "  hi  " };
        var feedback = new RoomFeedback
        {
            Id = Guid.NewGuid(),
            RoomId = roomId,
            UserId = userId,
            Rating = 5,
            Comment = "hi",
            CreatedAt = DateTime.UtcNow
        };

        _roomRepoMock.Setup(r => r.GetByIdAsync(roomId)).ReturnsAsync(room);
        _roomRepoMock.Setup(r => r.GetParticipantAsync(roomId, userId)).ReturnsAsync(participant);
        _feedbackRepoMock.Setup(f => f.UpsertAsync(roomId, userId, 5, "hi"))
            .ReturnsAsync((feedback, false));

        var result = await _service.SubmitFeedbackAsync(roomId, userId, dto);

        Assert.True(result.Succeeded);
        _feedbackRepoMock.Verify(f => f.UpsertAsync(roomId, userId, 5, "hi"), Times.Once);
    }

    [Fact]
    public async Task SubmitFeedbackAsync_CommentExceeds1000Chars_ReturnsBadRequestAndNeverQueriesRoom()
    {
        var roomId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var overlongComment = new string('x', 1001);
        var dto = new SubmitRoomFeedbackDto { Rating = 5, Comment = overlongComment };

        var result = await _service.SubmitFeedbackAsync(roomId, userId, dto);

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        Assert.Equal("Comment cannot exceed 1000 characters.", result.Message);
        _roomRepoMock.Verify(r => r.GetByIdAsync(It.IsAny<Guid>()), Times.Never);
        _feedbackRepoMock.Verify(f => f.UpsertAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task SubmitFeedbackAsync_UpsertReturnsIsUpdateTrue_ReturnsFeedbackUpdatedMessage()
    {
        var roomId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var room = new Room { Id = roomId, HostId = Guid.NewGuid(), Status = RoomStatus.Ended };
        var participant = new RoomParticipant { RoomId = roomId, UserId = userId, Status = ParticipantStatus.Active };
        var dto = new SubmitRoomFeedbackDto { Rating = 4, Comment = "Updated thought" };
        var feedback = new RoomFeedback
        {
            Id = Guid.NewGuid(),
            RoomId = roomId,
            UserId = userId,
            Rating = 4,
            Comment = "Updated thought",
            CreatedAt = DateTime.UtcNow.AddMinutes(-30),
            UpdatedAt = DateTime.UtcNow
        };

        _roomRepoMock.Setup(r => r.GetByIdAsync(roomId)).ReturnsAsync(room);
        _roomRepoMock.Setup(r => r.GetParticipantAsync(roomId, userId)).ReturnsAsync(participant);
        _feedbackRepoMock.Setup(f => f.UpsertAsync(roomId, userId, 4, "Updated thought"))
            .ReturnsAsync((feedback, true));

        var result = await _service.SubmitFeedbackAsync(roomId, userId, dto);

        Assert.True(result.Succeeded);
        Assert.Equal("Feedback updated.", result.Message);
        Assert.NotNull(result.Data!.UpdatedAt);
    }

    [Fact]
    public async Task SubmitFeedbackAsync_UpsertReturnsIsUpdateFalse_ReturnsFeedbackSubmittedMessage()
    {
        var roomId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var room = new Room { Id = roomId, HostId = Guid.NewGuid(), Status = RoomStatus.Ended };
        var participant = new RoomParticipant { RoomId = roomId, UserId = userId, Status = ParticipantStatus.Active };
        var dto = new SubmitRoomFeedbackDto { Rating = 5, Comment = "First review" };
        var feedback = new RoomFeedback
        {
            Id = Guid.NewGuid(),
            RoomId = roomId,
            UserId = userId,
            Rating = 5,
            Comment = "First review",
            CreatedAt = DateTime.UtcNow
        };

        _roomRepoMock.Setup(r => r.GetByIdAsync(roomId)).ReturnsAsync(room);
        _roomRepoMock.Setup(r => r.GetParticipantAsync(roomId, userId)).ReturnsAsync(participant);
        _feedbackRepoMock.Setup(f => f.UpsertAsync(roomId, userId, 5, "First review"))
            .ReturnsAsync((feedback, false));

        var result = await _service.SubmitFeedbackAsync(roomId, userId, dto);

        Assert.True(result.Succeeded);
        Assert.Equal("Feedback submitted.", result.Message);
        Assert.Null(result.Data!.UpdatedAt);
    }

    [Fact]
    public async Task SubmitFeedbackAsync_EventTracking_TracksEventWithoutCommentAndWithHasComment()
    {
        var roomId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var room = new Room { Id = roomId, HostId = Guid.NewGuid(), Status = RoomStatus.Ended };
        var participant = new RoomParticipant { RoomId = roomId, UserId = userId, Status = ParticipantStatus.Active };
        const string privateComment = "SuperSecretReviewMessage987";
        var dto = new SubmitRoomFeedbackDto { Rating = 4, Comment = privateComment };
        var feedback = new RoomFeedback
        {
            Id = Guid.NewGuid(),
            RoomId = roomId,
            UserId = userId,
            Rating = 4,
            Comment = privateComment,
            CreatedAt = DateTime.UtcNow
        };

        _roomRepoMock.Setup(r => r.GetByIdAsync(roomId)).ReturnsAsync(room);
        _roomRepoMock.Setup(r => r.GetParticipantAsync(roomId, userId)).ReturnsAsync(participant);
        _feedbackRepoMock.Setup(f => f.UpsertAsync(roomId, userId, 4, privateComment))
            .ReturnsAsync((feedback, false));

        _eventTrackerMock.Setup(t => t.NewEventEmissionEnabled).Returns(true);
        object? capturedProperties = null;
        _eventTrackerMock
            .Setup(t => t.Track(EventTypes.RoomFeedbackSubmitted, userId, It.IsAny<object?>()))
            .Callback<string, Guid?, object?>((_, _, props) => capturedProperties = props);

        var result = await _service.SubmitFeedbackAsync(roomId, userId, dto);

        Assert.True(result.Succeeded);
        _eventTrackerMock.Verify(t => t.Track(
            EventTypes.RoomFeedbackSubmitted,
            userId,
            It.IsAny<object?>()), Times.Once);

        Assert.NotNull(capturedProperties);
        var json = JsonSerializer.Serialize(capturedProperties);

        Assert.DoesNotContain(privateComment, json);
        Assert.Contains("\"hasComment\":true", json);
        Assert.Contains(roomId.ToString(), json);
        Assert.Contains("\"rating\":4", json);
        Assert.Contains("\"isUpdate\":false", json);
    }

    [Fact]
    public async Task SubmitFeedbackAsync_EventTracking_NullComment_SetsHasCommentFalse()
    {
        var roomId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var room = new Room { Id = roomId, HostId = Guid.NewGuid(), Status = RoomStatus.Ended };
        var participant = new RoomParticipant { RoomId = roomId, UserId = userId, Status = ParticipantStatus.Active };
        var dto = new SubmitRoomFeedbackDto { Rating = 3, Comment = "   " };
        var feedback = new RoomFeedback
        {
            Id = Guid.NewGuid(),
            RoomId = roomId,
            UserId = userId,
            Rating = 3,
            Comment = null,
            CreatedAt = DateTime.UtcNow
        };

        _roomRepoMock.Setup(r => r.GetByIdAsync(roomId)).ReturnsAsync(room);
        _roomRepoMock.Setup(r => r.GetParticipantAsync(roomId, userId)).ReturnsAsync(participant);
        _feedbackRepoMock.Setup(f => f.UpsertAsync(roomId, userId, 3, null))
            .ReturnsAsync((feedback, true));

        _eventTrackerMock.Setup(t => t.NewEventEmissionEnabled).Returns(true);
        object? capturedProperties = null;
        _eventTrackerMock
            .Setup(t => t.Track(EventTypes.RoomFeedbackSubmitted, userId, It.IsAny<object?>()))
            .Callback<string, Guid?, object?>((_, _, props) => capturedProperties = props);

        var result = await _service.SubmitFeedbackAsync(roomId, userId, dto);

        Assert.True(result.Succeeded);
        Assert.NotNull(capturedProperties);
        var json = JsonSerializer.Serialize(capturedProperties);

        Assert.Contains("\"hasComment\":false", json);
        Assert.Contains("\"isUpdate\":true", json);
    }

    [Fact]
    public async Task SubmitFeedbackAsync_NewEventEmissionDisabled_SucceedsWithoutTracking()
    {
        var roomId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var room = new Room { Id = roomId, HostId = Guid.NewGuid(), Status = RoomStatus.Ended };
        var participant = new RoomParticipant { RoomId = roomId, UserId = userId, Status = ParticipantStatus.Left };
        var feedback = new RoomFeedback { Id = Guid.NewGuid(), RoomId = roomId, UserId = userId, Rating = 5, CreatedAt = DateTime.UtcNow };

        _roomRepoMock.Setup(r => r.GetByIdAsync(roomId)).ReturnsAsync(room);
        _roomRepoMock.Setup(r => r.GetParticipantAsync(roomId, userId)).ReturnsAsync(participant);
        _feedbackRepoMock.Setup(f => f.UpsertAsync(roomId, userId, 5, null)).ReturnsAsync((feedback, false));
        _eventTrackerMock.Setup(t => t.NewEventEmissionEnabled).Returns(false);

        var result = await _service.SubmitFeedbackAsync(roomId, userId, new SubmitRoomFeedbackDto { Rating = 5 });

        Assert.True(result.Succeeded);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        _eventTrackerMock.Verify(t => t.Track(
            It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<object?>()), Times.Never);
    }

    [Fact]
    public async Task GetMyFeedbackStatusAsync_RoomNotFound_ReturnsNotFound()
    {
        var roomId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        _roomRepoMock.Setup(r => r.GetByIdAsync(roomId)).ReturnsAsync((Room?)null);

        var result = await _service.GetMyFeedbackStatusAsync(roomId, userId);

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.NotFound, result.StatusCode);
        Assert.Equal("Room not found.", result.Message);
    }

    [Fact]
    public async Task GetMyFeedbackStatusAsync_EligibleNoExistingFeedback_ReturnsCanSubmitTrueAndFeedbackNull()
    {
        var roomId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var room = new Room { Id = roomId, HostId = Guid.NewGuid(), Status = RoomStatus.Ended };
        var participant = new RoomParticipant { RoomId = roomId, UserId = userId, Status = ParticipantStatus.Active };

        _roomRepoMock.Setup(r => r.GetByIdAsync(roomId)).ReturnsAsync(room);
        _roomRepoMock.Setup(r => r.GetParticipantAsync(roomId, userId)).ReturnsAsync(participant);
        _feedbackRepoMock.Setup(f => f.GetByRoomAndUserAsync(roomId, userId)).ReturnsAsync((RoomFeedback?)null);

        var result = await _service.GetMyFeedbackStatusAsync(roomId, userId);

        Assert.True(result.Succeeded);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.NotNull(result.Data);
        Assert.True(result.Data!.CanSubmit);
        Assert.False(result.Data.HasSubmitted);
        Assert.Null(result.Data.Feedback);
    }

    [Fact]
    public async Task GetMyFeedbackStatusAsync_EligibleExistingFeedback_ReturnsHasSubmittedTrueAndFeedbackPopulated()
    {
        var roomId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var room = new Room { Id = roomId, HostId = Guid.NewGuid(), Status = RoomStatus.Ended };
        var participant = new RoomParticipant { RoomId = roomId, UserId = userId, Status = ParticipantStatus.Active };
        var existingFeedback = new RoomFeedback
        {
            Id = Guid.NewGuid(),
            RoomId = roomId,
            UserId = userId,
            Rating = 5,
            Comment = "Loved the discussion",
            CreatedAt = DateTime.UtcNow
        };

        _roomRepoMock.Setup(r => r.GetByIdAsync(roomId)).ReturnsAsync(room);
        _roomRepoMock.Setup(r => r.GetParticipantAsync(roomId, userId)).ReturnsAsync(participant);
        _feedbackRepoMock.Setup(f => f.GetByRoomAndUserAsync(roomId, userId)).ReturnsAsync(existingFeedback);

        var result = await _service.GetMyFeedbackStatusAsync(roomId, userId);

        Assert.True(result.Succeeded);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.NotNull(result.Data);
        Assert.True(result.Data!.CanSubmit);
        Assert.True(result.Data.HasSubmitted);
        Assert.NotNull(result.Data.Feedback);
        Assert.Equal(existingFeedback.Id, result.Data.Feedback!.Id);
        Assert.Equal(5, result.Data.Feedback.Rating);
        Assert.Equal("Loved the discussion", result.Data.Feedback.Comment);
    }

    [Fact]
    public async Task GetMyFeedbackStatusAsync_Host_ReturnsCanSubmitFalse()
    {
        var roomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        var room = new Room { Id = roomId, HostId = hostId, Status = RoomStatus.Ended };

        _roomRepoMock.Setup(r => r.GetByIdAsync(roomId)).ReturnsAsync(room);

        var result = await _service.GetMyFeedbackStatusAsync(roomId, hostId);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Data);
        Assert.False(result.Data!.CanSubmit);
    }

    [Fact]
    public async Task GetMyFeedbackStatusAsync_ActiveParticipantInLiveRoom_ReturnsCanSubmitFalse()
    {
        var roomId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var room = new Room { Id = roomId, HostId = Guid.NewGuid(), Status = RoomStatus.Live };
        var participant = new RoomParticipant { RoomId = roomId, UserId = userId, Status = ParticipantStatus.Active };

        _roomRepoMock.Setup(r => r.GetByIdAsync(roomId)).ReturnsAsync(room);
        _roomRepoMock.Setup(r => r.GetParticipantAsync(roomId, userId)).ReturnsAsync(participant);
        _feedbackRepoMock.Setup(f => f.GetByRoomAndUserAsync(roomId, userId)).ReturnsAsync((RoomFeedback?)null);

        var result = await _service.GetMyFeedbackStatusAsync(roomId, userId);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Data);
        Assert.False(result.Data!.CanSubmit);
    }

    [Fact]
    public async Task GetSummaryAsync_RoomNotFound_ReturnsNotFound()
    {
        var roomId = Guid.NewGuid();
        var callerId = Guid.NewGuid();
        _roomRepoMock.Setup(r => r.GetByIdAsync(roomId)).ReturnsAsync((Room?)null);

        var result = await _service.GetSummaryAsync(roomId, callerId, isAdmin: false);

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.NotFound, result.StatusCode);
        Assert.Equal("Room not found.", result.Message);
    }

    [Fact]
    public async Task GetSummaryAsync_CallerNotHostAndNotAdmin_ReturnsForbiddenAndNeverCallsGetRatingCounts()
    {
        var roomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        var callerId = Guid.NewGuid();
        var room = new Room { Id = roomId, HostId = hostId };

        _roomRepoMock.Setup(r => r.GetByIdAsync(roomId)).ReturnsAsync(room);

        var result = await _service.GetSummaryAsync(roomId, callerId, isAdmin: false);

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.Forbidden, result.StatusCode);
        Assert.Equal("Only the room host or an admin can view feedback.", result.Message);
        _feedbackRepoMock.Verify(f => f.GetRatingCountsAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task GetSummaryAsync_Host_ReturnsOk()
    {
        var roomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        var room = new Room { Id = roomId, HostId = hostId };

        _roomRepoMock.Setup(r => r.GetByIdAsync(roomId)).ReturnsAsync(room);
        _feedbackRepoMock.Setup(f => f.GetRatingCountsAsync(roomId))
            .ReturnsAsync(new Dictionary<int, int>());

        var result = await _service.GetSummaryAsync(roomId, hostId, isAdmin: false);

        Assert.True(result.Succeeded);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.NotNull(result.Data);
        _feedbackRepoMock.Verify(f => f.GetRatingCountsAsync(roomId), Times.Once);
    }

    [Fact]
    public async Task GetSummaryAsync_AdminNotHost_ReturnsOk()
    {
        var roomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var room = new Room { Id = roomId, HostId = hostId };

        _roomRepoMock.Setup(r => r.GetByIdAsync(roomId)).ReturnsAsync(room);
        _feedbackRepoMock.Setup(f => f.GetRatingCountsAsync(roomId))
            .ReturnsAsync(new Dictionary<int, int>());

        var result = await _service.GetSummaryAsync(roomId, adminId, isAdmin: true);

        Assert.True(result.Succeeded);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.NotNull(result.Data);
        _feedbackRepoMock.Verify(f => f.GetRatingCountsAsync(roomId), Times.Once);
    }

    [Fact]
    public async Task GetSummaryAsync_CountsPresent_CalculatesAverageAndFillsAllFiveDistributionKeys()
    {
        var roomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        var room = new Room { Id = roomId, HostId = hostId };

        // { 5: 2, 3: 1 } -> sum = 13, count = 3 -> 13 / 3 = 4.3333... -> rounded 4.33
        var counts = new Dictionary<int, int>
        {
            { 5, 2 },
            { 3, 1 }
        };

        _roomRepoMock.Setup(r => r.GetByIdAsync(roomId)).ReturnsAsync(room);
        _feedbackRepoMock.Setup(f => f.GetRatingCountsAsync(roomId)).ReturnsAsync(counts);

        var result = await _service.GetSummaryAsync(roomId, hostId, isAdmin: false);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Data);
        Assert.Equal(roomId, result.Data!.RoomId);
        Assert.Equal(3, result.Data.Count);
        Assert.Equal(4.33, result.Data.AverageRating);

        // All keys 1..5 must be present
        Assert.Equal(5, result.Data.Distribution.Count);
        Assert.Equal(0, result.Data.Distribution[1]);
        Assert.Equal(0, result.Data.Distribution[2]);
        Assert.Equal(1, result.Data.Distribution[3]);
        Assert.Equal(0, result.Data.Distribution[4]);
        Assert.Equal(2, result.Data.Distribution[5]);
    }

    [Fact]
    public async Task GetSummaryAsync_NoFeedback_ReturnsZeroAverageAndAllDistributionKeysZero()
    {
        var roomId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        var room = new Room { Id = roomId, HostId = hostId };

        _roomRepoMock.Setup(r => r.GetByIdAsync(roomId)).ReturnsAsync(room);
        _feedbackRepoMock.Setup(f => f.GetRatingCountsAsync(roomId))
            .ReturnsAsync(new Dictionary<int, int>());

        var result = await _service.GetSummaryAsync(roomId, hostId, isAdmin: false);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Data);
        Assert.Equal(0, result.Data!.Count);
        Assert.Equal(0, result.Data.AverageRating);
        Assert.Equal(5, result.Data.Distribution.Count);
        for (var i = 1; i <= 5; i++)
        {
            Assert.Equal(0, result.Data.Distribution[i]);
        }
    }

    [Fact]
    public async Task GetAdminFeedbackAsync_MapsPropertiesAndPassesFiltersAndPaging()
    {
        var roomId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var feedbackId = Guid.NewGuid();
        var createdAt = DateTime.UtcNow.AddHours(-1);
        var updatedAt = DateTime.UtcNow.AddMinutes(-10);

        var room = new Room { Id = roomId, RoomTitle = "Design Sprint Coaching" };
        var user = new ApplicationUser
        {
            Id = userId,
            FirstName = "Ada",
            LastName = "Lovelace"
        };
        var feedback = new RoomFeedback
        {
            Id = feedbackId,
            RoomId = roomId,
            Room = room,
            UserId = userId,
            User = user,
            Rating = 5,
            Comment = "Inspiring session",
            CreatedAt = createdAt,
            UpdatedAt = updatedAt
        };

        _feedbackRepoMock.Setup(f => f.GetPagedAsync(roomId, 5, 2, 15))
            .ReturnsAsync((1, new List<RoomFeedback> { feedback }));

        var result = await _service.GetAdminFeedbackAsync(roomId, 5, 2, 15);

        Assert.True(result.Succeeded);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal(1, result.TotalCount);
        Assert.Equal(2, result.CurrentPage);
        Assert.Equal(15, result.PageSize);

        var items = Assert.IsAssignableFrom<IEnumerable<AdminRoomFeedbackDto>>(result.Data);
        var item = Assert.Single(items);

        Assert.Equal(feedbackId, item.Id);
        Assert.Equal(roomId, item.RoomId);
        Assert.Equal("Design Sprint Coaching", item.RoomTitle);
        Assert.Equal(userId, item.UserId);
        Assert.Equal("Ada Lovelace", item.FullName);
        Assert.Equal(5, item.Rating);
        Assert.Equal("Inspiring session", item.Comment);
        Assert.Equal(createdAt, item.CreatedAt);
        Assert.Equal(updatedAt, item.UpdatedAt);

        _feedbackRepoMock.Verify(f => f.GetPagedAsync(roomId, 5, 2, 15), Times.Once);
    }

    [Fact]
    public async Task GetAdminFeedbackAsync_HandlesNullUserAndRoom_UsesFallbacks()
    {
        var roomId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var feedback = new RoomFeedback
        {
            Id = Guid.NewGuid(),
            RoomId = roomId,
            Room = null,
            UserId = userId,
            User = null,
            Rating = 4,
            Comment = "Room and user unlinked",
            CreatedAt = DateTime.UtcNow
        };

        _feedbackRepoMock.Setup(f => f.GetPagedAsync(null, null, 1, 10))
            .ReturnsAsync((1, new List<RoomFeedback> { feedback }));

        var result = await _service.GetAdminFeedbackAsync(null, null, 1, 10);

        Assert.True(result.Succeeded);
        var item = Assert.Single(result.Data!);
        Assert.Equal(string.Empty, item.RoomTitle);
        Assert.Equal("Unknown", item.FullName);
    }
}
