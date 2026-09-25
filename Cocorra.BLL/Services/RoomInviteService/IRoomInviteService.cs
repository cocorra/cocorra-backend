using System;
using System.Threading.Tasks;
using Cocorra.BLL.Base;
using Cocorra.DAL.DTOS.RoomInviteDto;

namespace Cocorra.BLL.Services.RoomInviteService
{
    public interface IRoomInviteService
    {
        Task<Response<CreateRoomInviteResultDto>> CreateInviteAsync(Guid roomId, Guid callerId, CreateRoomInviteDto? dto);

        /// <summary>Anonymous lookup. Never changes the invite.</summary>
        Task<Response<ResolveRoomInviteDto>> ResolveInviteAsync(string inviteCode);

        /// <summary>
        /// Data is an <see cref="AcceptRoomInviteResultDto"/> on 200 and a
        /// <see cref="ResolveRoomInviteDto"/> on 410, so a dead link reads the same as it does
        /// from resolve.
        /// </summary>
        Task<Response<object>> AcceptInviteAsync(string inviteCode, Guid userId);

        Task<Response<ResolveRoomInviteDto>> RevokeInviteAsync(string inviteCode, Guid callerId, bool isAdmin);

        Task<Response<RoomInviteStatsDto>> GetStatsAsync(DateTime? fromUtc, DateTime? toUtc, Guid? roomId, Guid? inviterUserId);
    }
}
