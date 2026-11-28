using System;
using System.Collections.Generic;
using Cocorra.DAL.Enums;

namespace Cocorra.DAL.Models
{
    public class TextRoom : BaseEntity
    {
        public string Title { get; set; } = string.Empty;
        public string? Subject { get; set; }
        public string? Description { get; set; }

        /// <summary>Pinned discussion questions, in display order.</summary>
        public List<string> DiscussionPrompts { get; set; } = new();

        /// <summary>Topics an always-on room rotates through. Empty: rotation only clears history.</summary>
        public List<TextRoomTopic> TopicPool { get; set; } = new();

        public int TopicRotationIndex { get; set; }

        public TextRoomStatus Status { get; set; } = TextRoomStatus.Active;

        public TextRoomSettings Settings { get; set; } = new();

        public DateTime OpensAt { get; set; } = DateTime.UtcNow;

        /// <summary>Null for always-on rooms.</summary>
        public DateTime? ClosesAt { get; set; }

        public DateTime? ClosedAt { get; set; }

        public string? CloseReason { get; set; }

        public DateTime? NextRotationAt { get; set; }

        /// <summary>Clients are never served messages created at or before this instant.</summary>
        public DateTime? HistoryClearedAt { get; set; }

        /// <summary>The primary room of an overflow family; null on the primary itself.</summary>
        public Guid? ParentRoomId { get; set; }

        /// <summary>1 for the primary, 2+ for overflow instances ("Room #2").</summary>
        public int OverflowIndex { get; set; } = 1;

        public Guid? ScheduleId { get; set; }

        public Guid? CreatedById { get; set; }

        /// <summary>Bumped on every change clients must re-render for.</summary>
        public int ConfigVersion { get; set; } = 1;
    }
}
