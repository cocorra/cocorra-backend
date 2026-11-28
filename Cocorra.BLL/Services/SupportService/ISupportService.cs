using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Cocorra.BLL.Base;
using Cocorra.DAL.DTOS.ReportDto;
using Cocorra.DAL.DTOS.SupportDto;
using Cocorra.DAL.DTOS.SupportChatDto;
using Cocorra.DAL.Enums;

namespace Cocorra.BLL.Services.SupportService
{
    public interface ISupportService
    {
        Task<Response<string>> SubmitTicketAsync(Guid? userId, SubmitSupportTicketDto dto);
        Task<Response<string>> SubmitReportAsync(Guid reporterId, SubmitReportDto dto);
        Task<PagedResponse<ReportDetailsDto>> GetFilteredReportsAsync(ReportCategory? category, string? status, int pageNumber = 1, int pageSize = 20, bool callerIsAdmin = false);
        Task<PagedResponse<ReportDetailsDto>> GetFilteredReportsAsync(ReportListQuery query, bool callerIsAdmin);
        Task<Response<ReportDetailsDto>> GetReportByIdAsync(Guid reportId, Guid callerId, bool callerIsAdmin);
        Task<Response<string>> UpdateReportStatusAsync(Guid reportId, string newStatus, string? reason, Guid callerId, bool callerIsAdmin);
        Task<PagedResponse<SupportTicketDetailsDto>> GetAdminTicketsAsync(SupportTicketType? type, SupportTicketStatus? status, int pageNumber, int pageSize, bool callerIsAdmin);
        Task<Response<SupportTicketDetailsDto>> GetTicketByIdAsync(Guid ticketId, Guid callerId, bool callerIsAdmin);
        Task<Response<string>> UpdateTicketStatusAsync(Guid ticketId, SupportTicketStatus newStatus, string? reason, Guid callerId, bool callerIsAdmin);
        /// <summary>A fresh presigned URL to a report's screenshot; NotFound when there is none.</summary>
        Task<Response<Cocorra.DAL.DTOS.StorageDto.PrivateFileUrlDto>> GetReportScreenshotUrlAsync(Guid reportId, bool callerIsAdmin);
        /// <summary>A fresh presigned URL to a support ticket's screenshot; NotFound when there is none.</summary>
        Task<Response<Cocorra.DAL.DTOS.StorageDto.PrivateFileUrlDto>> GetTicketScreenshotUrlAsync(Guid ticketId, bool callerIsAdmin);
        Task<Response<string>> TakeActionOnReportAsync(Guid reportId, TakeReportActionDto dto, Guid callerId, bool callerIsAdmin);

        // Chat Support
        Task<Response<SendMessageResultDto>> SendMessageAsync(string userId, SendMessageDto dto);
        Task<Response<string>> ClaimChatAsync(Guid chatId, string adminId);
        Task<Response<AdminReplyResultDto>> AdminReplyAsync(Guid chatId, string adminId, SendMessageDto dto, bool callerIsAdmin);
        Task<Response<string>> CloseChatAsync(Guid chatId, string adminId, bool callerIsAdmin);
        Task<Response<List<PendingChatDto>>> GetPendingChatsAsync(int pageNumber, int pageSize);
        Task<Response<List<SupportChatDetailsDto>>> GetAdminActiveChatsAsync(string adminId, int pageNumber, int pageSize);
        Task<Response<List<SupportChatDetailsDto>>> GetUserChatHistoryAsync(string userId, int pageNumber, int pageSize);
        Task<Response<SupportChatDetailsDto>> GetUserOpenChatAsync(string userId);
    }
}
