using System.Collections.Generic;

namespace Cocorra.DAL.Models
{
    /// <summary>
    /// The closed set of values stored in <see cref="AuditLog.Action"/>. The admin dashboard
    /// filters on these exact strings, so renaming one is an API break.
    /// </summary>
    public static class AuditActions
    {
        public const string UserStatusChanged = "User.StatusChanged";
        public const string UserDeviceAndEmailBlocked = "User.DeviceAndEmailBlocked";
        public const string UserRolesChanged = "User.RolesChanged";
        public const string UserCreatedWithRole = "User.CreatedWithRole";
        public const string NotificationBroadcastSent = "Notification.BroadcastSent";
        public const string RoleDisplayNameChanged = "Role.DisplayNameChanged";
        // A presigned URL to a private file was handed to a staff member. Details list each file.
        public const string VoiceUrlIssued = "Storage.VoiceUrlIssued";
        public const string ScreenshotUrlIssued = "Storage.ScreenshotUrlIssued";

        public const string VoiceReviewDecision = "VoiceReview.Decision";

        public const string TicketViewed = "Ticket.Viewed";
        public const string TicketStatusChanged = "Ticket.StatusChanged";

        public const string ReportViewed = "Report.Viewed";
        public const string ReportStatusChanged = "Report.StatusChanged";
        public const string ReportActionTaken = "Report.ActionTaken";

        public const string SupportChatClaimed = "SupportChat.Claimed";
        public const string SupportChatReplied = "SupportChat.Replied";
        public const string SupportChatClosed = "SupportChat.Closed";

        public const string RoomCreated = "Room.Created";
        public const string RoomStarted = "Room.Started";
        public const string RoomEnded = "Room.Ended";
        public const string RoomUserApproved = "Room.UserApproved";
        public const string RoomStageApproved = "Room.StageApproved";
        public const string RoomMovedToAudience = "Room.MovedToAudience";
        public const string RoomUserKicked = "Room.UserKicked";
        public const string RoomUserMuted = "Room.UserMuted";
        public const string RoomExtraTimeGranted = "Room.ExtraTimeGranted";

        public const string TextRoomCreated = "TextRoom.Created";
        public const string TextRoomUpdated = "TextRoom.Updated";
        public const string TextRoomClosed = "TextRoom.Closed";
        // FromState/ToState carry Created, Updated, Deleted, Enabled or Disabled.
        public const string TextRoomScheduleChanged = "TextRoom.ScheduleChanged";
        public const string TextRoomSettingsChanged = "TextRoom.SettingsChanged";
        public const string TextRoomMessageDeleted = "TextRoom.MessageDeleted";
        public const string TextRoomUserMuted = "TextRoom.UserMuted";
        public const string TextRoomUserKicked = "TextRoom.UserKicked";

        // FromState/ToState carry Created, Updated, Deleted, Activated, Deactivated or BulkAdded.
        public const string WordDictionaryChanged = "WordDictionary.Changed";

        // A staff member opened the ±N transcript attached to a report.
        public const string ReportContextViewed = "Report.ContextViewed";

        public static readonly IReadOnlySet<string> All = new HashSet<string>
        {
            UserStatusChanged,
            UserDeviceAndEmailBlocked,
            UserRolesChanged,
            UserCreatedWithRole,
            NotificationBroadcastSent,
            RoleDisplayNameChanged,
            VoiceUrlIssued,
            ScreenshotUrlIssued,
            VoiceReviewDecision,
            TicketViewed,
            TicketStatusChanged,
            ReportViewed,
            ReportStatusChanged,
            ReportActionTaken,
            SupportChatClaimed,
            SupportChatReplied,
            SupportChatClosed,
            RoomCreated,
            RoomStarted,
            RoomEnded,
            RoomUserApproved,
            RoomStageApproved,
            RoomMovedToAudience,
            RoomUserKicked,
            RoomUserMuted,
            RoomExtraTimeGranted,
            TextRoomCreated,
            TextRoomUpdated,
            TextRoomClosed,
            TextRoomScheduleChanged,
            TextRoomSettingsChanged,
            TextRoomMessageDeleted,
            TextRoomUserMuted,
            TextRoomUserKicked,
            WordDictionaryChanged,
            ReportContextViewed
        };
    }

    /// <summary>The closed set of values stored in <see cref="AuditLog.Category"/>.</summary>
    public static class AuditCategories
    {
        public const string VoiceReview = "VoiceReview";
        public const string UserModeration = "UserModeration";
        public const string Ticket = "Ticket";
        public const string Report = "Report";
        public const string SupportChat = "SupportChat";
        public const string Room = "Room";
        public const string Roles = "Roles";
        public const string Notification = "Notification";
        public const string TextRoom = "TextRoom";
        public const string ContentPolicy = "ContentPolicy";

        public static readonly IReadOnlySet<string> All = new HashSet<string>
        {
            VoiceReview, UserModeration, Ticket, Report, SupportChat, Room, Roles, Notification, TextRoom, ContentPolicy
        };
    }

    /// <summary>The closed set of values stored in <see cref="AuditLog.EntityType"/>.</summary>
    public static class AuditEntityTypes
    {
        public const string User = "User";
        public const string Ticket = "Ticket";
        public const string Report = "Report";
        public const string SupportChat = "SupportChat";
        public const string Room = "Room";
        public const string Role = "Role";
        public const string TextRoom = "TextRoom";
        public const string TextRoomSchedule = "TextRoomSchedule";
        public const string TextRoomMessage = "TextRoomMessage";
        public const string TextRoomSettings = "TextRoomSettings";
        public const string WordDictionaryEntry = "WordDictionaryEntry";
    }
}
