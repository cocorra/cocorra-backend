using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Cocorra.BLL.Base;
using Cocorra.BLL.Services.AuditLogs;
using Cocorra.DAL.Data;
using Cocorra.DAL.DTOS.TextRoomDto;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;
using Microsoft.EntityFrameworkCore;

namespace Cocorra.BLL.Services.TextRooms
{
    public sealed class TextRoomAdminService : ResponseHandler, ITextRoomAdminService
    {
        private const int MaxOverviewRooms = 500;
        private const int MaxScheduleList = 200;
        private const int MaxCloseReasonLength = 200;
        private const string DefaultCloseReason = "Closed by staff";
        private const string ManagePermissionDeniedMessage = "Moderators are not permitted to manage text rooms.";

        private readonly AppDbContext _db;
        private readonly ITextRoomPresenceTracker _presence;
        private readonly ITextRoomBroadcaster _broadcaster;
        private readonly ITextRoomSettingsProvider _settingsProvider;
        private readonly IAuditLogService _auditLog;
        private readonly TimeProvider _time;

        public TextRoomAdminService(
            AppDbContext db,
            ITextRoomPresenceTracker presence,
            ITextRoomBroadcaster broadcaster,
            ITextRoomSettingsProvider settingsProvider,
            IAuditLogService auditLog,
            TimeProvider time)
        {
            _db = db;
            _presence = presence;
            _broadcaster = broadcaster;
            _settingsProvider = settingsProvider;
            _auditLog = auditLog;
            _time = time;
        }

        public async Task<Response<TextRoomOverviewDto>> GetOverviewAsync(StaffActor actor)
        {
            var now = _time.GetUtcNow().UtcDateTime;
            var canManage = await CanManageRoomsAsync(actor);

            var rooms = await _db.TextRooms
                .AsNoTracking()
                .Where(r => r.Status == TextRoomStatus.Active || r.Status == TextRoomStatus.Scheduled)
                .OrderBy(r => r.CreatedAt)
                .ThenBy(r => r.Id)
                .Take(MaxOverviewRooms)
                .ToListAsync();

            var ordered = TextRoomMapper.OrderByFamily(rooms);
            var overflowCounts = ordered
                .Where(r => r.ParentRoomId.HasValue)
                .GroupBy(r => r.ParentRoomId!.Value)
                .ToDictionary(g => g.Key, g => g.Count());

            var participantCounts = await TextRoomMapper.LoadParticipantCountsAsync(
                _db, ordered.Select(r => r.Id).ToList());

            var roomViews = ordered
                .Select(room =>
                {
                    var counts = new TextRoomCounts(
                        _presence.GetOnlineCount(room.Id),
                        participantCounts.GetValueOrDefault(room.Id));
                    var overflowCount = room.ParentRoomId is null
                        ? overflowCounts.GetValueOrDefault(room.Id)
                        : 0;
                    return TextRoomMapper.ToStaffViewDto(room, counts, now, overflowCount);
                })
                .ToList();

            return Success(new TextRoomOverviewDto
            {
                GlobalOnlineUsers = _presence.GetGlobalOnlineUserCount(),
                ActiveRoomCount = ordered.Count(r => r.Status == TextRoomStatus.Active),
                ServerTimeUtc = now,
                Rooms = roomViews,
                CallerCanManageRooms = canManage
            });
        }

        public async Task<Response<TextRoomStaffViewDto>> GetRoomAsync(Guid roomId)
        {
            var room = await _db.TextRooms
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == roomId);

            if (room is null)
                return NotFound<TextRoomStaffViewDto>("Text room not found.");

            var totalParticipants = await _db.TextRoomParticipants
                .AsNoTracking()
                .CountAsync(p => p.TextRoomId == roomId);

            var overflowCount = room.ParentRoomId is null
                ? await _db.TextRooms.AsNoTracking().CountAsync(r => r.ParentRoomId == roomId)
                : 0;

