namespace Cocorra.DAL.Enums
{
    public enum TextRoomStatus
    {
        Scheduled = 0,
        Active = 1,
        Closed = 2
    }

    /// <summary>Shared by dictionary entries and reports, so a dictionary hit maps onto a report without translation.</summary>
    public enum ModerationSeverity
    {
        Low = 1,
        Medium = 2,
        High = 3,
        Critical = 4
    }

    public enum WordMatchType
    {
        Contains = 1,
        WholeWord = 2
    }

    public enum WordCategory
    {
        Profanity = 1,
        Harassment = 2,
        ContactLeak = 3,
        Spam = 4,
        Custom = 5
    }

    public enum WordAction
    {
        BlockMessage = 1,
        MaskWord = 2,
        AllowAndAutoReport = 3
    }

    public enum ReportSource
    {
        User = 0,
        AutoModeration = 1
    }
}
