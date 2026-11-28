using System;

namespace Cocorra.DAL.Models
{
    /// <summary>Single row (<see cref="SingletonId"/>), created with defaults on first read.</summary>
    public class TextRoomGlobalSettings
    {
        public const int SingletonId = 1;

        public int Id { get; set; } = SingletonId;

        /// <summary>Prefills the dashboard create form; rooms copy values, they do not reference this row.</summary>
        public TextRoomSettings Defaults { get; set; } = new();

        public int ReportContextMessagesBefore { get; set; } = 25;

        public int ReportContextMessagesAfter { get; set; } = 25;

        public int DefaultMessageRetentionHours { get; set; } = 72;

        /// <summary>When false, Moderators may moderate users and messages but not create, edit or close rooms.</summary>
        public bool AllowModeratorsToManageRooms { get; set; }

        public DateTime? UpdatedAt { get; set; }

        public Guid? UpdatedById { get; set; }
    }
}
