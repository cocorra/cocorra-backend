using System;
using System.Linq;
using System.Threading.Tasks;
using Cocorra.BLL.Base;
using Cocorra.BLL.Services.EventTracking;
using Cocorra.DAL.DTOS.RoomFeedbackDto;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;
using Cocorra.DAL.Repository.RoomFeedbackRepository;
using Cocorra.DAL.Repository.RoomRepository;

namespace Cocorra.BLL.Services.RoomFeedbackService
{
    public class RoomFeedbackService : ResponseHandler, IRoomFeedbackService
    {
        private readonly IRoomFeedbackRepository _feedbackRepo;
        private readonly IRoomRepository _roomRepo;
        private readonly IEventTracker _eventTracker;

        public RoomFeedbackService(
            IRoomFeedbackRepository feedbackRepo,
            IRoomRepository roomRepo,
            IEventTracker eventTracker)
        {
            _feedbackRepo = feedbackRepo;
            _roomRepo = roomRepo;
            _eventTracker = eventTracker;
        }

        private enum Eligibility
        {
            Eligible,
            IsHost,
            NotParticipant,
            TooEarly
        }

        /// <summary>
        /// Shared by submit and the Me endpoint so CanSubmit can never disagree with what a
        /// submit would actually do. "Participant" means actually admitted — a pending or
        /// rejected join request never put the user in the session.
        /// </summary>
        private async Task<Eligibility> CheckEligibilityAsync(Room room, Guid userId)
        {
            if (room.HostId == userId)
                return Eligibility.IsHost;

            var participant = await _roomRepo.GetParticipantAsync(room.Id, userId);
            if (participant == null
                || participant.Status is not (ParticipantStatus.Active or ParticipantStatus.Left or ParticipantStatus.Kicked))
                return Eligibility.NotParticipant;

            if (room.Status != RoomStatus.Ended
                && participant.Status is not (ParticipantStatus.Left or ParticipantStatus.Kicked))
                return Eligibility.TooEarly;

            return Eligibility.Eligible;
        }

        public async Task<Response<RoomFeedbackDto>> SubmitFeedbackAsync(Guid roomId, Guid userId, SubmitRoomFeedbackDto dto)
        {
            // DataAnnotations already cover this on the HTTP path; repeated here so the rule
            // holds for any other caller of the service.
            if (dto.Rating < 1 || dto.Rating > 5)
                return BadRequest<RoomFeedbackDto>("Rating must be between 1 and 5.");

            var comment = string.IsNullOrWhiteSpace(dto.Comment) ? null : dto.Comment.Trim();
            if (comment != null && comment.Length > 1000)
                return BadRequest<RoomFeedbackDto>("Comment cannot exceed 1000 characters.");

            var room = await _roomRepo.GetByIdAsync(roomId);
            if (room == null)
                return NotFound<RoomFeedbackDto>("Room not found.");

            switch (await CheckEligibilityAsync(room, userId))
            {
                case Eligibility.IsHost:
                    return BadRequest<RoomFeedbackDto>("Hosts cannot rate their own room.");
                case Eligibility.NotParticipant:
                    return Forbidden<RoomFeedbackDto>("Only participants of this room can submit feedback.");
                case Eligibility.TooEarly:
                    return BadRequest<RoomFeedbackDto>("Feedback is available after you leave the room or it ends.");
            }

            var (feedback, isUpdate) = await _feedbackRepo.UpsertAsync(roomId, userId, dto.Rating, comment);

            // Comment text deliberately stays out of analytics; only whether one was given.
            if (_eventTracker.NewEventEmissionEnabled)
            {
                _eventTracker.Track(EventTypes.RoomFeedbackSubmitted, userId, new
                {
                    roomId,
                    rating = dto.Rating,
                    hasComment = comment != null,
                    isUpdate
                });
            }

            return Success(ToDto(feedback), message: isUpdate ? "Feedback updated." : "Feedback submitted.");
        }

        public async Task<Response<MyRoomFeedbackStatusDto>> GetMyFeedbackStatusAsync(Guid roomId, Guid userId)
        {
            var room = await _roomRepo.GetByIdAsync(roomId);
            if (room == null)
                return NotFound<MyRoomFeedbackStatusDto>("Room not found.");

            var eligibility = await CheckEligibilityAsync(room, userId);
            var existing = await _feedbackRepo.GetByRoomAndUserAsync(roomId, userId);

            return Success(new MyRoomFeedbackStatusDto
            {
                CanSubmit = eligibility == Eligibility.Eligible,
                HasSubmitted = existing != null,
                Feedback = existing == null ? null : ToDto(existing)
            });
        }

        public async Task<Response<RoomFeedbackSummaryDto>> GetSummaryAsync(Guid roomId, Guid callerId, bool isAdmin)
        {
            var room = await _roomRepo.GetByIdAsync(roomId);
            if (room == null)
                return NotFound<RoomFeedbackSummaryDto>("Room not found.");

            if (!isAdmin && room.HostId != callerId)
                return Forbidden<RoomFeedbackSummaryDto>("Only the room host or an admin can view feedback.");

            var counts = await _feedbackRepo.GetRatingCountsAsync(roomId);

            // Every key 1..5 is present so clients can render the bars without gap-filling.
            var distribution = Enumerable.Range(1, 5)
                .ToDictionary(r => r, r => counts.TryGetValue(r, out var c) ? c : 0);

            var total = distribution.Values.Sum();
            var average = total == 0
                ? 0
                : Math.Round(distribution.Sum(kv => kv.Key * (double)kv.Value) / total, 2);

            return Success(new RoomFeedbackSummaryDto
            {
                RoomId = roomId,
                Count = total,
                AverageRating = average,
                Distribution = distribution
            });
        }

        public async Task<PagedResponse<AdminRoomFeedbackDto>> GetAdminFeedbackAsync(Guid? roomId, int? rating, int pageNumber, int pageSize)
        {
            var (totalCount, items) = await _feedbackRepo.GetPagedAsync(roomId, rating, pageNumber, pageSize);

            var result = items.Select(f => new AdminRoomFeedbackDto
            {
                Id = f.Id,
                RoomId = f.RoomId,
                RoomTitle = f.Room?.RoomTitle ?? string.Empty,
                UserId = f.UserId,
                FullName = f.User != null
                    ? $"{f.User.FirstName} {f.User.LastName}"
                    : "Unknown",
                Rating = f.Rating,
                Comment = f.Comment,
                CreatedAt = f.CreatedAt,
                UpdatedAt = f.UpdatedAt
            }).ToList();

            return Paginated(result, totalCount, pageNumber, pageSize);
        }

        private static RoomFeedbackDto ToDto(RoomFeedback f) => new()
        {
            Id = f.Id,
            RoomId = f.RoomId,
            Rating = f.Rating,
            Comment = f.Comment,
            CreatedAt = f.CreatedAt,
            UpdatedAt = f.UpdatedAt
        };
    }
}
