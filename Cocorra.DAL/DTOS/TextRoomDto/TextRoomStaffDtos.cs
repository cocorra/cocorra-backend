using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Cocorra.DAL.AppMetaData;

namespace Cocorra.DAL.DTOS.TextRoomDto
{
    public class TextRoomUpsertDto
    {
        [Required, MinLength(TextRoomLimits.TitleMin), MaxLength(TextRoomLimits.TitleMax)]
        public string Title { get; set; } = string.Empty;

        [MaxLength(TextRoomLimits.SubjectMax)]
        public string? Subject { get; set; }

        [MaxLength(TextRoomLimits.DescriptionMax)]
        public string? Description { get; set; }

        public List<string> DiscussionPrompts { get; set; } = new();

        public List<TextRoomTopicDto> TopicPool { get; set; } = new();

        /// <summary>Create only. Null opens immediately; a future instant creates a Scheduled room.</summary>
        public DateTime? OpensAt { get; set; }

        [Required]
        public TextRoomSettingsDto Settings { get; set; } = new();
    }

    /// <summary>Staff view: counts are always present, never message content.</summary>
    public class TextRoomStaffViewDto
    {
        public Guid RoomId { get; set; }
        public string Title { get; set; } = string.Empty;
        public string? Subject { get; set; }
        public string? Description { get; set; }
        public List<string> DiscussionPrompts { get; set; } = new();
        public List<TextRoomTopicDto> TopicPool { get; set; } = new();
        public string Status { get; set; } = string.Empty;
        public DateTime OpensAt { get; set; }
        public DateTime? ClosesAt { get; set; }
        public DateTime? ClosedAt { get; set; }
        public DateTime? NextRotationAt { get; set; }
        public DateTime ServerTimeUtc { get; set; }
        public int OverflowIndex { get; set; } = 1;
        public Guid? ParentRoomId { get; set; }
        public int OverflowInstanceCount { get; set; }
        public Guid? ScheduleId { get; set; }
        public int OnlineCount { get; set; }
        public int TotalParticipants { get; set; }
        public int ConfigVersion { get; set; }
        public TextRoomSettingsDto Settings { get; set; } = new();
        public DateTime CreatedAt { get; set; }
    }

    public class TextRoomOverviewDto
    {
        /// <summary>Distinct users online in any text room right now.</summary>
        public int GlobalOnlineUsers { get; set; }
        public int ActiveRoomCount { get; set; }
        public DateTime ServerTimeUtc { get; set; }
        /// <summary>Active and Scheduled rooms; overflow instances included, each with its ParentRoomId.</summary>
        public List<TextRoomStaffViewDto> Rooms { get; set; } = new();
        public bool CallerCanManageRooms { get; set; }
    }

    public class TextRoomScheduleUpsertDto
    {
        [Required, MinLength(TextRoomLimits.TitleMin), MaxLength(TextRoomLimits.TitleMax)]
        public string Name { get; set; } = string.Empty;

        public bool IsEnabled { get; set; } = true;

        [Range(TextRoomLimits.SpawnEveryMinutesMin, TextRoomLimits.SpawnEveryMinutesMax)]
        public int SpawnEveryMinutes { get; set; } = 60;

        [Range(0, TextRoomLimits.MinuteOfDayMax)]
        public int? WindowStartMinuteOfDay { get; set; }

        [Range(0, TextRoomLimits.MinuteOfDayMax)]
        public int? WindowEndMinuteOfDay { get; set; }

        public List<TextRoomTopicDto> TopicPool { get; set; } = new();

        public bool ClosePreviousOnSpawn { get; set; } = true;

        /// <summary>Null starts spawning on the next maintenance tick.</summary>
        public DateTime? FirstSpawnAt { get; set; }

        [Required]
        public TextRoomSettingsDto Settings { get; set; } = new();
    }

    public class TextRoomScheduleDto
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public bool IsEnabled { get; set; }
        public int SpawnEveryMinutes { get; set; }
        public int? WindowStartMinuteOfDay { get; set; }
        public int? WindowEndMinuteOfDay { get; set; }
        public List<TextRoomTopicDto> TopicPool { get; set; } = new();
        public int NextTopicIndex { get; set; }
        public bool ClosePreviousOnSpawn { get; set; }
        public DateTime NextSpawnAt { get; set; }
        public DateTime? LastSpawnAt { get; set; }
        public TextRoomSettingsDto Settings { get; set; } = new();
        public DateTime CreatedAt { get; set; }
    }

    public class StaffReasonDto
    {
        public string? Reason { get; set; }
    }

    public class TextRoomMuteDto
    {
        [Range(TextRoomLimits.MuteMinutesMin, TextRoomLimits.MuteMinutesMax)]
        public int Minutes { get; set; } = 60;

        public string? Reason { get; set; }
    }

    public class ToggleEnabledDto
    {
        public bool IsEnabled { get; set; }
    }
}
