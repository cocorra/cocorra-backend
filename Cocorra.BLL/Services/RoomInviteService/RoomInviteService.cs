using System;
using System.Threading.Tasks;
using Cocorra.BLL.Base;
using Cocorra.BLL.Services.EventTracking;
using Cocorra.BLL.Services.RoomService;
using Cocorra.DAL.DTOS.RoomDto;
using Cocorra.DAL.DTOS.RoomInviteDto;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;
using Cocorra.DAL.Repository.RoomInviteRepository;
using Cocorra.DAL.Repository.RoomRepository;
using Microsoft.Extensions.Options;

namespace Cocorra.BLL.Services.RoomInviteService
{
    public class RoomInviteService : ResponseHandler, IRoomInviteService
    {
        private const string InviteNotFound = "Invite not found.";

        private readonly IRoomInviteRepository _inviteRepo;
        private readonly IRoomRepository _roomRepo;
        private readonly IRoomService _roomService;
        private readonly IEventTracker _eventTracker;
        private readonly InviteSettings _settings;

        public RoomInviteService(
            IRoomInviteRepository inviteRepo,
            IRoomRepository roomRepo,
            IRoomService roomService,
            IEventTracker eventTracker,
            IOptions<InviteSettings> settings)
        {
            _inviteRepo = inviteRepo;
            _roomRepo = roomRepo;
            _roomService = roomService;
            _eventTracker = eventTracker;
            _settings = settings.Value;
        }

        /// <summary>
        /// Only Active/Used/Revoked are stored; an Active invite is reported as Expired once its
        /// time is up or its room can no longer be joined. Shared with the stats query's rule.
        /// </summary>
        public static RoomInviteStatus GetEffectiveStatus(RoomInvite invite, Room? room, DateTime nowUtc)
        {
            if (invite.Status != RoomInviteStatus.Active)
                return invite.Status;

            if (invite.ExpiresAt <= nowUtc
                || room == null
                || room.Status is RoomStatus.Ended or RoomStatus.Cancelled)
                return RoomInviteStatus.Expired;

            return RoomInviteStatus.Active;
        }

        public async Task<Response<CreateRoomInviteResultDto>> CreateInviteAsync(Guid roomId, Guid callerId, CreateRoomInviteDto? dto)
        {
            var room = await _roomRepo.GetByIdAsync(roomId);
            if (room == null)
                return NotFound<CreateRoomInviteResultDto>("Room not found.");

            if (room.Status is RoomStatus.Ended or RoomStatus.Cancelled)
                return BadRequest<CreateRoomInviteResultDto>("This room is no longer available.");

            // A join to a private room only queues a request for the host, so an invite could
            // not deliver what it promises. Out of scope until that flow is designed.
            if (room.IsPrivate)
                return BadRequest<CreateRoomInviteResultDto>("Invites are not available for private rooms.");

            if (room.HostId != callerId)
            {
                var participant = await _roomRepo.GetParticipantAsync(roomId, callerId);
                if (room.Status != RoomStatus.Live || participant?.Status != ParticipantStatus.Active)
                    return Forbidden<CreateRoomInviteResultDto>("Only the host or an active participant can invite people to this room.");
            }

            var now = DateTime.UtcNow;
            var maxActive = _settings.MaxActivePerInviterPerRoom;
            if (await _inviteRepo.CountUsableAsync(callerId, roomId, now) >= maxActive)
                return BadRequest<CreateRoomInviteResultDto>(
                    $"You already have {maxActive} active invites for this room. Revoke one or wait for one to expire.");

            var hours = Math.Clamp(
                dto?.ExpiresInHours ?? _settings.DefaultLifetimeHours,
                1,
                Math.Max(1, _settings.MaxLifetimeHours));

            var invite = await _inviteRepo.AddWithUniqueCodeAsync(new RoomInvite
            {
                RoomId = roomId,
                InviterUserId = callerId,
                ExpiresAt = now.AddHours(hours),
                Status = RoomInviteStatus.Active,
                CreatedAt = now
            }, InviteCodeGenerator.Generate);

            if (_eventTracker.NewEventEmissionEnabled)
            {
                _eventTracker.Track(EventTypes.RoomInviteCreated, callerId, new
                {
                    roomId,
                    inviteId = invite.Id,
                    expiresInHours = hours,
                    isHost = room.HostId == callerId
                });
            }

            return Success(new CreateRoomInviteResultDto
            {
                InviteCode = invite.InviteCode,
                InviteUrl = BuildInviteUrl(invite.InviteCode),
                ExpiresAt = invite.ExpiresAt
            }, message: "Invite created.");
        }

