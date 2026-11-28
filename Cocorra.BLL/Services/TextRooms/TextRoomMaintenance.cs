using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cocorra.DAL.AppMetaData;
using Cocorra.DAL.Data;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;
using Cocorra.DAL.Time;
using Microsoft.EntityFrameworkCore;

namespace Cocorra.BLL.Services.TextRooms
{
    public sealed class TextRoomMaintenance : ITextRoomMaintenance
    {
        private const int MaintenanceBatchLimit = 200;
        private const int PurgeBatchSize = 500;
        private const string ExpiredCloseReason = "Expired";
        private const string ReplacedCloseReason = "Replaced by scheduled room";

        private readonly AppDbContext _db;
        private readonly ITextRoomPresenceTracker _presence;
        private readonly ITextRoomBroadcaster _broadcaster;
        private readonly ITextRoomSettingsProvider _settingsProvider;
        private readonly TimeProvider _time;

        public TextRoomMaintenance(
            AppDbContext db,
            ITextRoomPresenceTracker presence,
            ITextRoomBroadcaster broadcaster,
            ITextRoomSettingsProvider settingsProvider,
            TimeProvider time)
        {
            _db = db;
            _presence = presence;
            _broadcaster = broadcaster;
            _settingsProvider = settingsProvider;
            _time = time;
        }

        public async Task<int> SpawnDueSchedulesAsync(CancellationToken ct = default)
        {
            var now = _time.GetUtcNow().UtcDateTime;
            var dueSchedules = await _db.TextRoomSchedules
                .Where(s => s.IsEnabled && s.NextSpawnAt <= now)
                .OrderBy(s => s.NextSpawnAt)
                .Take(MaintenanceBatchLimit)
                .ToListAsync(ct);

            if (dueSchedules.Count == 0)
                return 0;

            var cairoNow = CairoTime.ToLocal(now);
            var currentMinuteOfDay = cairoNow.Hour * 60 + cairoNow.Minute;
            var closedRoomIds = new List<Guid>();
            var spawnedCount = 0;

            foreach (var schedule in dueSchedules)
            {
                schedule.NextSpawnAt = AdvanceByWholeSteps(schedule.NextSpawnAt, schedule.SpawnEveryMinutes, now);
                schedule.UpdatedAt = now;

                if (schedule.TopicPool is null || schedule.TopicPool.Count == 0)
                    continue;

                if (!IsWithinDailyWindow(currentMinuteOfDay, schedule.WindowStartMinuteOfDay, schedule.WindowEndMinuteOfDay))
                    continue;

                if (schedule.ClosePreviousOnSpawn)
                    closedRoomIds.AddRange(await CloseSchedulePreviousRoomsAsync(schedule.Id, now, ct));

                _db.TextRooms.Add(BuildScheduledRoom(schedule, now));
                schedule.LastSpawnAt = now;
                spawnedCount++;
            }

            await _db.SaveChangesAsync(ct);

            foreach (var closedRoomId in closedRoomIds)
                await _broadcaster.RoomClosedAsync(closedRoomId, ReplacedCloseReason);

            if (spawnedCount > 0 || closedRoomIds.Count > 0)
                await _broadcaster.RoomListChangedAsync();

            return spawnedCount;
        }

        public async Task<int> ProcessRoomLifecycleAsync(CancellationToken ct = default)
        {
            var now = _time.GetUtcNow().UtcDateTime;

            var dueToOpen = await _db.TextRooms
                .Where(r => r.Status == TextRoomStatus.Scheduled && r.OpensAt <= now)
                .OrderBy(r => r.OpensAt)
                .Take(MaintenanceBatchLimit)
                .ToListAsync(ct);

            var dueToClose = await _db.TextRooms
                .Where(r => r.Status == TextRoomStatus.Active && r.ClosesAt != null && r.ClosesAt <= now)
                .OrderBy(r => r.ClosesAt)
                .Take(MaintenanceBatchLimit)
                .ToListAsync(ct);

            if (dueToOpen.Count == 0 && dueToClose.Count == 0)
                return 0;

            var expiredRoomIds = ApplyLifecycleTransitions(dueToOpen, dueToClose, now);
            await _db.SaveChangesAsync(ct);

            foreach (var closedRoomId in expiredRoomIds)
                await _broadcaster.RoomClosedAsync(closedRoomId, ExpiredCloseReason);

            await _broadcaster.RoomListChangedAsync();
            return dueToOpen.Count + dueToClose.Count;
        }

