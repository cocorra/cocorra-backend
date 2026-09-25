using System;
using System.Threading.Tasks;
using Cocorra.BLL.Base;
using Cocorra.DAL.DTOS.RoomFeedbackDto;

namespace Cocorra.BLL.Services.RoomFeedbackService
{
    public interface IRoomFeedbackService
    {
        Task<Response<RoomFeedbackDto>> SubmitFeedbackAsync(Guid roomId, Guid userId, SubmitRoomFeedbackDto dto);
        Task<Response<MyRoomFeedbackStatusDto>> GetMyFeedbackStatusAsync(Guid roomId, Guid userId);
        Task<Response<RoomFeedbackSummaryDto>> GetSummaryAsync(Guid roomId, Guid callerId, bool isAdmin);
        Task<PagedResponse<AdminRoomFeedbackDto>> GetAdminFeedbackAsync(Guid? roomId, int? rating, int pageNumber, int pageSize);
    }
}
