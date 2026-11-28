using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cocorra.BLL.Base;
using Cocorra.BLL.Services.ChatService;
using Cocorra.BLL.Services.ContentModeration;
using Cocorra.DAL.AppMetaData;
using Cocorra.DAL.Data;
using Cocorra.DAL.DTOS.ChatDto;
using Cocorra.DAL.DTOS.TextRoomDto;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Cocorra.BLL.Services.TextRooms
{
    public sealed class TextRoomService : ResponseHandler, ITextRoomService
    {
        private const int MaxActiveRoomListSize = 500;

        private readonly AppDbContext _db;
        private readonly ITextRoomPresenceTracker _presence;
        private readonly ITextRoomBroadcaster _broadcaster;
        private readonly IWordFilter _wordFilter;
        private readonly ITextRoomReportService _reportService;
        private readonly IChatService _chatService;
        private readonly IMemoryCache _cache;
        private readonly TimeProvider _time;
        private readonly TextRoomFamilyLockProvider _familyLocks;
        private readonly ILogger<TextRoomService> _logger;

        public TextRoomService(
            AppDbContext db,
            ITextRoomPresenceTracker presence,
            ITextRoomBroadcaster broadcaster,
            IWordFilter wordFilter,
            ITextRoomReportService reportService,
            IChatService chatService,
            IMemoryCache cache,
            TimeProvider time,
            TextRoomFamilyLockProvider familyLocks,
            ILogger<TextRoomService> logger)
        {
            _db = db;
            _presence = presence;
            _broadcaster = broadcaster;
            _wordFilter = wordFilter;
            _reportService = reportService;
            _chatService = chatService;
            _cache = cache;
            _time = time;
            _familyLocks = familyLocks;
            _logger = logger;
        }

        public async Task<Response<List<TextRoomListItemDto>>> GetActiveRoomsAsync(Guid userId, bool viewerIsStaff)
        {
            var rooms = await _db.TextRooms
                .AsNoTracking()
                .Where(r => r.Status == TextRoomStatus.Active)
                .OrderBy(r => r.CreatedAt)
                .ThenBy(r => r.Id)
                .Take(MaxActiveRoomListSize)
                .ToListAsync();

            var ordered = TextRoomMapper.OrderByFamily(rooms);
            var participantCounts = await TextRoomMapper.LoadParticipantCountsAsync(
                _db, ordered.Select(r => r.Id).ToList());

            var roomSummaries = ordered
                .Select(room =>
                {
                    var counts = new TextRoomCounts(
                        _presence.GetOnlineCount(room.Id),
                        participantCounts.GetValueOrDefault(room.Id));
                    return viewerIsStaff
                        ? TextRoomMapper.ToStaffListItemDto(room, counts)
                        : TextRoomMapper.ToUserListItemDto(room, counts);
                })
                .ToList();

            return Success(roomSummaries);
        }

        public async Task<Response<TextRoomClientConfigDto>> GetRoomConfigAsync(
            Guid roomId, Guid userId, bool viewerIsStaff)
        {
            var room = await _db.TextRooms
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == roomId);

            if (room is null)
                return NotFound<TextRoomClientConfigDto>("Text room not found.");

            var totalParticipants = viewerIsStaff || room.Settings.ShowOnlineCountToUsers
                ? await _db.TextRoomParticipants.AsNoTracking().CountAsync(p => p.TextRoomId == room.Id)
                : 0;

            var counts = new TextRoomCounts(_presence.GetOnlineCount(room.Id), totalParticipants);
            var now = _time.GetUtcNow().UtcDateTime;
            var config = viewerIsStaff
                ? TextRoomMapper.ToStaffClientConfigDto(room, counts, now)
                : TextRoomMapper.ToUserClientConfigDto(room, counts, now);

            return Success(config);
        }

        public async Task<TextRoomJoinResultDto> JoinAsync(
            Guid roomId, Guid userId, bool viewerIsStaff, string connectionId)
        {
            var heldGate = _familyLocks.ForFamily(roomId);
            await heldGate.WaitAsync();
            try
            {
                var room = await _db.TextRooms
                    .AsNoTracking()
                    .FirstOrDefaultAsync(r => r.Id == roomId);

                if (room is { ParentRoomId: { } parentId } && parentId != roomId)
                {
                    // Hand off to the primary family gate without holding two gates at once,
                    // preventing deadlocks when callers target primary and overflow simultaneously.
                    heldGate.Release();
                    heldGate = null;

                    var primaryGate = _familyLocks.ForFamily(parentId);
                    await primaryGate.WaitAsync();
                    heldGate = primaryGate;

                    room = await _db.TextRooms
                        .AsNoTracking()
                        .FirstOrDefaultAsync(r => r.Id == roomId);
                }

                var join = await JoinUnderFamilyLockAsync(room, userId, viewerIsStaff, connectionId);
                heldGate.Release();
                heldGate = null;

                if (join.SpawnedOverflow)
                    await _broadcaster.RoomListChangedAsync();
                if (join.UserWentOnline && join.Outcome.RoomId is { } joinedRoomId)
                    await BroadcastPresenceAsync(joinedRoomId);

                return join.Outcome;
            }
            finally
            {
                heldGate?.Release();
            }
        }

        public async Task LeaveAsync(Guid roomId, Guid userId, string connectionId)
        {
            var change = _presence.RemoveConnectionFromRoom(roomId, connectionId);
            await TouchLastSeenAsync(roomId, userId);

            if (change is { UserWentOffline: true })
                await BroadcastPresenceAsync(roomId);
        }

        public async Task DisconnectAsync(string connectionId)
        {
            foreach (var change in _presence.RemoveConnection(connectionId).Where(c => c.UserWentOffline))
            {
                try
                {
                    await TouchLastSeenAsync(change.RoomId, change.UserId);
                    await BroadcastPresenceAsync(change.RoomId);
                }
                catch (Exception ex)
                {
                    // One room failing must not leave the user shown online in their other rooms.
                    _logger.LogWarning(ex,
                        "Text room disconnect cleanup failed for user {UserId} in room {RoomId}.",
                        change.UserId, change.RoomId);
                }
            }
        }

        private async Task TouchLastSeenAsync(Guid roomId, Guid userId)
        {
            var now = _time.GetUtcNow().UtcDateTime;
            await _db.TextRoomParticipants
                .Where(p => p.TextRoomId == roomId && p.UserId == userId)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.LastSeenAt, now));
        }

        private Task BroadcastPresenceAsync(Guid roomId) =>
            TextRoomPresencePublisher.PublishAsync(_db, _presence, _broadcaster, roomId);

        public async Task<Response<List<TextRoomMessageDto>>> GetMessagesAsync(
            Guid roomId, Guid userId, long? beforeId, int limit)
        {
            var room = await _db.TextRooms
                .AsNoTracking()
                .Where(r => r.Id == roomId)
                .Select(r => new { r.Id, r.HistoryClearedAt })
                .FirstOrDefaultAsync();

            if (room is null)
                return NotFound<List<TextRoomMessageDto>>("Text room not found.");

            var participant = await _db.TextRoomParticipants
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.TextRoomId == roomId && p.UserId == userId);

            if (participant is null || participant.IsKicked)
                return Forbidden<List<TextRoomMessageDto>>("You must be a member of this text room to view messages.");

            var clampedLimit = Math.Clamp(limit, 1, TextRoomLimits.HistoryPageMax);
            var messages = await LoadVisibleMessagesAsync(roomId, room.HistoryClearedAt, beforeId, clampedLimit);
            return Success(messages);
        }

        public async Task<SendTextRoomMessageResultDto> SendMessageAsync(
            Guid roomId, Guid userId, string? content, bool senderIsStaff)
        {
            var now = _time.GetUtcNow().UtcDateTime;
            var (sendContext, trimmedContent, validationFailure) =
                await ValidateSendEligibilityAsync(roomId, userId, content, senderIsStaff, now);

            if (validationFailure is not null)
                return validationFailure;

            var filterResult = await _wordFilter.EvaluateAsync(trimmedContent!);
            if (filterResult.IsBlocked)
                return SendFailure(SendTextRoomMessageStatus.Blocked, "Your message contains prohibited content.");

            return await PersistAndBroadcastMessageAsync(sendContext!.Value, filterResult);
        }

        public async Task<Response<MessageDto>> SendPrivateMessageAsync(
            Guid roomId, Guid senderId, Guid targetUserId, string? content)
        {
            var room = await _db.TextRooms
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == roomId);

            if (room is null)
                return NotFound<MessageDto>("Text room not found.");
            if (!room.Settings.AllowPrivateChatFromRoom)
                return Forbidden<MessageDto>("Private chat is disabled for this text room.");
            if (room.Status != TextRoomStatus.Active)
                return Forbidden<MessageDto>("This text room is not active.");

            var expectedParticipants = senderId == targetUserId ? 1 : 2;
            var activeParticipantCount = await _db.TextRoomParticipants
                .AsNoTracking()
                .CountAsync(p => p.TextRoomId == roomId
                    && !p.IsKicked
                    && (p.UserId == senderId || p.UserId == targetUserId));

            if (activeParticipantCount < expectedParticipants)
                return Forbidden<MessageDto>("Both users must be active participants of this text room.");

            if (senderId == targetUserId)
                return BadRequest<MessageDto>("You cannot send a private message to yourself.");

            var trimmed = (content ?? string.Empty).Trim();
            if (trimmed.Length == 0)
                return BadRequest<MessageDto>("Message cannot be empty.");

            return await _chatService.SaveMessageAsync(
                senderId, targetUserId, trimmed, new MessageOrigin(TextRoomId: roomId));
        }

        private static TextRoomJoinStatus? CheckRoomJoinable(TextRoom? room)
        {
            if (room is null)
                return TextRoomJoinStatus.NotFound;
            if (room.Status == TextRoomStatus.Closed)
                return TextRoomJoinStatus.Closed;
            if (room.Status == TextRoomStatus.Scheduled)
                return TextRoomJoinStatus.NotOpenYet;

            return null;
        }

        private async Task<JoinAttempt> JoinUnderFamilyLockAsync(
            TextRoom? room, Guid userId, bool viewerIsStaff, string connectionId)
        {
            var statusFailure = CheckRoomJoinable(room);
            if (statusFailure.HasValue)
                return JoinAttempt.Failed(statusFailure.Value);

            var existing = await _db.TextRoomParticipants
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.TextRoomId == room!.Id && p.UserId == userId);

            if (existing is { IsKicked: true })
                return JoinAttempt.Failed(TextRoomJoinStatus.Kicked);

            var now = _time.GetUtcNow().UtcDateTime;
            var joinContext = new JoinContext(room!, userId, viewerIsStaff, now);
            var placement = await ResolvePlacementUnderLockAsync(joinContext);
            if (placement.FailureStatus.HasValue)
                return JoinAttempt.Failed(placement.FailureStatus.Value);

            var targetRoom = placement.TargetRoom!;
            var participant = await UpsertParticipantAsync(targetRoom.Id, userId, now);
            if (participant.IsKicked)
                return JoinAttempt.Failed(TextRoomJoinStatus.Kicked);

            var wentOnline = _presence.AddConnection(targetRoom.Id, userId, connectionId);
            var joinResult = await BuildJoinedResultAsync(placement, participant, joinContext);
            return new JoinAttempt(joinResult, placement.SpawnedOverflow, wentOnline);
        }

        private async Task<PlacementDecision> ResolvePlacementUnderLockAsync(JoinContext context)
        {
            var requestedRoom = context.RequestedRoom;
            var trigger = CapacityTrigger(requestedRoom.Settings);
            if (context.ViewerIsStaff
                || _presence.IsOnline(requestedRoom.Id, context.UserId)
                || !trigger.HasValue
                || _presence.GetOnlineCount(requestedRoom.Id) < trigger.Value)
            {
                return PlacementDecision.ForRoom(requestedRoom, redirected: false, spawnedOverflow: false);
            }

            if (!requestedRoom.Settings.AutoOverflowEnabled)
                return PlacementDecision.Failed(TextRoomJoinStatus.Full);

            var primaryId = requestedRoom.ParentRoomId ?? requestedRoom.Id;
            var family = await _db.TextRooms
                .AsNoTracking()
                .Where(r => (r.Id == primaryId || r.ParentRoomId == primaryId) && r.Status == TextRoomStatus.Active)
                .OrderBy(r => r.OverflowIndex)
                .ToListAsync();

            var available = await FindAvailableFamilyRoomAsync(family, context.UserId);
            if (available is not null)
            {
                return PlacementDecision.ForRoom(
                    available, redirected: available.Id != requestedRoom.Id, spawnedOverflow: false);
            }

            return await TrySpawnOverflowRoomAsync(family, context, primaryId);
        }

        private async Task<TextRoom?> FindAvailableFamilyRoomAsync(List<TextRoom> family, Guid userId)
        {
            var familyIds = family.Select(r => r.Id).ToList();
            var kickedRoomIds = await _db.TextRoomParticipants
                .AsNoTracking()
                .Where(p => familyIds.Contains(p.TextRoomId) && p.UserId == userId && p.IsKicked)
                .Select(p => p.TextRoomId)
                .ToHashSetAsync();

            foreach (var candidate in family)
            {
                if (kickedRoomIds.Contains(candidate.Id))
                    continue;

                var candidateTrigger = CapacityTrigger(candidate.Settings);
                if (!candidateTrigger.HasValue
                    || _presence.IsOnline(candidate.Id, userId)
                    || _presence.GetOnlineCount(candidate.Id) < candidateTrigger.Value)
                {
                    return candidate;
                }
            }

            return null;
        }

        private async Task<PlacementDecision> TrySpawnOverflowRoomAsync(
            List<TextRoom> family, JoinContext context, Guid primaryId)
        {
            var maxOverflows = Math.Min(
                context.RequestedRoom.Settings.MaxOverflowInstances ?? TextRoomLimits.HardMaxOverflowInstances,
                TextRoomLimits.HardMaxOverflowInstances);

            if (family.Count(r => r.ParentRoomId == primaryId) >= maxOverflows)
                return PlacementDecision.Failed(TextRoomJoinStatus.Full);

            var template = family.FirstOrDefault(r => r.Id == primaryId) ?? context.RequestedRoom;
            var maxFamilyIndex = await _db.TextRooms
                .AsNoTracking()
                .Where(r => r.Id == primaryId || r.ParentRoomId == primaryId)
                .MaxAsync(r => r.OverflowIndex);

            var spawned = CreateOverflowRoom(template, primaryId, maxFamilyIndex + 1, context.Now);
            _db.TextRooms.Add(spawned);
            await _db.SaveChangesAsync();
            _db.Entry(spawned).State = EntityState.Detached;

            return PlacementDecision.ForRoom(spawned, redirected: true, spawnedOverflow: true);
        }

        private static TextRoom CreateOverflowRoom(
            TextRoom template, Guid primaryId, int overflowIndex, DateTime now) => new()
        {
            Id = Guid.NewGuid(),
            Title = template.Title,
            Subject = template.Subject,
            Description = template.Description,
            DiscussionPrompts = template.DiscussionPrompts.ToList(),
            TopicPool = template.TopicPool.Select(TextRoomMapper.CloneTopic).ToList(),
            TopicRotationIndex = template.TopicRotationIndex,
            Status = TextRoomStatus.Active,
            Settings = template.Settings.Clone(),
            OpensAt = template.OpensAt,
            ClosesAt = template.ClosesAt,
            NextRotationAt = template.NextRotationAt,
            ParentRoomId = primaryId,
            OverflowIndex = overflowIndex,
            ScheduleId = template.ScheduleId,
            CreatedById = template.CreatedById,
            CreatedAt = now,
            ConfigVersion = template.ConfigVersion
        };

        private async Task<TextRoomParticipant> UpsertParticipantAsync(Guid roomId, Guid userId, DateTime now)
        {
            var participant = await _db.TextRoomParticipants
                .FirstOrDefaultAsync(p => p.TextRoomId == roomId && p.UserId == userId);

            if (participant is not null)
                return await TouchParticipantIfNotKickedAsync(participant, now);

            var created = new TextRoomParticipant
            {
                TextRoomId = roomId,
                UserId = userId,
                FirstJoinedAt = now,
                LastSeenAt = now
            };
            _db.TextRoomParticipants.Add(created);

            try
            {
                await _db.SaveChangesAsync();
                return created;
            }
            catch (DbUpdateException)
            {
                // Concurrent join for the same (room, user) inserted first; reload and update LastSeenAt.
                _db.Entry(created).State = EntityState.Detached;
                var existing = await _db.TextRoomParticipants
                    .FirstAsync(p => p.TextRoomId == roomId && p.UserId == userId);
                return await TouchParticipantIfNotKickedAsync(existing, now);
            }
        }

        private async Task<TextRoomParticipant> TouchParticipantIfNotKickedAsync(
            TextRoomParticipant participant, DateTime now)
        {
            if (!participant.IsKicked)
            {
                participant.LastSeenAt = now;
                await _db.SaveChangesAsync();
            }

            return participant;
        }

        private async Task<TextRoomJoinResultDto> BuildJoinedResultAsync(
            PlacementDecision placement, TextRoomParticipant participant, JoinContext context)
        {
            var room = placement.TargetRoom!;
            var totalParticipants = context.ViewerIsStaff || room.Settings.ShowOnlineCountToUsers
                ? await _db.TextRoomParticipants.AsNoTracking().CountAsync(p => p.TextRoomId == room.Id)
                : 0;

            var counts = new TextRoomCounts(_presence.GetOnlineCount(room.Id), totalParticipants);
            var config = context.ViewerIsStaff
                ? TextRoomMapper.ToStaffClientConfigDto(room, counts, context.Now)
                : TextRoomMapper.ToUserClientConfigDto(room, counts, context.Now);

            var recentMessages = await LoadVisibleMessagesAsync(
                room.Id, room.HistoryClearedAt, beforeId: null, limit: TextRoomLimits.HistoryPageDefault);

            DateTime? mutedUntil = participant.MutedUntil.HasValue && participant.MutedUntil.Value > context.Now
                ? participant.MutedUntil.Value
                : null;

            return new TextRoomJoinResultDto
            {
                Status = placement.Redirected ? TextRoomJoinStatus.Redirected : TextRoomJoinStatus.Joined,
                RoomId = room.Id,
                Config = config,
                RecentMessages = recentMessages,
                MutedUntil = mutedUntil
            };
        }

        private async Task<(SendContext? Context, string? TrimmedContent, SendTextRoomMessageResultDto? Failure)>
            ValidateSendEligibilityAsync(
                Guid roomId, Guid userId, string? content, bool senderIsStaff, DateTime now)
        {
            var room = await _db.TextRooms
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == roomId);

            if (room is null)
                return (null, null, SendFailure(SendTextRoomMessageStatus.NotFound, "Text room not found."));
            if (room.Status != TextRoomStatus.Active)
                return (null, null, SendFailure(SendTextRoomMessageStatus.Closed, "This text room is closed."));

            var participant = await _db.TextRoomParticipants
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.TextRoomId == roomId && p.UserId == userId);

            if (!_presence.IsOnline(roomId, userId) || participant is null || participant.IsKicked)
                return (null, null, SendFailure(SendTextRoomMessageStatus.NotMember, "You must be an active member of this room."));

            if (participant.MutedUntil.HasValue && participant.MutedUntil.Value > now)
            {
                return (null, null, new SendTextRoomMessageResultDto
                {
                    Status = SendTextRoomMessageStatus.Muted,
                    MutedUntil = participant.MutedUntil.Value,
                    Error = "You are currently muted in this text room."
                });
            }

            var trimmed = (content ?? string.Empty).Trim();
            if (trimmed.Length == 0)
                return (null, null, SendFailure(SendTextRoomMessageStatus.Empty, "Message cannot be empty."));

            if (trimmed.Length > room.Settings.MaxMessageLength)
            {
                return (null, null, SendFailure(
                    SendTextRoomMessageStatus.TooLong,
                    $"Message cannot exceed {room.Settings.MaxMessageLength} characters."));
            }

            var sendContext = new SendContext(room, userId, senderIsStaff, now);
            var slowModeFailure = CheckSlowMode(sendContext);
            if (slowModeFailure is not null)
                return (null, null, slowModeFailure);

            return (sendContext, trimmed, null);
        }

        private SendTextRoomMessageResultDto? CheckSlowMode(SendContext context)
        {
            if (context.SenderIsStaff || context.Room.Settings.SlowModeSeconds <= 0)
                return null;

            var slowKey = SlowModeKey(context.Room.Id, context.UserId);
            if (!_cache.TryGetValue(slowKey, out DateTime slowExpiresAtUtc) || slowExpiresAtUtc <= context.Now)
                return null;

            var retryAfter = Math.Max(1, (int)Math.Ceiling((slowExpiresAtUtc - context.Now).TotalSeconds));
            return new SendTextRoomMessageResultDto
            {
                Status = SendTextRoomMessageStatus.SlowMode,
                RetryAfterSeconds = retryAfter,
                Error = $"Slow mode is active. Try again in {retryAfter} seconds."
            };
        }

        private async Task<SendTextRoomMessageResultDto> PersistAndBroadcastMessageAsync(
            SendContext context, WordFilterResult filterResult)
        {
            var message = new TextRoomMessage
            {
                TextRoomId = context.Room.Id,
                SenderId = context.UserId,
                Content = filterResult.Content,
                CreatedAt = context.Now,
                IsMasked = filterResult.WasMasked
            };
            _db.TextRoomMessages.Add(message);
            await _db.SaveChangesAsync();

            RecordSlowModeCooldown(context);

            if (filterResult.RequiresAutoReport && filterResult.TopAutoReportHit is not null)
                await TryCreateAutoReportAsync(message, filterResult.TopAutoReportHit);

            var sender = await _db.Users
                .AsNoTracking()
                .Where(u => u.Id == context.UserId)
                .Select(u => new { u.FirstName, u.LastName, u.ProfilePicturePath })
                .FirstOrDefaultAsync();

            var senderName = TextRoomMapper.FormatDisplayName(sender?.FirstName, sender?.LastName);
            var dto = TextRoomMapper.ToMessageDto(message, senderName, sender?.ProfilePicturePath);

            await _broadcaster.MessageReceivedAsync(dto);

            return new SendTextRoomMessageResultDto
            {
                Status = filterResult.WasMasked ? SendTextRoomMessageStatus.Masked : SendTextRoomMessageStatus.Sent,
                Message = dto
            };
        }

        private void RecordSlowModeCooldown(SendContext context)
        {
            if (context.SenderIsStaff || context.Room.Settings.SlowModeSeconds <= 0)
                return;

            var ttl = TimeSpan.FromSeconds(context.Room.Settings.SlowModeSeconds);
            // The value is the expiry instant: the check compares it with the injected clock, because the
            // cache's own expiration runs on wall-clock time and would disagree with TimeProvider.
            _cache.Set(SlowModeKey(context.Room.Id, context.UserId), context.Now.Add(ttl), new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = ttl
            });
        }

        private async Task TryCreateAutoReportAsync(TextRoomMessage message, WordFilterHit hit)
        {
            try
            {
                await _reportService.CreateAutoReportAsync(message, hit);
            }
            catch (Exception ex)
            {
                // Auto-report failure must never reject an already-persisted user message.
                _logger.LogError(
                    ex,
                    "Auto-report creation failed for text room message {MessageId} in room {RoomId}.",
                    message.Id,
                    message.TextRoomId);
            }
        }

        private async Task<List<TextRoomMessageDto>> LoadVisibleMessagesAsync(
            Guid roomId, DateTime? historyClearedAt, long? beforeId, int limit)
        {
            var query = _db.TextRoomMessages
                .AsNoTracking()
                .Where(m => m.TextRoomId == roomId && !m.IsDeleted);

            if (historyClearedAt.HasValue)
            {
                var clearedAt = historyClearedAt.Value;
                query = query.Where(m => m.CreatedAt > clearedAt);
            }

            if (beforeId.HasValue)
            {
                var cursor = beforeId.Value;
                query = query.Where(m => m.Id < cursor);
            }

            var messageRows = await query
                .OrderByDescending(m => m.Id)
                .Take(limit)
                .Select(m => new
                {
                    m.Id,
                    m.TextRoomId,
                    m.SenderId,
                    FirstName = m.Sender != null ? m.Sender.FirstName : null,
                    LastName = m.Sender != null ? m.Sender.LastName : null,
                    ProfilePicturePath = m.Sender != null ? m.Sender.ProfilePicturePath : null,
                    m.Content,
                    m.IsMasked,
                    m.CreatedAt
                })
                .ToListAsync();

            messageRows.Reverse();

            return messageRows
                .Select(row => new TextRoomMessageDto
                {
                    Id = row.Id,
                    RoomId = row.TextRoomId,
                    SenderId = row.SenderId,
                    SenderName = TextRoomMapper.FormatDisplayName(row.FirstName, row.LastName),
                    SenderProfilePicturePath = row.ProfilePicturePath,
                    Content = row.Content,
                    IsMasked = row.IsMasked,
                    CreatedAt = row.CreatedAt
                })
                .ToList();
        }

        private static int? CapacityTrigger(TextRoomSettings settings) =>
            settings.OverflowTriggerCount ?? settings.MaxParticipants;

        private static string SlowModeKey(Guid roomId, Guid userId) => $"textroom:slow:{roomId}:{userId}";

        private static TextRoomJoinResultDto JoinOutcome(TextRoomJoinStatus status) => new() { Status = status };

        private static SendTextRoomMessageResultDto SendFailure(SendTextRoomMessageStatus status, string error) => new()
        {
            Status = status,
            Error = error
        };

        private readonly record struct JoinAttempt(
            TextRoomJoinResultDto Outcome, bool SpawnedOverflow, bool UserWentOnline)
        {
            public static JoinAttempt Failed(TextRoomJoinStatus status) => new(JoinOutcome(status), false, false);
        }

        private readonly record struct JoinContext(
            TextRoom RequestedRoom, Guid UserId, bool ViewerIsStaff, DateTime Now);

        private readonly record struct SendContext(
            TextRoom Room, Guid UserId, bool SenderIsStaff, DateTime Now);

        private readonly record struct PlacementDecision(
            TextRoom? TargetRoom,
            bool Redirected,
            bool SpawnedOverflow,
            TextRoomJoinStatus? FailureStatus)
        {
            public static PlacementDecision ForRoom(TextRoom room, bool redirected, bool spawnedOverflow) =>
                new(room, redirected, spawnedOverflow, null);

            public static PlacementDecision Failed(TextRoomJoinStatus status) =>
                new(null, false, false, status);
        }
    }
}
