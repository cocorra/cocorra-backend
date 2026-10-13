namespace Cocorra.DAL.DTOS.AdminDto
{
    public class BroadcastNotificationResultDto
    {
        public int TotalTargetUsers { get; set; }
        public int TokensCount { get; set; }
        public int SuccessCount { get; set; }
        public int FailureCount { get; set; }
        public int InAppNotificationsCreated { get; set; }
    }
}
