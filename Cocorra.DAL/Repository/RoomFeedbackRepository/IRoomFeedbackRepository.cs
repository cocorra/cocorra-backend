using Cocorra.DAL.Models;
using Cocorra.DAL.Repository.GenericRepository;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Cocorra.DAL.Repository.RoomFeedbackRepository
{
    public interface IRoomFeedbackRepository : IGenericRepositoryAsync<RoomFeedback>
    {
        Task<RoomFeedback?> GetByRoomAndUserAsync(Guid roomId, Guid userId);

        /// <summary>
        /// Inserts the caller's feedback for the room, or updates it if one already exists.
        /// Returns the saved row and whether it was an update.
        /// </summary>
        Task<(RoomFeedback Feedback, bool IsUpdate)> UpsertAsync(Guid roomId, Guid userId, int rating, string? comment);

        /// <summary>Rating → count for the room. Ratings nobody gave are absent.</summary>
        Task<Dictionary<int, int>> GetRatingCountsAsync(Guid roomId);

        /// <summary>Newest first, with Room and User loaded.</summary>
        Task<(int TotalCount, List<RoomFeedback> Items)> GetPagedAsync(Guid? roomId, int? rating, int pageNumber, int pageSize);
    }
}
