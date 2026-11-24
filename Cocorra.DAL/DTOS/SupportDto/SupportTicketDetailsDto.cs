using System;
using Cocorra.DAL.Enums;

namespace Cocorra.DAL.DTOS.SupportDto
{
    public class SupportTicketDetailsDto
    {
        public Guid Id { get; set; }
        /// <summary>Null for tickets submitted without signing in, or whose user was deleted.</summary>
        public Guid? UserId { get; set; }
        public string UserName { get; set; } = string.Empty;
        public string? UserEmail { get; set; }
        public string? ContactEmail { get; set; }
        public SupportTicketType Type { get; set; }
        public string Message { get; set; } = string.Empty;
        /// <summary>True when the ticket has a screenshot to fetch through
        /// GET Support/admin/tickets/{id}/screenshot-url. A path or URL is never listed.</summary>
        public bool HasScreenshot { get; set; }
        public SupportTicketStatus Status { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public DateTime? ResolvedAt { get; set; }
    }
}