            var counts = new TextRoomCounts(_presence.GetOnlineCount(roomId), totalParticipants);
            return Success(TextRoomMapper.ToStaffViewDto(room, counts, _time.GetUtcNow().UtcDateTime, overflowCount));
        }

        public async Task<Response<TextRoomStaffViewDto>> CreateRoomAsync(TextRoomUpsertDto dto, StaffActor actor)
        {
            if (!await CanManageRoomsAsync(actor))
                return Forbidden<TextRoomStaffViewDto>(ManagePermissionDeniedMessage);

            var validationError = TextRoomSettingsValidator.ValidateRoomUpsert(dto);
            if (validationError is not null)
                return BadRequest<TextRoomStaffViewDto>(validationError);

            var now = _time.GetUtcNow().UtcDateTime;
            var opensAt = dto.OpensAt.HasValue ? UtcDateTime.Normalize(dto.OpensAt.Value) : now;
            if (dto.OpensAt.HasValue && opensAt < now.AddMinutes(-1))
                return BadRequest<TextRoomStaffViewDto>("OpensAt cannot be more than 1 minute in the past.");

            var room = BuildNewRoom(dto, actor.UserId, opensAt, now);
            _db.TextRooms.Add(room);
            await _db.SaveChangesAsync();

            await RecordRoomAuditAsync(
                AuditActions.TextRoomCreated,
                room,
                actor,
                new RoomAuditMeta(
                    FromState: null,
                    ToState: room.Status.ToString(),
                    Reason: null,
                    Details: new { roomId = room.Id, status = room.Status.ToString(), opensAt = room.OpensAt, closesAt = room.ClosesAt }));

            await _broadcaster.RoomListChangedAsync();
            return Created(TextRoomMapper.ToStaffViewDto(room, new TextRoomCounts(0, 0), now, overflowInstanceCount: 0));
        }

        public async Task<Response<TextRoomStaffViewDto>> UpdateRoomAsync(
            Guid roomId, TextRoomUpsertDto dto, StaffActor actor)
        {
            if (!await CanManageRoomsAsync(actor))
                return Forbidden<TextRoomStaffViewDto>(ManagePermissionDeniedMessage);

            var validationError = TextRoomSettingsValidator.ValidateRoomUpsert(dto);
            if (validationError is not null)
                return BadRequest<TextRoomStaffViewDto>(validationError);

            var target = await _db.TextRooms.FirstOrDefaultAsync(r => r.Id == roomId);
            if (target is null)
                return NotFound<TextRoomStaffViewDto>("Text room not found.");
            if (target.Status == TextRoomStatus.Closed)
                return Conflict<TextRoomStaffViewDto>("Closed text rooms cannot be updated.");

            var primaryId = target.ParentRoomId ?? target.Id;
            var family = await LoadOpenFamilyAsync(primaryId);

            var now = _time.GetUtcNow().UtcDateTime;
            ApplyRoomUpdateToFamily(family, dto, now);
            await _db.SaveChangesAsync();

            await RecordRoomAuditAsync(
                AuditActions.TextRoomUpdated,
                target,
                actor,
                new RoomAuditMeta(
                    FromState: null,
                    ToState: target.Status.ToString(),
                    Reason: null,
                    Details: new { roomId = target.Id, primaryRoomId = primaryId, familySize = family.Count, configVersion = target.ConfigVersion }));

            var participantCounts = await BroadcastFamilyConfigUpdatedAsync(family, now);
            var targetCounts = new TextRoomCounts(
                _presence.GetOnlineCount(target.Id),
                participantCounts.GetValueOrDefault(target.Id));
            var overflowCount = target.ParentRoomId is null ? family.Count(m => m.ParentRoomId == primaryId) : 0;

            return Success(TextRoomMapper.ToStaffViewDto(target, targetCounts, now, overflowCount));
        }

