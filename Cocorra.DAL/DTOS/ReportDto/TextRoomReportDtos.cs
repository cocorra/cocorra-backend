using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Cocorra.DAL.Enums;

namespace Cocorra.DAL.DTOS.ReportDto
{
    public class ReportTextRoomMessageDto
    {
        [Required]
        public ReportCategory Category { get; set; }

        [MaxLength(1000)]
        public string? Description { get; set; }
    }

    public class TextRoomReportResultDto
    {
        public Guid ReportId { get; set; }
        public ModerationSeverity Severity { get; set; }
        public string SeverityName { get; set; } = string.Empty;
    }

    public class ReportMessageContextDto
    {
        public Guid ReportId { get; set; }
        public Guid? TextRoomId { get; set; }
        public string? TextRoomTitle { get; set; }
        public long ReportedMessageId { get; set; }
        public int BeforeRequested { get; set; }
        public int AfterRequested { get; set; }
        public int CapturedBefore { get; set; }
        public int CapturedAfter { get; set; }
        /// <summary>False while messages after the reported one may still be appended.</summary>
        public bool IsComplete { get; set; }
        /// <summary>Ordered by Offset ascending.</summary>
        public List<ReportContextMessageDto> Messages { get; set; } = new();
    }

    public class ReportContextMessageDto
    {
        public long MessageId { get; set; }
        public int Offset { get; set; }
        /// <summary>Null when hidden from the viewer (an Admin's message shown to a Moderator).</summary>
        public Guid? SenderId { get; set; }
        public string SenderName { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public DateTime SentAt { get; set; }
        public bool IsReportedMessage { get; set; }
        public bool WasDeleted { get; set; }
    }
}
