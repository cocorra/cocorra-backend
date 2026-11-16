using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;

namespace Cocorra.DAL.Repository.SupportRepository
{
    public interface ISupportRepository
    {
        Task AddTicketAsync(SupportTicket ticket);
        Task AddReportAsync(Report report);
        Task<List<Report>> GetFilteredReportsAsync(ReportCategory? category, string? status);
        Task<Report?> GetReportByIdAsync(Guid reportId);
        Task<SupportTicket?> GetTicketByIdAsync(Guid ticketId);
        Task UpdateReportAsync(Report report);

        // Chat Support
        Task<SupportChat?> GetUserOpenChatAsync(string userId);
        Task<SupportChat?> GetChatByIdAsync(Guid chatId);
        Task<int> GetPendingUserMessageCountAsync(Guid chatId);
        Task AddChatAsync(SupportChat chat);
        Task AddMessageAsync(SupportMessage message);
        Task UpdateChatAsync(SupportChat chat);
        Task<List<SupportChat>> GetPendingChatsAsync(int pageNumber, int pageSize);
        Task<int> GetPendingChatsCountAsync();
        Task<List<SupportChat>> GetAdminActiveChatsAsync(string adminId, int pageNumber, int pageSize);
        Task<int> GetAdminActiveChatsCountAsync(string adminId);
        Task<List<SupportChat>> GetUserChatHistoryAsync(string userId, int pageNumber, int pageSize);
        Task<int> GetUserChatHistoryCountAsync(string userId);
    }
}
