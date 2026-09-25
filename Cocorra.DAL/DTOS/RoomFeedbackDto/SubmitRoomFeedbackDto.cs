using System.ComponentModel.DataAnnotations;

namespace Cocorra.DAL.DTOS.RoomFeedbackDto
{
    public class SubmitRoomFeedbackDto
    {
        [Range(1, 5, ErrorMessage = "Rating must be between 1 and 5.")]
        public int Rating { get; set; }

        [MaxLength(1000, ErrorMessage = "Comment cannot exceed 1000 characters.")]
        public string? Comment { get; set; }
    }
}
