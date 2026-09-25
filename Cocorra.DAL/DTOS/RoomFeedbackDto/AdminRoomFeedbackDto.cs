using System;

namespace Cocorra.DAL.DTOS.RoomFeedbackDto
{
    public class AdminRoomFeedbackDto
    {
        public Guid Id { get; set; }
        public Guid RoomId { get; set; }
        public string RoomTitle { get; set; } = string.Empty;
        public Guid UserId { get; set; }
        public string FullName { get; set; } = string.Empty;
        public int Rating { get; set; }
        public string? Comment { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }
    }
}
