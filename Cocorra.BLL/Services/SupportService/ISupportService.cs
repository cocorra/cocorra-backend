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
        Task<Response<List<ReportDetailsDto>>> GetFilteredReportsAsync(ReportCategory? category, string? status);
        Task<Response<string>> UpdateReportStatusAsync(Guid reportId, string newStatus);
        /// <summary>A fresh presigned URL to a report's screenshot; NotFound when there is none.</summary>
        Task<Response<Cocorra.DAL.DTOS.StorageDto.PrivateFileUrlDto>> GetReportScreenshotUrlAsync(Guid reportId);
        /// <summary>A fresh presigned URL to a support ticket's screenshot; NotFound when there is none.</summary>
        Task<Response<Cocorra.DAL.DTOS.StorageDto.PrivateFileUrlDto>> GetTicketScreenshotUrlAsync(Guid ticketId);
        Task<Response<string>> TakeActionOnReportAsync(Guid reportId, TakeReportActionDto dto);

        // Chat Support
        Task<Response<SendMessageResultDto>> SendMessageAsync(string userId, SendMessageDto dto);
        Task<Response<string>> ClaimChatAsync(Guid chatId, string adminId);
        Task<Response<AdminReplyResultDto>> AdminReplyAsync(Guid chatId, string adminId, SendMessageDto dto);
        Task<Response<string>> CloseChatAsync(Guid chatId, string adminId);
        Task<Response<List<PendingChatDto>>> GetPendingChatsAsync(int pageNumber, int pageSize);
        Task<Response<List<SupportChatDetailsDto>>> GetAdminActiveChatsAsync(string adminId, int pageNumber, int pageSize);
        Task<Response<List<SupportChatDetailsDto>>> GetUserChatHistoryAsync(string userId, int pageNumber, int pageSize);
        Task<Response<SupportChatDetailsDto>> GetUserOpenChatAsync(string userId);
    }
}
