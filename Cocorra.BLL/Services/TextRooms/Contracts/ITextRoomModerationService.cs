using System;
using System.Threading.Tasks;
using Cocorra.BLL.Base;

namespace Cocorra.BLL.Services.TextRooms
{
    /// <summary>
    /// Staff sanctions inside text rooms. Shared rules, checked in this order:
    /// reason trimmed to 3–1000 chars (BadRequest) → target exists (NotFound) → target is not the
    /// actor (BadRequest) → a Moderator acting on an Admin gets NotFound (Admins are invisible to
    /// Moderators), on any other AppRoles.ProtectedFromModerators role Forbidden. Each success
    /// writes one audit row (AuditCategories.TextRoom, Reason set) and broadcasts to the room.
    /// </summary>
    public interface ITextRoomModerationService
    {
        /// <summary>Soft delete; broadcasts TextRoomMessageDeleted. Audit: TextRoomMessageDeleted, TargetUserId = sender.</summary>
        Task<Response<string>> DeleteMessageAsync(long messageId, string? reason, StaffActor actor);

        /// <summary>Sets MutedUntil = now + minutes (1–43200); broadcasts TextRoomUserMuted. Audit: TextRoomUserMuted.</summary>
        Task<Response<string>> MuteUserAsync(Guid roomId, Guid userId, int minutes, string? reason, StaffActor actor);

        /// <summary>
        /// Marks the participant kicked (cannot rejoin this room), drops their presence and
        /// connections from the room group; broadcasts TextRoomKicked. Audit: TextRoomUserKicked.
        /// </summary>
        Task<Response<string>> KickUserAsync(Guid roomId, Guid userId, string? reason, StaffActor actor);
    }
}
