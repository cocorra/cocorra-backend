using System;

namespace Cocorra.DAL.DTOS.RoomFeedbackDto
{
    public class RoomFeedbackDto
    {
        public Guid Id { get; set; }
        public Guid RoomId { get; set; }
        public int Rating { get; set; }
        public string? Comment { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }
    }
}
