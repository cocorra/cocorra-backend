using System;
using System.Collections.Generic;

namespace Cocorra.DAL.DTOS.RoomFeedbackDto
{
    /// <summary>
    /// Aggregates only — visible to the room's host, so it deliberately carries no
    /// commenter identities or comment text.
    /// </summary>
    public class RoomFeedbackSummaryDto
    {
        public Guid RoomId { get; set; }
        public int Count { get; set; }

        /// <summary>0 when there is no feedback yet; otherwise rounded to 2 decimal places.</summary>
        public double AverageRating { get; set; }

        /// <summary>Rating (1..5) → count. All five keys are always present.</summary>
        public Dictionary<int, int> Distribution { get; set; } = new();
    }
}