        public async Task<Response<string>> CloseRoomAsync(Guid roomId, string? reason, StaffActor actor)
        {
            if (!await CanManageRoomsAsync(actor))
                return Forbidden<string>(ManagePermissionDeniedMessage);

            var target = await _db.TextRooms.FirstOrDefaultAsync(r => r.Id == roomId);
            if (target is null)
                return NotFound<string>("Text room not found.");
            if (target.Status == TextRoomStatus.Closed)
                return Conflict<string>("Text room is already closed.");

            var closeReason = NormalizeCloseReason(reason);
            var previousStatus = target.Status.ToString();
            var primaryId = target.ParentRoomId ?? target.Id;
            var family = await LoadOpenFamilyAsync(primaryId);

            var now = _time.GetUtcNow().UtcDateTime;
            foreach (var member in family)
            {
                member.Status = TextRoomStatus.Closed;
                member.ClosedAt = now;
                member.CloseReason = closeReason;
                member.UpdatedAt = now;
            }

            await _db.SaveChangesAsync();

            await RecordRoomAuditAsync(
                AuditActions.TextRoomClosed,
                target,
                actor,
                new RoomAuditMeta(
                    FromState: previousStatus,
                    ToState: TextRoomStatus.Closed.ToString(),
                    Reason: closeReason,
                    Details: new { roomId = target.Id, primaryRoomId = primaryId, closedCount = family.Count }));

            foreach (var member in family)
                await _broadcaster.RoomClosedAsync(member.Id, closeReason);

            await _broadcaster.RoomListChangedAsync();
            return Success("Text room closed.");
        }

        public async Task<Response<List<TextRoomScheduleDto>>> GetSchedulesAsync()
        {
            var schedules = await _db.TextRoomSchedules
                .AsNoTracking()
                .OrderBy(s => s.Name)
                .Take(MaxScheduleList)
                .ToListAsync();

            return Success(schedules.Select(TextRoomMapper.ToScheduleDto).ToList());
        }

        public async Task<Response<TextRoomScheduleDto>> CreateScheduleAsync(
            TextRoomScheduleUpsertDto dto, StaffActor actor)
        {
            if (!await CanManageRoomsAsync(actor))
                return Forbidden<TextRoomScheduleDto>(ManagePermissionDeniedMessage);

            var validationError = TextRoomSettingsValidator.ValidateScheduleUpsert(dto);
            if (validationError is not null)
                return BadRequest<TextRoomScheduleDto>(validationError);

            var now = _time.GetUtcNow().UtcDateTime;
            var schedule = new TextRoomSchedule
            {
                Id = Guid.NewGuid(),
                NextTopicIndex = 0,
                NextSpawnAt = dto.FirstSpawnAt.HasValue ? UtcDateTime.Normalize(dto.FirstSpawnAt.Value) : now,
                CreatedById = actor.UserId,
                CreatedAt = now
            };
            ApplyScheduleUpsert(schedule, dto);

            _db.TextRoomSchedules.Add(schedule);
            await _db.SaveChangesAsync();

            await RecordScheduleAuditAsync(schedule, fromState: null, toState: "Created", actor);
            return Created(TextRoomMapper.ToScheduleDto(schedule));
        }

        public async Task<Response<TextRoomScheduleDto>> UpdateScheduleAsync(
            Guid scheduleId, TextRoomScheduleUpsertDto dto, StaffActor actor)
        {
            if (!await CanManageRoomsAsync(actor))
                return Forbidden<TextRoomScheduleDto>(ManagePermissionDeniedMessage);

            var validationError = TextRoomSettingsValidator.ValidateScheduleUpsert(dto);
            if (validationError is not null)
                return BadRequest<TextRoomScheduleDto>(validationError);

            var schedule = await _db.TextRoomSchedules.FirstOrDefaultAsync(s => s.Id == scheduleId);
            if (schedule is null)
                return NotFound<TextRoomScheduleDto>("Text room schedule not found.");

            ApplyScheduleUpsert(schedule, dto);
            if (dto.FirstSpawnAt.HasValue)
                schedule.NextSpawnAt = UtcDateTime.Normalize(dto.FirstSpawnAt.Value);
            schedule.UpdatedAt = _time.GetUtcNow().UtcDateTime;

            await _db.SaveChangesAsync();

            await RecordScheduleAuditAsync(schedule, fromState: null, toState: "Updated", actor);
            return Success(TextRoomMapper.ToScheduleDto(schedule));
        }

