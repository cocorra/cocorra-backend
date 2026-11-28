using System;
using Cocorra.DAL.Enums;

namespace Cocorra.DAL.DTOS.ReportDto
{
    public class ReportDetailsDto
    {
        public Guid Id { get; set; }
        public Guid ReporterId { get; set; }
        public string ReporterName { get; set; } = string.Empty;
        public Guid? ReportedUserId { get; set; }
        public string? ReportedUserName { get; set; }
        public Guid? ReportedRoomId { get; set; }
        public string? ReportedRoomName { get; set; }
        public string? ReportedRoomHostName { get; set; }
        public DateTime? ReportedRoomCreatedAt { get; set; }
        public int Category { get; set; }
        public string CategoryName { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        // Always null in report lists: a link is only issued (and audit-logged) by
        // GET Support/admin/reports/{id}/screenshot-url. Kept for response shape.
        public string? ScreenshotPath { get; set; }
        /// <summary>True when the report has a screenshot to fetch through screenshot-url.</summary>
        public bool HasScreenshot { get; set; }
        public string Status { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        /// <summary>Null on reports filed before severity existed.</summary>
        public ModerationSeverity? Severity { get; set; }
        public string? SeverityName { get; set; }
        /// <summary>"User" or "AutoModeration".</summary>
        public string Source { get; set; } = nameof(ReportSource.User);
        public Guid? ReportedTextRoomId { get; set; }
        public string? ReportedTextRoomTitle { get; set; }
        public long? ReportedTextRoomMessageId { get; set; }
        public string? MatchedTerm { get; set; }
        /// <summary>True when a ±N transcript can be opened through message-context.</summary>
        public bool HasMessageContext { get; set; }
        public bool IsMessageContextComplete { get; set; }
        /// <summary>Distinct users who reported the same text-room message.</summary>
        public int SameMessageReportCount { get; set; }
    }
}