        public async Task<Response<ResolveRoomInviteDto>> ResolveInviteAsync(string inviteCode)
        {
            if (!InviteCodeGenerator.IsWellFormed(inviteCode))
                return NotFound<ResolveRoomInviteDto>(InviteNotFound);

            var invite = await _inviteRepo.GetByCodeAsync(inviteCode);
            if (invite == null)
                return NotFound<ResolveRoomInviteDto>(InviteNotFound);

            var room = await _roomRepo.GetByIdAsync(invite.RoomId);
            var status = GetEffectiveStatus(invite, room, DateTime.UtcNow);

            // Anonymous, so no userId.
            if (_eventTracker.NewEventEmissionEnabled)
            {
                _eventTracker.Track(EventTypes.RoomInviteResolved, null, new
                {
                    roomId = invite.RoomId,
                    inviteId = invite.Id,
                    status = status.ToString()
                });
            }

            var dto = ToResolveDto(invite, status);
            return status == RoomInviteStatus.Active
                ? Success(dto)
                : Gone(dto, GoneMessage(status));
        }

        public async Task<Response<object>> AcceptInviteAsync(string inviteCode, Guid userId)
        {
            if (!InviteCodeGenerator.IsWellFormed(inviteCode))
                return NotFound<object>(InviteNotFound);

            var invite = await _inviteRepo.GetByCodeAsync(inviteCode);
            if (invite == null)
                return NotFound<object>(InviteNotFound);

            TrackInviteEvent(EventTypes.RoomInviteAcceptAttempted, userId, invite);

            var now = DateTime.UtcNow;
            var room = await _roomRepo.GetByIdAsync(invite.RoomId);
            var status = GetEffectiveStatus(invite, room, now);
            if (status != RoomInviteStatus.Active)
            {
                TrackAcceptFailed(userId, invite, status.ToString().ToLowerInvariant());
                return Gone<object>(ToResolveDto(invite, status), GoneMessage(status));
            }

            // Re-checked here rather than trusted from creation time.
            if (room!.IsPrivate)
            {
                TrackAcceptFailed(userId, invite, "private_room");
                return BadRequest<object>("Invites are not available for private rooms.");
            }

            if (invite.InviterUserId == userId)
            {
                TrackAcceptFailed(userId, invite, "own_invite");
                return BadRequest<object>("You cannot accept your own invite.");
            }

            // Already in the room: hand back a fresh token and leave the invite for someone else.
            var participant = await _roomRepo.GetParticipantAsync(room.Id, userId);
            if (participant?.Status == ParticipantStatus.Active)
            {
                var rejoin = await _roomService.JoinRoomAsync(room.Id, userId);
                if (!rejoin.Succeeded || rejoin.Data == null)
                {
                    TrackAcceptFailed(userId, invite, "join_failed");
                    return PassThroughFailure(rejoin);
                }

                TrackAccepted(userId, invite, inviteConsumed: false);
                return Success<object>(new AcceptRoomInviteResultDto
                {
                    RoomId = room.Id,
                    InviteConsumed = false,
                    AlreadyParticipant = true,
                    Join = rejoin.Data
                }, message: rejoin.Message ?? "Joined successfully.");
            }

            // Consume, then join, in one transaction. The conditional UPDATE in TryConsumeAsync
            // holds the invite's row lock until commit/rollback, so exactly one of two
            // concurrent accepts gets past it; the join's writes share this scoped context and
            // therefore the transaction. A join that fails rolls the consumption back, leaving
            // the invite usable.
            var outcome = await _inviteRepo.ExecuteInTransactionAsync<Response<object>>(async () =>
            {
                if (!await _inviteRepo.TryConsumeAsync(invite.Id, userId, now))
                    return (false, Conflict<object>("This invite has already been used."));

                var join = await _roomService.JoinRoomAsync(room.Id, userId);
                if (!join.Succeeded)
                    return (false, PassThroughFailure(join));

                // Public rooms always issue a token on success; anything else is not a real join.
                if (string.IsNullOrEmpty(join.Data?.LiveKitToken))
                    return (false, BadRequest<object>("Could not join the room with this invite."));

                return (true, Success<object>(new AcceptRoomInviteResultDto
                {
                    RoomId = room.Id,
                    InviteConsumed = true,
                    AlreadyParticipant = false,
                    Join = join.Data
                }, message: join.Message ?? "Joined successfully."));
            });

            if (outcome.Succeeded)
                TrackAccepted(userId, invite, inviteConsumed: true);
            else
                TrackAcceptFailed(userId, invite,
                    outcome.StatusCode == System.Net.HttpStatusCode.Conflict ? "concurrently_used" : "join_failed");

            return outcome;
        }