        public async Task<Response<string>> DeleteScheduleAsync(Guid scheduleId, StaffActor actor)
        {
            if (!await CanManageRoomsAsync(actor))
                return Forbidden<string>(ManagePermissionDeniedMessage);

            var schedule = await _db.TextRoomSchedules.FirstOrDefaultAsync(s => s.Id == scheduleId);
            if (schedule is null)
                return NotFound<string>("Text room schedule not found.");

            var fromState = schedule.IsEnabled ? "Enabled" : "Disabled";
            _db.TextRoomSchedules.Remove(schedule);
            await _db.SaveChangesAsync();

            await RecordScheduleAuditAsync(schedule, fromState, toState: "Deleted", actor);
            return Deleted<string>("Text room schedule deleted.");
        }

        public async Task<Response<TextRoomScheduleDto>> SetScheduleEnabledAsync(
            Guid scheduleId, bool isEnabled, StaffActor actor)
        {
            if (!await CanManageRoomsAsync(actor))
                return Forbidden<TextRoomScheduleDto>(ManagePermissionDeniedMessage);

            var schedule = await _db.TextRoomSchedules.FirstOrDefaultAsync(s => s.Id == scheduleId);
            if (schedule is null)
                return NotFound<TextRoomScheduleDto>("Text room schedule not found.");

            var fromState = schedule.IsEnabled ? "Enabled" : "Disabled";
            var toState = isEnabled ? "Enabled" : "Disabled";

            schedule.IsEnabled = isEnabled;
            schedule.UpdatedAt = _time.GetUtcNow().UtcDateTime;
            await _db.SaveChangesAsync();

            await RecordScheduleAuditAsync(schedule, fromState, toState, actor);
            return Success(TextRoomMapper.ToScheduleDto(schedule));
        }

        public async Task<Response<TextRoomGlobalSettingsDto>> GetGlobalSettingsAsync()
        {
            var settings = await _settingsProvider.GetAsync();
            return Success(TextRoomMapper.ToGlobalSettingsDto(settings));
        }

        public async Task<Response<TextRoomGlobalSettingsDto>> UpdateGlobalSettingsAsync(
            TextRoomGlobalSettingsDto dto, StaffActor actor)
        {
            if (!actor.IsAdmin)
                return Forbidden<TextRoomGlobalSettingsDto>("Only admins can update global text room settings.");

            var validationError = TextRoomSettingsValidator.ValidateGlobalSettings(dto);
            if (validationError is not null)
                return BadRequest<TextRoomGlobalSettingsDto>(validationError);

            var globalSettings = await _db.TextRoomGlobalSettings
                .FirstOrDefaultAsync(x => x.Id == TextRoomGlobalSettings.SingletonId);

            if (globalSettings is null)
            {
                globalSettings = new TextRoomGlobalSettings { Id = TextRoomGlobalSettings.SingletonId };
                _db.TextRoomGlobalSettings.Add(globalSettings);
            }

            TextRoomMapper.CopySettings(dto.Defaults, globalSettings.Defaults);
            globalSettings.ReportContextMessagesBefore = dto.ReportContextMessagesBefore;
            globalSettings.ReportContextMessagesAfter = dto.ReportContextMessagesAfter;
            globalSettings.DefaultMessageRetentionHours = dto.DefaultMessageRetentionHours;
            globalSettings.AllowModeratorsToManageRooms = dto.AllowModeratorsToManageRooms;
            globalSettings.UpdatedAt = _time.GetUtcNow().UtcDateTime;
            globalSettings.UpdatedById = actor.UserId;

            await _db.SaveChangesAsync();
            _settingsProvider.Invalidate();

            await _auditLog.RecordAsync(new AuditEntry(
                AuditActions.TextRoomSettingsChanged,
                actor.UserId,
                TargetUserId: null,
                TargetDisplay: "Global Text Room Settings",
                Details: new
                {
                    globalSettings.AllowModeratorsToManageRooms,
                    globalSettings.DefaultMessageRetentionHours,
                    globalSettings.ReportContextMessagesBefore,
                    globalSettings.ReportContextMessagesAfter
                })
            {
                Category = AuditCategories.TextRoom,
                EntityType = AuditEntityTypes.TextRoomSettings,
                EntityId = TextRoomGlobalSettings.SingletonId.ToString(),
                ActorRole = actor.Roles
            });

            return Success(TextRoomMapper.ToGlobalSettingsDto(globalSettings));
        }