        public async Task<int> RotateDueTopicsAsync(CancellationToken ct = default)
        {
            var now = _time.GetUtcNow().UtcDateTime;
            var dueRooms = await _db.TextRooms
                .Where(r => r.Status == TextRoomStatus.Active
                    && r.Settings.IsAlwaysOn
                    && r.NextRotationAt != null
                    && r.NextRotationAt <= now)
                .OrderBy(r => r.NextRotationAt)
                .Take(MaintenanceBatchLimit)
                .ToListAsync(ct);

            if (dueRooms.Count == 0)
                return 0;

            foreach (var room in dueRooms)
                ApplyTopicRotation(room, now);

            await _db.SaveChangesAsync(ct);
            await BroadcastRotatedRoomsAsync(dueRooms, now, ct);

            return dueRooms.Count;
        }

        public async Task<int> PurgeExpiredMessagesAsync(CancellationToken ct = default)
        {
            var now = _time.GetUtcNow().UtcDateTime;
            var roomsWithMessages = await _db.TextRooms
                .AsNoTracking()
                .Where(r => _db.TextRoomMessages.Any(m => m.TextRoomId == r.Id && !m.IsReported))
                .Select(r => new { r.Id, r.Settings.MessageRetentionHours })
                .ToListAsync(ct);

            if (roomsWithMessages.Count == 0)
                return 0;

            var globalSettings = await _settingsProvider.GetAsync(ct);
            var totalDeleted = 0;

            foreach (var room in roomsWithMessages)
            {
                var retentionHours = Math.Clamp(
                    room.MessageRetentionHours ?? globalSettings.DefaultMessageRetentionHours,
                    TextRoomLimits.RetentionHoursMin,
                    TextRoomLimits.RetentionHoursMax);

                totalDeleted += await PurgeRoomMessagesInBatchesAsync(room.Id, now.AddHours(-retentionHours), ct);
            }

            return totalDeleted;
        }

        private async Task<List<Guid>> CloseSchedulePreviousRoomsAsync(
            Guid scheduleId, DateTime now, CancellationToken ct)
        {
            var previousRooms = await _db.TextRooms
                .Where(r => r.ScheduleId == scheduleId && r.Status != TextRoomStatus.Closed)
                .ToListAsync(ct);

            var closedIds = new List<Guid>(previousRooms.Count);
            foreach (var previous in previousRooms)
            {
                previous.Status = TextRoomStatus.Closed;
                previous.ClosedAt = now;
                previous.CloseReason = ReplacedCloseReason;
                previous.UpdatedAt = now;
                closedIds.Add(previous.Id);
            }

            return closedIds;
        }

        private async Task BroadcastRotatedRoomsAsync(List<TextRoom> rotatedRooms, DateTime now, CancellationToken ct)
        {
            var participantCounts = await TextRoomMapper.LoadParticipantCountsAsync(
                _db, rotatedRooms.Select(r => r.Id).ToList(), ct);

            foreach (var room in rotatedRooms)
            {
                var counts = new TextRoomCounts(
                    _presence.GetOnlineCount(room.Id),
                    participantCounts.GetValueOrDefault(room.Id));
                await _broadcaster.ConfigUpdatedAsync(TextRoomMapper.ToUserClientConfigDto(room, counts, now));

                if (room.Settings.ClearHistoryOnRotate)
                    await _broadcaster.HistoryClearedAsync(room.Id);
            }
        }

        private static List<Guid> ApplyLifecycleTransitions(
            List<TextRoom> dueToOpen, List<TextRoom> dueToClose, DateTime now)
        {
            var expiredRoomIds = new List<Guid>(dueToClose.Count);

            foreach (var room in dueToOpen)
            {
                if (!room.Settings.IsAlwaysOn && room.ClosesAt.HasValue && room.ClosesAt.Value <= now)
                {
                    MarkExpired(room, now);
                    expiredRoomIds.Add(room.Id);
                }
                else
                {
                    room.Status = TextRoomStatus.Active;
                    room.UpdatedAt = now;
                }
            }

            foreach (var room in dueToClose)
            {
                MarkExpired(room, now);
                expiredRoomIds.Add(room.Id);
            }

            return expiredRoomIds;
        }

