using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cocorra.DAL.Data;
using Cocorra.DAL.DTOS.TextRoomDto;
using Cocorra.DAL.Models;
using Microsoft.EntityFrameworkCore;

namespace Cocorra.BLL.Services.TextRooms
{
    internal readonly record struct TextRoomCounts(int Online, int Total);

    internal static class TextRoomMapper
    {
        public static string FormatDisplayName(string? firstName, string? lastName)
        {
            var first = firstName?.Trim();
            var last = lastName?.Trim();

            if (string.IsNullOrEmpty(first) && string.IsNullOrEmpty(last))
                return "User";
            if (string.IsNullOrEmpty(first))
                return last!;
            if (string.IsNullOrEmpty(last))
                return first;

            return $"{first} {last}";
        }

        public static string? TrimToNull(string? text) =>
            string.IsNullOrWhiteSpace(text) ? null : text.Trim();

        public static List<string> TrimPrompts(IEnumerable<string>? prompts) =>
            prompts is null ? new List<string>() : prompts.Select(p => p.Trim()).ToList();

        public static TextRoomSettings ToEntity(TextRoomSettingsDto dto)
        {
            var settings = new TextRoomSettings();
            CopySettings(dto, settings);
            return settings;
        }

        public static void CopySettings(TextRoomSettingsDto source, TextRoomSettings target)
        {
            target.IsAlwaysOn = source.IsAlwaysOn;
            target.DurationMinutes = source.DurationMinutes;
            target.RotateTopicEveryMinutes = source.RotateTopicEveryMinutes;
            target.ClearHistoryOnRotate = source.ClearHistoryOnRotate;
            target.MaxParticipants = source.MaxParticipants;
            target.AutoOverflowEnabled = source.AutoOverflowEnabled;
            target.OverflowTriggerCount = source.OverflowTriggerCount;
            target.MaxOverflowInstances = source.MaxOverflowInstances;
            target.ShowOnlineCountToUsers = source.ShowOnlineCountToUsers;
            target.AllowPrivateChatFromRoom = source.AllowPrivateChatFromRoom;
            target.SlowModeSeconds = source.SlowModeSeconds;
            target.MaxMessageLength = source.MaxMessageLength;
            target.MessageRetentionHours = source.MessageRetentionHours;
            target.ReportContextMessagesBefore = source.ReportContextMessagesBefore;
            target.ReportContextMessagesAfter = source.ReportContextMessagesAfter;
        }

        public static TextRoomSettingsDto ToDto(TextRoomSettings entity) => new()
        {
            IsAlwaysOn = entity.IsAlwaysOn,
            DurationMinutes = entity.DurationMinutes,
            RotateTopicEveryMinutes = entity.RotateTopicEveryMinutes,
            ClearHistoryOnRotate = entity.ClearHistoryOnRotate,
            MaxParticipants = entity.MaxParticipants,
            AutoOverflowEnabled = entity.AutoOverflowEnabled,
            OverflowTriggerCount = entity.OverflowTriggerCount,
            MaxOverflowInstances = entity.MaxOverflowInstances,
            ShowOnlineCountToUsers = entity.ShowOnlineCountToUsers,
            AllowPrivateChatFromRoom = entity.AllowPrivateChatFromRoom,
            SlowModeSeconds = entity.SlowModeSeconds,
            MaxMessageLength = entity.MaxMessageLength,
            MessageRetentionHours = entity.MessageRetentionHours,
            ReportContextMessagesBefore = entity.ReportContextMessagesBefore,
            ReportContextMessagesAfter = entity.ReportContextMessagesAfter
        };

        public static TextRoomTopic ToEntity(TextRoomTopicDto dto) => new()
        {
            Title = dto.Title.Trim(),
            Subject = TrimToNull(dto.Subject),
            Description = TrimToNull(dto.Description),
            Prompts = TrimPrompts(dto.Prompts)
        };

        public static TextRoomTopicDto ToDto(TextRoomTopic entity) => new()
        {
            Title = entity.Title,
            Subject = entity.Subject,
            Description = entity.Description,
            Prompts = entity.Prompts is null ? new List<string>() : entity.Prompts.ToList()
        };

        public static TextRoomTopic CloneTopic(TextRoomTopic source) => new()
        {
            Title = source.Title,
            Subject = source.Subject,
            Description = source.Description,
            Prompts = source.Prompts is null ? new List<string>() : source.Prompts.ToList()
        };

        public static TextRoomClientConfigDto ToUserClientConfigDto(
            TextRoom room, TextRoomCounts counts, DateTime serverTimeUtc)
        {
            var config = ToStaffClientConfigDto(room, counts, serverTimeUtc);
            if (!room.Settings.ShowOnlineCountToUsers)
            {
                config.OnlineCount = null;
                config.TotalParticipants = null;
            }

            return config;
        }

        public static TextRoomClientConfigDto ToStaffClientConfigDto(
            TextRoom room, TextRoomCounts counts, DateTime serverTimeUtc) => new()
        {
            RoomId = room.Id,
            Title = room.Title,
            Subject = room.Subject,
            Description = room.Description,
            DiscussionPrompts = room.DiscussionPrompts is null ? new List<string>() : room.DiscussionPrompts.ToList(),
            Status = room.Status.ToString(),
            IsAlwaysOn = room.Settings.IsAlwaysOn,
            OpensAt = room.OpensAt,
            ClosesAt = room.ClosesAt,
            ServerTimeUtc = serverTimeUtc,
            NextRotationAt = room.NextRotationAt,
            MaxParticipants = room.Settings.MaxParticipants,
            OverflowIndex = room.OverflowIndex,
            ParentRoomId = room.ParentRoomId,
            ShowOnlineCount = room.Settings.ShowOnlineCountToUsers,
            OnlineCount = counts.Online,
            TotalParticipants = counts.Total,
            AllowPrivateChat = room.Settings.AllowPrivateChatFromRoom,
            SlowModeSeconds = room.Settings.SlowModeSeconds,
            MaxMessageLength = room.Settings.MaxMessageLength,
            ConfigVersion = room.ConfigVersion
        };