        public IReadOnlyList<TextRoomPresetDto> GetPresets() => TextRoomPresets.All();

        private async Task<bool> CanManageRoomsAsync(StaffActor actor)
        {
            if (actor.IsAdmin)
                return true;

            var settings = await _settingsProvider.GetAsync();
            return settings.AllowModeratorsToManageRooms;
        }

        private Task<List<TextRoom>> LoadOpenFamilyAsync(Guid primaryId) =>
            _db.TextRooms
                .Where(r => (r.Id == primaryId || r.ParentRoomId == primaryId) && r.Status != TextRoomStatus.Closed)
                .OrderBy(r => r.OverflowIndex)
                .ToListAsync();

        private async Task<Dictionary<Guid, int>> BroadcastFamilyConfigUpdatedAsync(
            List<TextRoom> family, DateTime now)
        {
            var participantCounts = await TextRoomMapper.LoadParticipantCountsAsync(
                _db, family.Select(r => r.Id).ToList());

            foreach (var member in family)
            {
                var counts = new TextRoomCounts(
                    _presence.GetOnlineCount(member.Id),
                    participantCounts.GetValueOrDefault(member.Id));
                await _broadcaster.ConfigUpdatedAsync(TextRoomMapper.ToUserClientConfigDto(member, counts, now));
            }

            return participantCounts;
        }

        private Task RecordRoomAuditAsync(string action, TextRoom room, StaffActor actor, RoomAuditMeta meta) =>
            _auditLog.RecordAsync(new AuditEntry(
                action,
                actor.UserId,
                TargetUserId: null,
                TargetDisplay: room.Title,
                Details: meta.Details)
            {
                Category = AuditCategories.TextRoom,
                EntityType = AuditEntityTypes.TextRoom,
                EntityId = room.Id.ToString(),
                FromState = meta.FromState,
                ToState = meta.ToState,
                Reason = meta.Reason,
                ActorRole = actor.Roles
            });

        private Task RecordScheduleAuditAsync(
            TextRoomSchedule schedule, string? fromState, string toState, StaffActor actor) =>
            _auditLog.RecordAsync(new AuditEntry(
                AuditActions.TextRoomScheduleChanged,
                actor.UserId,
                TargetUserId: null,
                TargetDisplay: schedule.Name,
                Details: new { scheduleId = schedule.Id, schedule.SpawnEveryMinutes, schedule.IsEnabled })
            {
                Category = AuditCategories.TextRoom,
                EntityType = AuditEntityTypes.TextRoomSchedule,
                EntityId = schedule.Id.ToString(),
                FromState = fromState,
                ToState = toState,
                ActorRole = actor.Roles
            });

        private static TextRoom BuildNewRoom(TextRoomUpsertDto dto, Guid creatorId, DateTime opensAt, DateTime now)
        {
            var settings = TextRoomMapper.ToEntity(dto.Settings);
            return new TextRoom
            {
                Id = Guid.NewGuid(),
                Title = dto.Title.Trim(),
                Subject = TextRoomMapper.TrimToNull(dto.Subject),
                Description = TextRoomMapper.TrimToNull(dto.Description),
                DiscussionPrompts = TextRoomMapper.TrimPrompts(dto.DiscussionPrompts),
                TopicPool = (dto.TopicPool ?? new List<TextRoomTopicDto>()).Select(TextRoomMapper.ToEntity).ToList(),
                TopicRotationIndex = 0,
                Status = opensAt > now ? TextRoomStatus.Scheduled : TextRoomStatus.Active,
                Settings = settings,
                OpensAt = opensAt,
                ClosesAt = ComputeClosesAt(opensAt, settings),
                NextRotationAt = ComputeNextRotationAt(opensAt, settings),
                OverflowIndex = 1,
                CreatedById = creatorId,
                CreatedAt = now,
                ConfigVersion = 1
            };
        }

