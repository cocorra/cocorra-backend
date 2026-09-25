namespace Cocorra.DAL.DTOS.RoomFeedbackDto
{
    /// <summary>
    /// Tells the app whether to show the feedback prompt. CanSubmit mirrors the submit
    /// eligibility rules, so a true here means a submit right now would be accepted.
    /// </summary>
    public class MyRoomFeedbackStatusDto
    {
        public bool CanSubmit { get; set; }
        public bool HasSubmitted { get; set; }
        public RoomFeedbackDto? Feedback { get; set; }
    }
}
