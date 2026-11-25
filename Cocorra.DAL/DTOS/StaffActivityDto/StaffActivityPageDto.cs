using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Cocorra.DAL.DTOS.StaffActivityDto
{
    public class StaffActivityPageDto
    {
        public StaffProfileDto Staff { get; set; } = new();
        public StaffActivitySummaryDto Summary { get; set; } = new();
        public List<StaffActivityItemDto> Items { get; set; } = new();
        public int TotalCount { get; set; }
        public int PageNumber { get; set; }
        public int PageSize { get; set; }
        public int TotalPages { get; set; }
    }

    public class StaffProfileDto
    {
        public Guid UserId { get; set; }
        public string FullName { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public List<string> Roles { get; set; } = new();
        public string Status { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public string? ProfilePicture { get; set; }
    }

    public class StaffActivitySummaryDto
    {
        public int TotalActions { get; set; }
        public DateTime? LastActivityAtUtc { get; set; }
        public int VoiceUrlsIssued { get; set; }
        public int VoiceReviews { get; set; }
        public int VoiceApprovals { get; set; }
        public int VoiceReRecords { get; set; }
        public int VoiceRejections { get; set; }
        public int TicketsViewed { get; set; }
        public int TicketStatusChanges { get; set; }
        public int ReportsViewed { get; set; }
        public int ReportStatusChanges { get; set; }
        public int ReportsResolved { get; set; }
        public int ReportsRejected { get; set; }
        public int WarnsIssued { get; set; }
        public int MutesIssued { get; set; }
        public int BansIssued { get; set; }
        public int UserStatusChanges { get; set; }
        public int ChatsClaimed { get; set; }
        public int ChatRepliesSent { get; set; }
        public int ChatsClosed { get; set; }
        public int RoomsCreated { get; set; }
        public int RoomsStarted { get; set; }
        public int RoomsEnded { get; set; }
        public int RoomModerationActions { get; set; }
    }

    public class StaffActivityItemDto
    {
        public long Id { get; set; }
        public DateTime OccurredAtUtc { get; set; }
        public string Action { get; set; } = string.Empty;
        public string? Category { get; set; }
        public string? EntityType { get; set; }
        public string? EntityId { get; set; }
        public Guid? TargetUserId { get; set; }
        public string? TargetName { get; set; }
        public string? TargetEmail { get; set; }
        public string? FromState { get; set; }
        public string? ToState { get; set; }
        public string? Reason { get; set; }
        public JsonElement? Details { get; set; }
    }
}