        private static void ApplyRoomUpdateToFamily(List<TextRoom> family, TextRoomUpsertDto dto, DateTime now)
        {
            var title = dto.Title.Trim();
            var subject = TextRoomMapper.TrimToNull(dto.Subject);
            var description = TextRoomMapper.TrimToNull(dto.Description);
            var prompts = TextRoomMapper.TrimPrompts(dto.DiscussionPrompts);
            var pool = (dto.TopicPool ?? new List<TextRoomTopicDto>()).Select(TextRoomMapper.ToEntity).ToList();

            foreach (var member in family)
            {
                var previousRotateMinutes = member.Settings.RotateTopicEveryMinutes;
                member.Title = title;
                member.Subject = subject;
                member.Description = description;
                member.DiscussionPrompts = prompts.ToList();
                member.TopicPool = pool.Select(TextRoomMapper.CloneTopic).ToList();
                TextRoomMapper.CopySettings(dto.Settings, member.Settings);
                member.ClosesAt = ComputeClosesAt(member.OpensAt, member.Settings);
                member.NextRotationAt = RescheduleRotation(member, previousRotateMinutes, now);
                member.ConfigVersion++;
                member.UpdatedAt = now;
            }
        }

        // Deriving it from OpensAt would put it in the past for any room open longer than one
        // interval, so every edit — even a title typo — would rotate the topic (and clear history).
        private static DateTime? RescheduleRotation(TextRoom room, int? previousRotateMinutes, DateTime now)
        {
            if (!room.Settings.IsAlwaysOn || room.Settings.RotateTopicEveryMinutes is not { } rotateMinutes)
                return null;

            return rotateMinutes == previousRotateMinutes && room.NextRotationAt.HasValue
                ? room.NextRotationAt
                : now.AddMinutes(rotateMinutes);
        }

        private static void ApplyScheduleUpsert(TextRoomSchedule schedule, TextRoomScheduleUpsertDto dto)
        {
            schedule.Name = dto.Name.Trim();
            schedule.IsEnabled = dto.IsEnabled;
            schedule.SpawnEveryMinutes = dto.SpawnEveryMinutes;
            schedule.WindowStartMinuteOfDay = dto.WindowStartMinuteOfDay;
            schedule.WindowEndMinuteOfDay = dto.WindowEndMinuteOfDay;
            schedule.TopicPool = dto.TopicPool.Select(TextRoomMapper.ToEntity).ToList();
            TextRoomMapper.CopySettings(dto.Settings, schedule.Settings);
            schedule.ClosePreviousOnSpawn = dto.ClosePreviousOnSpawn;
        }

        private static DateTime? ComputeClosesAt(DateTime opensAt, TextRoomSettings settings) =>
            settings.IsAlwaysOn ? null : opensAt.AddMinutes(settings.DurationMinutes!.Value);

        private static DateTime? ComputeNextRotationAt(DateTime opensAt, TextRoomSettings settings) =>
            settings.IsAlwaysOn && settings.RotateTopicEveryMinutes.HasValue
                ? opensAt.AddMinutes(settings.RotateTopicEveryMinutes.Value)
                : null;

        private static string NormalizeCloseReason(string? reason)
        {
            if (string.IsNullOrWhiteSpace(reason))
                return DefaultCloseReason;

            var trimmed = reason.Trim();
            return trimmed.Length > MaxCloseReasonLength ? trimmed[..MaxCloseReasonLength] : trimmed;
        }

        private readonly record struct RoomAuditMeta(
            string? FromState, string? ToState, string? Reason, object Details);
    }
}
