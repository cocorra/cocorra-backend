using Cocorra.DAL.DTOS.ReportDto;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;

namespace Cocorra.DAL.Repository.SupportRepository
{
    public sealed record ReportContextSummary(
        HashSet<Guid> ReportIdsWithContext,
        Dictionary<long, int> SameMessageUserReportCounts);

    public interface ISupportRepository
    {
        Task AddTicketAsync(SupportTicket ticket);
        Task AddReportAsync(Report report);
        Task<(int TotalCount, List<Report> Items)> GetPagedReportsAsync(ReportCategory? category, string? status, int pageNumber, int pageSize, bool excludeAdmins = false);
        Task<(int TotalCount, List<Report> Items)> GetPagedReportsAsync(ReportListQuery query, bool excludeAdmins);
        Task<ReportContextSummary> GetReportContextSummaryAsync(IReadOnlyCollection<Guid> reportIds, IReadOnlyCollection<long> textRoomMessageIds);
        Task<Report?> GetReportByIdAsync(Guid reportId);
        Task<Report?> GetReportDetailsByIdAsync(Guid reportId);
        Task<Guid?> GetRoomHostIdAsync(Guid roomId);
        Task<SupportTicket?> GetTicketByIdAsync(Guid ticketId);
        Task<SupportTicket?> GetTicketDetailsByIdAsync(Guid ticketId);
        Task<(int TotalCount, List<SupportTicket> Items)> GetPagedTicketsAsync(SupportTicketType? type, SupportTicketStatus? status, int pageNumber, int pageSize, bool excludeAdmins = false);
        Task UpdateTicketAsync(SupportTicket ticket);
        Task UpdateReportAsync(Report report);

        // Chat Support
        Task<SupportChat?> GetUserOpenChatAsync(string userId);
        Task<SupportChat?> GetChatByIdAsync(Guid chatId);
        Task<int> GetPendingUserMessageCountAsync(Guid chatId);
        Task AddChatAsync(SupportChat chat);
        Task AddMessageAsync(SupportMessage message);
        Task UpdateChatAsync(SupportChat chat);
        Task ReleaseActiveChatsForStaffAsync(string adminId);
        Task<List<SupportChat>> GetPendingChatsAsync(int pageNumber, int pageSize);
        Task<int> GetPendingChatsCountAsync();
        Task<List<SupportChat>> GetAdminActiveChatsAsync(string adminId, int pageNumber, int pageSize);
        Task<int> GetAdminActiveChatsCountAsync(string adminId);
        Task<List<SupportChat>> GetUserChatHistoryAsync(string userId, int pageNumber, int pageSize);
        Task<int> GetUserChatHistoryCountAsync(string userId);
    }
}
