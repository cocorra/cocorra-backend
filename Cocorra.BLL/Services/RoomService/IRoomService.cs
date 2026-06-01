using Cocorra.DAL.DTOS.RoomDto;
using Cocorra.DAL.Enums;
using Cocorra.BLL.Base;
using Microsoft.AspNetCore.Http;

namespace Cocorra.BLL.Services.RoomService;

public interface IRoomService
{
    Task<Response<Guid>> CreateRoomAsync(CreateRoomDto dto, Guid hostId, IFormFile? roomImage = null);
    Task<Response<JoinRoomResultDto>> JoinRoomAsync(Guid roomId, Guid userId);
    Task<Response<bool>> ApproveUserAsync(Guid roomId, Guid targetUserId, Guid hostId);
    Task<Response<RoomStateDto>> GetRoomStateAsync(Guid roomId, Guid currentUserId);
    Task<Response<IEnumerable<RoomSummaryDto>>> GetRoomsFeedAsync(Guid currentUserId, RoomCategory? categoryId = null, int pageNumber = 1, int pageSize = 20);
    Task<Response<string>> ToggleReminderAsync(Guid roomId, Guid userId);
    Task<Response<string>> StartScheduledRoomAsync(Guid roomId, Guid hostId);
    Task<Response<string>> EndRoomAsync(Guid roomId, Guid hostId);
    Task LeaveRoomCleanupAsync(Guid roomId, Guid userId);
    Task<Response<IEnumerable<RoomSummaryDto>>> GetEndedRoomsHistoryAsync(int pageNumber = 1, int pageSize = 20);
}