        public static TextRoomListItemDto ToUserListItemDto(TextRoom room, TextRoomCounts counts)
        {
            var summary = ToStaffListItemDto(room, counts);
            if (!room.Settings.ShowOnlineCountToUsers)
            {
                summary.OnlineCount = null;
                summary.TotalParticipants = null;
            }

            return summary;
        }

        public static TextRoomListItemDto ToStaffListItemDto(TextRoom room, TextRoomCounts counts) => new()
        {
            RoomId = room.Id,
            Title = room.Title,
            Subject = room.Subject,
            IsAlwaysOn = room.Settings.IsAlwaysOn,
            ClosesAt = room.ClosesAt,
            OverflowIndex = room.OverflowIndex,
            ParentRoomId = room.ParentRoomId,
            IsFull = room.Settings.MaxParticipants.HasValue && counts.Online >= room.Settings.MaxParticipants.Value,
            OnlineCount = counts.Online,
            TotalParticipants = counts.Total
        };

        public static TextRoomStaffViewDto ToStaffViewDto(
            TextRoom room, TextRoomCounts counts, DateTime serverTimeUtc, int overflowInstanceCount) => new()
        {
            RoomId = room.Id,
            Title = room.Title,
            Subject = room.Subject,
            Description = room.Description,
            DiscussionPrompts = room.DiscussionPrompts is null ? new List<string>() : room.DiscussionPrompts.ToList(),
            TopicPool = room.TopicPool is null ? new List<TextRoomTopicDto>() : room.TopicPool.Select(ToDto).ToList(),
            Status = room.Status.ToString(),
            OpensAt = room.OpensAt,
            ClosesAt = room.ClosesAt,
            ClosedAt = room.ClosedAt,
            NextRotationAt = room.NextRotationAt,
            ServerTimeUtc = serverTimeUtc,
            OverflowIndex = room.OverflowIndex,
            ParentRoomId = room.ParentRoomId,
            OverflowInstanceCount = overflowInstanceCount,
            ScheduleId = room.ScheduleId,
            OnlineCount = counts.Online,
            TotalParticipants = counts.Total,
            ConfigVersion = room.ConfigVersion,
            Settings = ToDto(room.Settings),
            CreatedAt = room.CreatedAt
        };

        public static TextRoomMessageDto ToMessageDto(
            TextRoomMessage message, string senderName, string? senderProfilePicturePath) => new()
        {
            Id = message.Id,
            RoomId = message.TextRoomId,
            SenderId = message.SenderId,
            SenderName = senderName,
            SenderProfilePicturePath = senderProfilePicturePath,
            Content = message.Content,
            IsMasked = message.IsMasked,
            CreatedAt = message.CreatedAt
        };

        public static TextRoomScheduleDto ToScheduleDto(TextRoomSchedule schedule) => new()
        {
            Id = schedule.Id,
            Name = schedule.Name,
            IsEnabled = schedule.IsEnabled,
            SpawnEveryMinutes = schedule.SpawnEveryMinutes,
            WindowStartMinuteOfDay = schedule.WindowStartMinuteOfDay,
            WindowEndMinuteOfDay = schedule.WindowEndMinuteOfDay,
            TopicPool = schedule.TopicPool is null ? new List<TextRoomTopicDto>() : schedule.TopicPool.Select(ToDto).ToList(),
            NextTopicIndex = schedule.NextTopicIndex,
            ClosePreviousOnSpawn = schedule.ClosePreviousOnSpawn,
            NextSpawnAt = schedule.NextSpawnAt,
            LastSpawnAt = schedule.LastSpawnAt,
            Settings = ToDto(schedule.Settings),
            CreatedAt = schedule.CreatedAt
        };

        public static TextRoomGlobalSettingsDto ToGlobalSettingsDto(TextRoomGlobalSettings settings) => new()
        {
            Defaults = ToDto(settings.Defaults),
            ReportContextMessagesBefore = settings.ReportContextMessagesBefore,
            ReportContextMessagesAfter = settings.ReportContextMessagesAfter,
            DefaultMessageRetentionHours = settings.DefaultMessageRetentionHours,
            AllowModeratorsToManageRooms = settings.AllowModeratorsToManageRooms
        };

        public static List<TextRoom> OrderByFamily(IEnumerable<TextRoom> rooms)
        {
            var roomList = rooms.ToList();
            var primaryOpensAt = roomList
                .Where(r => r.ParentRoomId is null)
                .ToDictionary(r => r.Id, r => r.OpensAt);

            return roomList
                .OrderBy(r => primaryOpensAt.TryGetValue(r.ParentRoomId ?? r.Id, out var opensAt) ? opensAt : r.OpensAt)
                .ThenBy(r => r.ParentRoomId ?? r.Id)
                .ThenBy(r => r.OverflowIndex)
                .ToList();
        }

        public static async Task<Dictionary<Guid, int>> LoadParticipantCountsAsync(
            AppDbContext db, IReadOnlyCollection<Guid> roomIds, CancellationToken ct = default)
        {
            if (roomIds.Count == 0)
                return new Dictionary<Guid, int>();

            return await db.TextRoomParticipants
                .AsNoTracking()
                .Where(p => roomIds.Contains(p.TextRoomId))
                .GroupBy(p => p.TextRoomId)
                .Select(g => new { RoomId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.RoomId, x => x.Count, ct);
        }
    }
}