        private static TextRoom BuildScheduledRoom(TextRoomSchedule schedule, DateTime now)
        {
            var count = schedule.TopicPool.Count;
            var topicIndex = ((schedule.NextTopicIndex % count) + count) % count;
            var topic = schedule.TopicPool[topicIndex];
            schedule.NextTopicIndex++;

            var settings = schedule.Settings.Clone();
            DateTime? closesAt = settings.IsAlwaysOn
                ? null
                : now.AddMinutes(settings.DurationMinutes ?? 60);
            DateTime? nextRotationAt = settings.IsAlwaysOn && settings.RotateTopicEveryMinutes.HasValue
                ? now.AddMinutes(settings.RotateTopicEveryMinutes.Value)
                : null;

            return new TextRoom
            {
                Id = Guid.NewGuid(),
                Title = topic.Title,
                Subject = topic.Subject,
                Description = topic.Description,
                DiscussionPrompts = topic.Prompts is null ? new List<string>() : topic.Prompts.ToList(),
                TopicPool = nextRotationAt.HasValue
                    ? schedule.TopicPool.Select(TextRoomMapper.CloneTopic).ToList()
                    : new List<TextRoomTopic>(),
                TopicRotationIndex = (topicIndex + 1) % count,
                Status = TextRoomStatus.Active,
                Settings = settings,
                OpensAt = now,
                ClosesAt = closesAt,
                NextRotationAt = nextRotationAt,
                OverflowIndex = 1,
                ScheduleId = schedule.Id,
                CreatedById = schedule.CreatedById,
                CreatedAt = now,
                ConfigVersion = 1
            };
        }

        private static void ApplyTopicRotation(TextRoom room, DateTime now)
        {
            if (room.TopicPool is { Count: > 0 })
            {
                var count = room.TopicPool.Count;
                var index = ((room.TopicRotationIndex % count) + count) % count;
                var nextTopic = room.TopicPool[index];

                room.Title = nextTopic.Title;
                room.Subject = nextTopic.Subject;
                room.Description = nextTopic.Description;
                room.DiscussionPrompts = nextTopic.Prompts is null
                    ? new List<string>()
                    : nextTopic.Prompts.ToList();
                room.TopicRotationIndex = (index + 1) % count;
            }

            if (room.Settings.ClearHistoryOnRotate)
                room.HistoryClearedAt = now;

            room.ConfigVersion++;
            room.UpdatedAt = now;
            room.NextRotationAt = room.Settings.RotateTopicEveryMinutes.HasValue
                ? AdvanceByWholeSteps(room.NextRotationAt!.Value, room.Settings.RotateTopicEveryMinutes.Value, now)
                : null;
        }

        private async Task<int> PurgeRoomMessagesInBatchesAsync(Guid roomId, DateTime cutoff, CancellationToken ct)
        {
            var deletedForRoom = 0;

            while (!ct.IsCancellationRequested)
            {
                // Two-step batched delete works identically on SQL Server and SQLite (SQLite rejects DELETE ... LIMIT).
                var batchIds = await _db.TextRoomMessages
                    .AsNoTracking()
                    .Where(m => m.TextRoomId == roomId && !m.IsReported && m.CreatedAt < cutoff)
                    .OrderBy(m => m.Id)
                    .Select(m => m.Id)
                    .Take(PurgeBatchSize)
                    .ToListAsync(ct);

                if (batchIds.Count == 0)
                    break;

                var deleted = await _db.TextRoomMessages
                    .Where(m => batchIds.Contains(m.Id) && !m.IsReported)
                    .ExecuteDeleteAsync(ct);

                deletedForRoom += deleted;

                if (batchIds.Count < PurgeBatchSize || deleted == 0)
                    break;
            }

            return deletedForRoom;
        }

        private static void MarkExpired(TextRoom room, DateTime now)
        {
            room.Status = TextRoomStatus.Closed;
            room.ClosedAt = now;
            room.CloseReason = ExpiredCloseReason;
            room.UpdatedAt = now;
        }

        private static bool IsWithinDailyWindow(int minuteOfDay, int? startMinute, int? endMinute)
        {
            if (!startMinute.HasValue || !endMinute.HasValue)
                return true;

            // Boundary cases: normal window [start, end] (e.g. 09:00..17:00 or 00:00..23:59),
            // equal start == end (single minute), and midnight-wrapping window where start > end.
            if (startMinute.Value <= endMinute.Value)
                return minuteOfDay >= startMinute.Value && minuteOfDay <= endMinute.Value;

            return minuteOfDay >= startMinute.Value || minuteOfDay <= endMinute.Value;
        }

        private static DateTime AdvanceByWholeSteps(DateTime scheduledAt, int stepMinutes, DateTime now)
        {
            var stepTicks = TimeSpan.FromMinutes(Math.Max(1, stepMinutes)).Ticks;
            var elapsedTicks = Math.Max(0, now.Ticks - scheduledAt.Ticks);
            var steps = (elapsedTicks / stepTicks) + 1;
            return scheduledAt.AddTicks(steps * stepTicks);
        }
    }
}