        public async Task<Response<ResolveRoomInviteDto>> RevokeInviteAsync(string inviteCode, Guid callerId, bool isAdmin)
        {
            if (!InviteCodeGenerator.IsWellFormed(inviteCode))
                return NotFound<ResolveRoomInviteDto>(InviteNotFound);

            var invite = await _inviteRepo.GetByCodeAsync(inviteCode);
            if (invite == null)
                return NotFound<ResolveRoomInviteDto>(InviteNotFound);

            var room = await _roomRepo.GetByIdAsync(invite.RoomId);

            string revokedAs;
            if (invite.InviterUserId == callerId) revokedAs = "inviter";
            else if (room != null && room.HostId == callerId) revokedAs = "host";
            else if (isAdmin) revokedAs = "admin";
            else return Forbidden<ResolveRoomInviteDto>("Only the inviter, the room host or an admin can revoke this invite.");

            var now = DateTime.UtcNow;
            var status = GetEffectiveStatus(invite, room, now);
            if (status != RoomInviteStatus.Active)
                return Gone(ToResolveDto(invite, status), GoneMessage(status));

            if (!await _inviteRepo.TryRevokeAsync(invite.Id, callerId, now))
            {
                // Consumed or revoked between our read and the update; report what won.
                var current = await _inviteRepo.GetByCodeAsync(inviteCode);
                var currentStatus = current == null
                    ? RoomInviteStatus.Revoked
                    : GetEffectiveStatus(current, room, now);
                return Gone(ToResolveDto(current ?? invite, currentStatus), GoneMessage(currentStatus));
            }

            if (_eventTracker.NewEventEmissionEnabled)
            {
                _eventTracker.Track(EventTypes.RoomInviteRevoked, callerId, new
                {
                    roomId = invite.RoomId,
                    inviteId = invite.Id,
                    revokedAs
                });
            }

            invite.Status = RoomInviteStatus.Revoked;
            return Success(ToResolveDto(invite, RoomInviteStatus.Revoked), message: "Invite revoked.");
        }

        public async Task<Response<RoomInviteStatsDto>> GetStatsAsync(DateTime? fromUtc, DateTime? toUtc, Guid? roomId, Guid? inviterUserId)
        {
            if (fromUtc.HasValue && toUtc.HasValue && fromUtc.Value > toUtc.Value)
                return BadRequest<RoomInviteStatsDto>("'from' must be earlier than 'to'.");

            var stats = await _inviteRepo.GetLifecycleStatsAsync(fromUtc, toUtc, roomId, inviterUserId, DateTime.UtcNow);

            stats.EventCountsAvailable = _eventTracker.NewEventEmissionEnabled;
            if (stats.EventCountsAvailable)
            {
                // inviterUserId is deliberately NOT applied to these three: resolve events are
                // anonymous and accept events belong to the accepting user, so no column on the
                // event row identifies the inviter. The window applies to when the event
                // happened, not when the invite was created.
                var counts = await _inviteRepo.CountEventsAsync(
                    new[]
                    {
                        EventTypes.RoomInviteResolved,
                        EventTypes.RoomInviteAcceptAttempted,
                        EventTypes.RoomInviteAcceptFailed
                    },
                    fromUtc, toUtc, roomId);

                stats.Resolved = counts.GetValueOrDefault(EventTypes.RoomInviteResolved);
                stats.AcceptAttempts = counts.GetValueOrDefault(EventTypes.RoomInviteAcceptAttempted);
                stats.FailedAcceptances = counts.GetValueOrDefault(EventTypes.RoomInviteAcceptFailed);
            }

            return Success(stats);
        }

        private string BuildInviteUrl(string code) =>
            $"{_settings.PublicBaseUrl.TrimEnd('/')}/invite/{code}";

        private static ResolveRoomInviteDto ToResolveDto(RoomInvite invite, RoomInviteStatus status) => new()
        {
            Valid = status == RoomInviteStatus.Active,
            InviteCode = invite.InviteCode,
            RoomId = status == RoomInviteStatus.Active ? invite.RoomId : null,
            ExpiresAt = invite.ExpiresAt,
            Status = status
        };

        private static string GoneMessage(RoomInviteStatus status) => status switch
        {
            RoomInviteStatus.Used => "This invite has already been used.",
            RoomInviteStatus.Revoked => "This invite has been revoked.",
            _ => "This invite has expired."
        };

        /// <summary>JoinRoomAsync's own status and message reach the client unchanged.</summary>
        private static Response<object> PassThroughFailure(Response<JoinRoomResultDto> join) => new()
        {
            StatusCode = join.StatusCode,
            Succeeded = false,
            Message = join.Message,
            Errors = join.Errors
        };

        private void TrackInviteEvent(string eventType, Guid userId, RoomInvite invite)
        {
            if (!_eventTracker.NewEventEmissionEnabled) return;
            _eventTracker.Track(eventType, userId, new { roomId = invite.RoomId, inviteId = invite.Id });
        }

        private void TrackAccepted(Guid userId, RoomInvite invite, bool inviteConsumed)
        {
            if (!_eventTracker.NewEventEmissionEnabled) return;
            _eventTracker.Track(EventTypes.RoomInviteAccepted, userId, new
            {
                roomId = invite.RoomId,
                inviteId = invite.Id,
                inviteConsumed
            });
        }

        private void TrackAcceptFailed(Guid userId, RoomInvite invite, string reason)
        {
            if (!_eventTracker.NewEventEmissionEnabled) return;
            _eventTracker.Track(EventTypes.RoomInviteAcceptFailed, userId, new
            {
                roomId = invite.RoomId,
                inviteId = invite.Id,
                reason
            });
        }
    }
}
