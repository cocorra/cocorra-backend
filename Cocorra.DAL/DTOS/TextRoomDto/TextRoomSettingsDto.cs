using System.Collections.Generic;

namespace Cocorra.DAL.DTOS.TextRoomDto
{
    /// <summary>Wire shape of <see cref="Cocorra.DAL.Models.TextRoomSettings"/>; bounds in <c>TextRoomLimits</c>.</summary>
    public class TextRoomSettingsDto
    {
        public bool IsAlwaysOn { get; set; }
        public int? DurationMinutes { get; set; } = 60;
        public int? RotateTopicEveryMinutes { get; set; }
        public bool ClearHistoryOnRotate { get; set; }
        public int? MaxParticipants { get; set; }
        public bool AutoOverflowEnabled { get; set; }
        public int? OverflowTriggerCount { get; set; }
        public int? MaxOverflowInstances { get; set; }
        public bool ShowOnlineCountToUsers { get; set; }
        public bool AllowPrivateChatFromRoom { get; set; } = true;
        public int SlowModeSeconds { get; set; }
        public int MaxMessageLength { get; set; } = 500;
        public int? MessageRetentionHours { get; set; }
        public int? ReportContextMessagesBefore { get; set; }
        public int? ReportContextMessagesAfter { get; set; }
    }

    public class TextRoomTopicDto
    {
        public string Title { get; set; } = string.Empty;
        public string? Subject { get; set; }
        public string? Description { get; set; }
        public List<string> Prompts { get; set; } = new();
    }

    public class TextRoomPresetDto
    {
        public string Key { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public TextRoomSettingsDto Settings { get; set; } = new();
    }

    public class TextRoomGlobalSettingsDto
    {
        public TextRoomSettingsDto Defaults { get; set; } = new();
        public int ReportContextMessagesBefore { get; set; } = 25;
        public int ReportContextMessagesAfter { get; set; } = 25;
        public int DefaultMessageRetentionHours { get; set; } = 72;
        public bool AllowModeratorsToManageRooms { get; set; }
    }
}
