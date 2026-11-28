using System;
using System.Collections.Generic;

namespace Cocorra.DAL.DTOS.TextRoomDto
{
    /// <summary>
    /// Everything the mobile app renders a room from, sent over REST and on every
    /// <c>TextRoomConfigUpdated</c> hub event. Counts are null for regular users unless
    /// <see cref="ShowOnlineCount"/> is true.
    /// </summary>
    public class TextRoomClientConfigDto
    {
        public Guid RoomId { get; set; }
        public string Title { get; set; } = string.Empty;
        public string? Subject { get; set; }
        public string? Description { get; set; }
        public List<string> DiscussionPrompts { get; set; } = new();
        public string Status { get; set; } = string.Empty;
        public bool IsAlwaysOn { get; set; }
        public DateTime OpensAt { get; set; }
        public DateTime? ClosesAt { get; set; }
        /// <summary>For drift-free countdowns: clients compute ClosesAt - ServerTimeUtc.</summary>
        public DateTime ServerTimeUtc { get; set; }
        public DateTime? NextRotationAt { get; set; }
        public int? MaxParticipants { get; set; }
        public int OverflowIndex { get; set; } = 1;
        public Guid? ParentRoomId { get; set; }
        public bool ShowOnlineCount { get; set; }
        public int? OnlineCount { get; set; }
        public int? TotalParticipants { get; set; }
        public bool AllowPrivateChat { get; set; }
        public int SlowModeSeconds { get; set; }
        public int MaxMessageLength { get; set; }
        public int ConfigVersion { get; set; }
    }

    public class TextRoomListItemDto
    {
        public Guid RoomId { get; set; }
        public string Title { get; set; } = string.Empty;
        public string? Subject { get; set; }
        public bool IsAlwaysOn { get; set; }
        public DateTime? ClosesAt { get; set; }
        public int OverflowIndex { get; set; } = 1;
        public Guid? ParentRoomId { get; set; }
        public bool IsFull { get; set; }
        public int? OnlineCount { get; set; }
        public int? TotalParticipants { get; set; }
    }

    public class TextRoomMessageDto
    {
        public long Id { get; set; }
        public Guid RoomId { get; set; }
        public Guid SenderId { get; set; }
        public string SenderName { get; set; } = string.Empty;
        public string? SenderProfilePicturePath { get; set; }
        public string Content { get; set; } = string.Empty;
        public bool IsMasked { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    public enum TextRoomJoinStatus
    {
        Joined = 0,
        /// <summary>The requested room was at capacity; the user was placed in <see cref="TextRoomJoinResultDto.RoomId"/> instead.</summary>
        Redirected = 1,
        Full = 2,
        Closed = 3,
        Kicked = 4,
        NotFound = 5,
        NotOpenYet = 6
    }

    public class TextRoomJoinResultDto
    {
        public TextRoomJoinStatus Status { get; set; }
        public string StatusName => Status.ToString();
        /// <summary>The room actually joined; differs from the requested one when Redirected.</summary>
        public Guid? RoomId { get; set; }
        public TextRoomClientConfigDto? Config { get; set; }
        public List<TextRoomMessageDto> RecentMessages { get; set; } = new();
        public DateTime? MutedUntil { get; set; }
    }

    public enum SendTextRoomMessageStatus
    {
        Sent = 0,
        Masked = 1,
        Blocked = 2,
        SlowMode = 3,
        TooLong = 4,
        Empty = 5,
        Muted = 6,
        NotMember = 7,
        Closed = 8,
        NotFound = 9
    }

    public class SendTextRoomMessageResultDto
    {
        public SendTextRoomMessageStatus Status { get; set; }
        public string StatusName => Status.ToString();
        public TextRoomMessageDto? Message { get; set; }
        public int? RetryAfterSeconds { get; set; }
        public DateTime? MutedUntil { get; set; }
        public string? Error { get; set; }
    }
}
