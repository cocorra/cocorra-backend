using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Cocorra.BLL.Base;
using Cocorra.DAL.DTOS.ChatDto;
using Cocorra.DAL.DTOS.TextRoomDto;

namespace Cocorra.BLL.Services.TextRooms
{
    /// <summary>
    /// The mobile-facing text room surface. <c>viewerIsStaff</c> is true for Admin and Moderator
    /// callers: they always receive counts and are exempt from capacity, overflow and slow mode.
    /// </summary>
    public interface ITextRoomService
    {
        /// <summary>Active rooms, primaries first then their overflow instances by OverflowIndex.</summary>
        Task<Response<List<TextRoomListItemDto>>> GetActiveRoomsAsync(Guid userId, bool viewerIsStaff);

        /// <summary>NotFound for unknown rooms. Closed rooms are returned with Status "Closed".</summary>
        Task<Response<TextRoomClientConfigDto>> GetRoomConfigAsync(Guid roomId, Guid userId, bool viewerIsStaff);

        /// <summary>
        /// Upserts the participant row and decides placement; never fails with an exception for a
        /// business outcome — the result Status carries it. On Joined/Redirected the connection is
        /// registered with <see cref="ITextRoomPresenceTracker"/> for the returned RoomId while the
        /// family lock is still held — registering after the lock would let concurrent joiners all
        /// see the same free seat — and a presence update is broadcast when the user came online.
        /// Capacity: trigger = OverflowTriggerCount ?? MaxParticipants. A user already online in the
        /// room, or staff, always joins. Otherwise, when online &gt;= trigger: with AutoOverflowEnabled
        /// the user is placed in the first family instance (ordered by OverflowIndex) below its own
        /// trigger, spawning a new instance when none is and the family has fewer than
        /// (MaxOverflowInstances ?? HardMaxOverflowInstances) overflow instances → Redirected; else Full.
        /// </summary>
        Task<TextRoomJoinResultDto> JoinAsync(Guid roomId, Guid userId, bool viewerIsStaff, string connectionId);

        /// <summary>
        /// Removes this connection from the room, updates LastSeenAt and broadcasts presence when the
        /// user has no connection left there.
        /// </summary>
        Task LeaveAsync(Guid roomId, Guid userId, string connectionId);

        /// <summary><see cref="LeaveAsync"/> for every room the dropped connection was in.</summary>
        Task DisconnectAsync(string connectionId);

        /// <summary>
        /// Members only (has a participant row, not kicked): Forbidden otherwise. Excludes deleted
        /// messages and messages at or before HistoryClearedAt. Newest page first in the query,
        /// returned in ascending Id order. limit is clamped to [1, HistoryPageMax].
        /// </summary>
        Task<Response<List<TextRoomMessageDto>>> GetMessagesAsync(Guid roomId, Guid userId, long? beforeId, int limit);

        /// <summary>
        /// Pipeline, in order: room exists (NotFound) and Active (Closed) → caller is a non-kicked
        /// participant currently online (NotMember) → not muted (Muted, with MutedUntil) → trimmed
        /// content non-empty (Empty) → length &lt;= MaxMessageLength (TooLong) → slow mode unless staff
        /// (SlowMode, with RetryAfterSeconds) → IWordFilter (Blocked: nothing persisted) → persist →
        /// auto-report through ITextRoomReportService when the filter requires it → broadcast.
        /// Returns Masked instead of Sent when the filter masked the content.
        /// </summary>
        Task<SendTextRoomMessageResultDto> SendMessageAsync(Guid roomId, Guid userId, string? content, bool senderIsStaff);

        /// <summary>
        /// A persisted 1-on-1 message, saved through IChatService with OriginTextRoomId set, so it
        /// shows in the regular chat list after the room ends. Forbidden when the room disallows
        /// private chat, or when either user is not a non-kicked participant of the room.
        /// </summary>
        Task<Response<MessageDto>> SendPrivateMessageAsync(Guid roomId, Guid senderId, Guid targetUserId, string? content);
    }
}
