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
    }
}
