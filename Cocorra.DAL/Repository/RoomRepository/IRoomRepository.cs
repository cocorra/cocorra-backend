using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;
using Cocorra.DAL.Repository.GenericRepository;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Cocorra.DAL.Repository.RoomRepository
{
    public interface IRoomRepository : IGenericRepositoryAsync<Room>
    {
        // 1. دوال القراءة (Read)
        Task<RoomParticipant?> GetParticipantAsync(Guid roomId, Guid userId);
        Task<List<RoomParticipant>> GetRoomParticipantsAsync(Guid roomId);
        Task<List<RoomParticipant>> GetStageSpeakersAsync(Guid roomId);

        // 2. النواقص: دوال التعديل الخاصة بالمشاركين (Write)
        Task AddParticipantAsync(RoomParticipant participant);
        Task UpdateParticipantAsync(RoomParticipant participant);
        Task RemoveParticipantAsync(RoomParticipant participant);
        Task<List<RoomReminder>> GetRemindersByRoomIdAsync(Guid roomId);
        Task RemoveRemindersAsync(IEnumerable<RoomReminder> reminders);
        Task AddNotificationsAsync(IEnumerable<Notification> notifications);

        Task<List<Room>> GetActiveRoomsAsync(RoomCategory? categoryId = null, int pageNumber = 1, int pageSize = 20); // لنجلب الغرف اللايف والمجدولة بس
        Task<RoomReminder?> GetRoomReminderAsync(Guid roomId, Guid userId);
        Task<int> GetRoomRemindersCountAsync(Guid roomId);
        Task AddRoomReminderAsync(RoomReminder reminder);
        Task RemoveRoomReminderAsync(RoomReminder reminder);

        Task<List<Room>> GetEndedRoomsAsync(int pageNumber = 1, int pageSize = 20);

        /// <summary>
        /// Live rooms whose host dropped before <paramref name="disconnectedBefore"/> — the
        /// grace window has expired and the session should be closed. Ordered oldest first so
        /// a backlog after a restart is cleared in the order the rooms were abandoned.
        /// </summary>
        Task<List<Room>> GetRoomsWithExpiredHostGraceAsync(DateTime disconnectedBefore);

        /// <summary>
        /// Live rooms that went live before <paramref name="wentLiveBefore"/>, oldest first.
        ///
        /// <para>
        /// A prefilter, not the answer: each room's real deadline depends on its own
        /// DurationHours, so the caller passes the cutoff for the <i>shortest</i> bookable
        /// duration and applies the per-room deadline itself. Deliberately a plain comparison
        /// with no date arithmetic in the query — the arithmetic would have to be translated by
        /// every provider, including the SQLite used in tests.
        /// </para>
        ///
        /// <para>
        /// Rooms with a null WentLiveAt are excluded; see Room.WentLiveAt for why those are
        /// exempt rather than backfilled.
        /// </para>
        /// </summary>
        Task<List<Room>> GetLiveRoomsStartedBeforeAsync(DateTime wentLiveBefore);
    }
}