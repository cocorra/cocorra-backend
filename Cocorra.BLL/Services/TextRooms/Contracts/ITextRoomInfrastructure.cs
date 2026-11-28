using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Cocorra.DAL.DTOS.TextRoomDto;
using Cocorra.DAL.Models;

namespace Cocorra.BLL.Services.TextRooms
{
    /// <summary>
    /// In-memory presence (singleton). A user is online in a room while at least one of their
    /// connections is registered there. Thread-safe.
    /// </summary>
    public interface ITextRoomPresenceTracker
    {
        /// <summary>True when the user was not online in the room before this connection.</summary>
        bool AddConnection(Guid roomId, Guid userId, string connectionId);

        /// <summary>Every room the connection was in, with whether the user went offline there.</summary>
        IReadOnlyList<TextRoomPresenceChange> RemoveConnection(string connectionId);

        /// <summary>Removes the connection from one room only; null when it was not registered there.</summary>
        TextRoomPresenceChange? RemoveConnectionFromRoom(Guid roomId, string connectionId);

        /// <summary>Removes all of the user's connections from the room; returns the connection ids removed.</summary>
        IReadOnlyList<string> RemoveUser(Guid roomId, Guid userId);

        int GetOnlineCount(Guid roomId);

        bool IsOnline(Guid roomId, Guid userId);

        /// <summary>Distinct users online in any room.</summary>
        int GetGlobalOnlineUserCount();
    }

    public sealed record TextRoomPresenceChange(Guid RoomId, Guid UserId, bool UserWentOffline);

    /// <summary>
    /// Real-time fan-out, implemented over SignalR in the API layer. Hub groups:
    /// <c>text-room:{roomId}</c> for members, <c>text-room-staff</c> for dashboard monitors,
    /// and all connections for list changes. Implementations swallow transport failures.
    /// </summary>
    public interface ITextRoomBroadcaster
    {
        Task MessageReceivedAsync(TextRoomMessageDto message);
        Task MessageDeletedAsync(Guid roomId, long messageId);
        /// <summary>Sends the user-audience config (counts already nulled when hidden).</summary>
        Task ConfigUpdatedAsync(TextRoomClientConfigDto config);
        /// <summary>To staff always; to the room group only when <paramref name="visibleToUsers"/>.</summary>
        Task PresenceChangedAsync(Guid roomId, int onlineCount, int totalParticipants, bool visibleToUsers);
        Task RoomClosedAsync(Guid roomId, string reason);
        Task HistoryClearedAsync(Guid roomId);
        Task UserMutedAsync(Guid roomId, Guid userId, DateTime mutedUntil, string reason);
        /// <summary>Notifies the user, then removes <paramref name="connectionIds"/> from the room group.</summary>
        Task UserKickedAsync(Guid roomId, Guid userId, string reason, IReadOnlyList<string> connectionIds);
        Task RoomListChangedAsync();
    }

    /// <summary>Cached read of the global settings row (IMemoryCache), created with defaults when missing.</summary>
    public interface ITextRoomSettingsProvider
    {
        Task<TextRoomGlobalSettings> GetAsync(CancellationToken ct = default);
        void Invalidate();
        /// <summary>Room override ?? global value, clamped to [0, TextRoomLimits.ReportContextMax].</summary>
        Task<(int Before, int After)> GetReportContextWindowAsync(TextRoomSettings roomSettings, CancellationToken ct = default);
        Task<int> GetRetentionHoursAsync(TextRoomSettings roomSettings, CancellationToken ct = default);
    }

    /// <summary>
    /// One pass of each periodic job; the hosted service calls all four every tick. Each returns
    /// the number of rooms/messages affected. All time comes from the injected TimeProvider.
    /// </summary>
    public interface ITextRoomMaintenance
    {
        /// <summary>
        /// Enabled schedules with NextSpawnAt &lt;= now. Inside the daily window (Cairo time, wrapping
        /// past midnight) a room is spawned from TopicPool[NextTopicIndex % count] with a copy of
        /// the schedule settings, closing the schedule's previous open room first when
        /// ClosePreviousOnSpawn. Outside the window nothing spawns. Either way NextSpawnAt advances
        /// by whole SpawnEveryMinutes steps until it is in the future (no burst catch-up).
        /// </summary>
        Task<int> SpawnDueSchedulesAsync(CancellationToken ct = default);

        /// <summary>Scheduled rooms whose OpensAt passed become Active; Active rooms past ClosesAt close (reason "Expired").</summary>
        Task<int> ProcessRoomLifecycleAsync(CancellationToken ct = default);

        /// <summary>
        /// Always-on rooms with NextRotationAt &lt;= now: next TopicPool entry (if any) becomes the
        /// title/subject/description/prompts, HistoryClearedAt = now when ClearHistoryOnRotate,
        /// ConfigVersion++, NextRotationAt advances, broadcasts config (and HistoryCleared).
        /// </summary>
        Task<int> RotateDueTopicsAsync(CancellationToken ct = default);

        /// <summary>
        /// Hard-deletes non-reported messages older than the room's retention, in batches, so
        /// storage stays bounded. Reported messages are never purged here.
        /// </summary>
        Task<int> PurgeExpiredMessagesAsync(CancellationToken ct = default);
    }
}
