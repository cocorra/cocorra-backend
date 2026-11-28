using System;
using System.Linq;
using System.Threading.Tasks;
using Cocorra.BLL.Base;
using Cocorra.BLL.Services.AuditLogs;
using Cocorra.DAL.AppMetaData;
using Cocorra.DAL.Data;
using Cocorra.DAL.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Cocorra.BLL.Services.TextRooms
{
    public sealed class TextRoomModerationService : ResponseHandler, ITextRoomModerationService
    {
        private readonly AppDbContext _db;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly ITextRoomPresenceTracker _presence;
        private readonly ITextRoomBroadcaster _broadcaster;
        private readonly IAuditLogService _auditLog;
        private readonly TimeProvider _time;

        public TextRoomModerationService(
            AppDbContext db,
            UserManager<ApplicationUser> userManager,
            ITextRoomPresenceTracker presence,
            ITextRoomBroadcaster broadcaster,
            IAuditLogService auditLog,
            TimeProvider time)
        {
            _db = db;
            _userManager = userManager;
            _presence = presence;
            _broadcaster = broadcaster;
            _auditLog = auditLog;
            _time = time;
        }

        public async Task<Response<string>> DeleteMessageAsync(long messageId, string? reason, StaffActor actor)
        {
            if (TextRoomSettingsValidator.NormalizeSanctionReason(reason) is not { } trimmedReason)
                return BadRequest<string>(TextRoomSettingsValidator.SanctionReasonError);

            var message = await _db.TextRoomMessages.FirstOrDefaultAsync(m => m.Id == messageId);
            if (message is null)
                return NotFound<string>("Message not found.");

            var (targetUser, policyFailure) = await ValidateTargetUserAsync(message.SenderId, actor);
            if (policyFailure is not null)
                return policyFailure;

            if (message.IsDeleted)
                return Conflict<string>("Message is already deleted.");

            message.IsDeleted = true;
            message.DeletedAt = _time.GetUtcNow().UtcDateTime;
            message.DeletedById = actor.UserId;
            await _db.SaveChangesAsync();

            await RecordSanctionAuditAsync(new SanctionAuditRecord(
                AuditActions.TextRoomMessageDeleted,
                AuditEntityTypes.TextRoomMessage,
                message.Id.ToString(),
                "Deleted",
                trimmedReason,
                targetUser!,
                actor,
                new { roomId = message.TextRoomId, messageId = message.Id }));

            await _broadcaster.MessageDeletedAsync(message.TextRoomId, message.Id);
            return Success("Message deleted.");
        }

        public async Task<Response<string>> MuteUserAsync(
            Guid roomId, Guid userId, int minutes, string? reason, StaffActor actor)
        {
            if (TextRoomSettingsValidator.NormalizeSanctionReason(reason) is not { } trimmedReason)
                return BadRequest<string>(TextRoomSettingsValidator.SanctionReasonError);

            if (minutes < TextRoomLimits.MuteMinutesMin || minutes > TextRoomLimits.MuteMinutesMax)
            {
                return BadRequest<string>(
                    $"Minutes must be between {TextRoomLimits.MuteMinutesMin} and {TextRoomLimits.MuteMinutesMax}.");
            }

            var participant = await _db.TextRoomParticipants
                .FirstOrDefaultAsync(p => p.TextRoomId == roomId && p.UserId == userId);
            if (participant is null)
                return NotFound<string>("Participant not found in this text room.");

            var (targetUser, policyFailure) = await ValidateTargetUserAsync(userId, actor);
            if (policyFailure is not null)
                return policyFailure;

            var mutedUntil = _time.GetUtcNow().UtcDateTime.AddMinutes(minutes);
            participant.MutedUntil = mutedUntil;
            participant.MutedById = actor.UserId;
            await _db.SaveChangesAsync();

            await RecordSanctionAuditAsync(new SanctionAuditRecord(
                AuditActions.TextRoomUserMuted,
                AuditEntityTypes.TextRoom,
                roomId.ToString(),
                $"Muted:{minutes}m",
                trimmedReason,
                targetUser!,
                actor,
                new { roomId, minutes, mutedUntil }));

            await _broadcaster.UserMutedAsync(roomId, userId, mutedUntil, trimmedReason);
            return Success("User muted.");
        }

        public async Task<Response<string>> KickUserAsync(
            Guid roomId, Guid userId, string? reason, StaffActor actor)
        {
            if (TextRoomSettingsValidator.NormalizeSanctionReason(reason) is not { } trimmedReason)
                return BadRequest<string>(TextRoomSettingsValidator.SanctionReasonError);

            var participant = await _db.TextRoomParticipants
                .FirstOrDefaultAsync(p => p.TextRoomId == roomId && p.UserId == userId);
            if (participant is null)
                return NotFound<string>("Participant not found in this text room.");

            var (targetUser, policyFailure) = await ValidateTargetUserAsync(userId, actor);
            if (policyFailure is not null)
                return policyFailure;

            participant.IsKicked = true;
            participant.KickedAt = _time.GetUtcNow().UtcDateTime;
            participant.KickedById = actor.UserId;
            await _db.SaveChangesAsync();

            var connectionIds = _presence.RemoveUser(roomId, userId);

            await RecordSanctionAuditAsync(new SanctionAuditRecord(
                AuditActions.TextRoomUserKicked,
                AuditEntityTypes.TextRoom,
                roomId.ToString(),
                "Kicked",
                trimmedReason,
                targetUser!,
                actor,
                new { roomId }));

            await _broadcaster.UserKickedAsync(roomId, userId, trimmedReason, connectionIds);

            if (connectionIds.Count > 0)
                await TextRoomPresencePublisher.PublishAsync(_db, _presence, _broadcaster, roomId);

            return Success("User kicked.");
        }

        private async Task<(ApplicationUser? User, Response<string>? Failure)> ValidateTargetUserAsync(
            Guid targetUserId, StaffActor actor)
        {
            var targetUser = await _userManager.FindByIdAsync(targetUserId.ToString());
            if (targetUser is null)
                return (null, NotFound<string>("Target user not found."));

            if (targetUserId == actor.UserId)
                return (null, BadRequest<string>("You cannot moderate yourself."));

            if (!actor.IsAdmin)
            {
                var targetRoles = await _userManager.GetRolesAsync(targetUser);
                // Admins are invisible to Moderators: return NotFound rather than Forbidden.
                if (targetRoles.Any(r => string.Equals(r, AppRoles.Admin, StringComparison.OrdinalIgnoreCase)))
                    return (null, NotFound<string>("Target not found."));

                if (AppRoles.AnyProtectedFromModerators(targetRoles))
                    return (null, Forbidden<string>("Moderators cannot sanction protected staff roles."));
            }

            return (targetUser, null);
        }

        private Task RecordSanctionAuditAsync(SanctionAuditRecord sanction) =>
            _auditLog.RecordAsync(new AuditEntry(
                sanction.Action,
                sanction.Actor.UserId,
                TargetUserId: sanction.TargetUser.Id,
                TargetDisplay: TextRoomMapper.FormatDisplayName(sanction.TargetUser.FirstName, sanction.TargetUser.LastName),
                Details: sanction.Details)
            {
                Category = AuditCategories.TextRoom,
                EntityType = sanction.EntityType,
                EntityId = sanction.EntityId,
                ToState = sanction.ToState,
                Reason = sanction.Reason,
                ActorRole = sanction.Actor.Roles
            });

        private readonly record struct SanctionAuditRecord(
            string Action,
            string EntityType,
            string EntityId,
            string ToState,
            string Reason,
            ApplicationUser TargetUser,
            StaffActor Actor,
            object Details);
    }
}
