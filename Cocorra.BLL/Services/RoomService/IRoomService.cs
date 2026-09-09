using Cocorra.DAL.DTOS.RoomDto;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;
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
    Task<Response<string>> EndRoomAsync(Guid roomId, Guid hostId, string endReason = RoomEndReasons.HostEnded);
    Task LeaveRoomCleanupAsync(Guid roomId, Guid userId);
    Task<Response<IEnumerable<RoomSummaryDto>>> GetEndedRoomsHistoryAsync(int pageNumber = 1, int pageSize = 20);

    /// <summary>
    /// Records that the host's connection dropped, starting the reconnect grace window.
    /// The room stays Live and nobody is ejected. Returns false when there is nothing to
    /// hold open — the room is not Live, the user is not its host, or a window is already
    /// running (a second drop must not extend the first one's deadline).
    /// </summary>
    Task<bool> MarkHostDisconnectedAsync(Guid roomId, Guid hostId);

    /// <summary>
    /// Cancels a running grace window because the host came back. Returns true only if a
    /// window was actually open, so the caller can tell a genuine reconnect from an ordinary
    /// join and broadcast accordingly.
    /// </summary>
    Task<bool> ClearHostDisconnectedAsync(Guid roomId, Guid hostId);

    /// <summary>
    /// Ends every Live room whose host has been gone longer than the grace window.
    /// Returns the rooms it ended, so the caller can tell their participants.
    /// </summary>
    Task<IReadOnlyList<Guid>> EndRoomsWithExpiredHostGraceAsync(DateTime disconnectedBefore);
}
