using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Cocorra.BLL.Base;
using Cocorra.BLL.Services.TextRooms;
using Cocorra.DAL.AppMetaData;
using Cocorra.DAL.DTOS.TextRoomDto;
using Cocorra.DAL.Models;
using Cocorra.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cocorra.Tests.TextRooms
{
    public class TextRoomModerationTests : IDisposable
    {
        private readonly TextRoomTestHost _host = new();

        public void Dispose() => _host.Dispose();

        private async Task<long> SendMemberMessageAsync(Guid roomId, Guid userId, bool isStaff = false, string content = "Sample message")
        {
            var sent = await _host.With<ITextRoomService, SendTextRoomMessageResultDto>(svc =>
                svc.SendMessageAsync(roomId, userId, content, isStaff));
            Assert.Equal(SendTextRoomMessageStatus.Sent, sent.Status);
            return sent.Message!.Id;
        }

        [Theory]
        [InlineData(null)]
        [InlineData("  ")]
        [InlineData("ab")]
        [InlineData("too-long")]
        public async Task Sanctions_InvalidReasonLength_ReturnsBadRequestAcrossDeleteMuteAndKick(string? reasonCase)
        {
            var reason = reasonCase == "too-long" ? new string('r', 1001) : reasonCase;

            var admin = await _host.CreateUserAsync(AppRoles.Admin);
            var target = await _host.CreateUserAsync(AppRoles.User);
            var adminActor = TextRoomTestHost.AdminActor(admin);

            var room = await TextRoomTestFixtures.CreateRoomAsync(_host, adminActor);
            await TextRoomTestFixtures.JoinAndGoOnlineAsync(_host, room.RoomId, target.Id, "c1");
            var msgId = await SendMemberMessageAsync(room.RoomId, target.Id);

            var del = await _host.With<ITextRoomModerationService, Response<string>>(svc =>
                svc.DeleteMessageAsync(msgId, reason, adminActor));
            var mute = await _host.With<ITextRoomModerationService, Response<string>>(svc =>
                svc.MuteUserAsync(room.RoomId, target.Id, 10, reason, adminActor));
            var kick = await _host.With<ITextRoomModerationService, Response<string>>(svc =>
                svc.KickUserAsync(room.RoomId, target.Id, reason, adminActor));

            Assert.Equal(HttpStatusCode.BadRequest, del.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, mute.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, kick.StatusCode);
        }

        [Theory]
        [InlineData(3)]
        [InlineData(1000)]
        public async Task Sanctions_ReasonAtMinAndMaxBoundaries_Succeeds(int length)
        {
            var admin = await _host.CreateUserAsync(AppRoles.Admin);
            var target = await _host.CreateUserAsync(AppRoles.User);
            var adminActor = TextRoomTestHost.AdminActor(admin);

            var room = await TextRoomTestFixtures.CreateRoomAsync(_host, adminActor);
            await TextRoomTestFixtures.JoinAndGoOnlineAsync(_host, room.RoomId, target.Id, "c1");

            var reason = new string('x', length);
            var mute = await _host.With<ITextRoomModerationService, Response<string>>(svc =>
                svc.MuteUserAsync(room.RoomId, target.Id, 10, reason, adminActor));

            Assert.True(mute.Succeeded);
            var audit = Assert.Single(_host.Audit.ByAction(AuditActions.TextRoomUserMuted));
            Assert.Equal(reason, audit.Reason);
        }

        [Fact]
        public async Task ModeratorActingOnAdminTarget_ReturnsNotFoundForDeleteMuteAndKick()
        {
            var admin = await _host.CreateUserAsync(AppRoles.Admin);
            var moderator = await _host.CreateUserAsync(AppRoles.Moderator);
            var adminActor = TextRoomTestHost.AdminActor(admin);
            var modActor = TextRoomTestHost.ModeratorActor(moderator);

            var room = await TextRoomTestFixtures.CreateRoomAsync(_host, adminActor);
            await TextRoomTestFixtures.JoinAndGoOnlineAsync(_host, room.RoomId, admin.Id, "c-admin", isStaff: true);
            var adminMsgId = await SendMemberMessageAsync(room.RoomId, admin.Id, isStaff: true);

            var del = await _host.With<ITextRoomModerationService, Response<string>>(svc =>
                svc.DeleteMessageAsync(adminMsgId, "Valid moderation reason", modActor));
            var mute = await _host.With<ITextRoomModerationService, Response<string>>(svc =>
                svc.MuteUserAsync(room.RoomId, admin.Id, 10, "Valid moderation reason", modActor));
            var kick = await _host.With<ITextRoomModerationService, Response<string>>(svc =>
                svc.KickUserAsync(room.RoomId, admin.Id, "Valid moderation reason", modActor));

            Assert.Equal(HttpStatusCode.NotFound, del.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, mute.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, kick.StatusCode);
        }

        [Theory]
        [InlineData(AppRoles.Moderator)]
        [InlineData(AppRoles.Coach)]
        [InlineData(AppRoles.RecordReviewer)]
        public async Task ModeratorActingOnProtectedStaffRole_ReturnsForbidden(string protectedRole)
        {
            var admin = await _host.CreateUserAsync(AppRoles.Admin);
            var actingMod = await _host.CreateUserAsync(AppRoles.Moderator);
            var targetStaff = await _host.CreateUserAsync(protectedRole);
            var modActor = TextRoomTestHost.ModeratorActor(actingMod);

            var room = await TextRoomTestFixtures.CreateRoomAsync(_host, TextRoomTestHost.AdminActor(admin));
            var isTextRoomStaff = protectedRole == AppRoles.Moderator;
            await TextRoomTestFixtures.JoinAndGoOnlineAsync(_host, room.RoomId, targetStaff.Id, "c-target", isStaff: isTextRoomStaff);
            var msgId = await SendMemberMessageAsync(room.RoomId, targetStaff.Id, isStaff: isTextRoomStaff);

            var del = await _host.With<ITextRoomModerationService, Response<string>>(svc =>
                svc.DeleteMessageAsync(msgId, "Valid moderation reason", modActor));
            var mute = await _host.With<ITextRoomModerationService, Response<string>>(svc =>
                svc.MuteUserAsync(room.RoomId, targetStaff.Id, 10, "Valid moderation reason", modActor));
            var kick = await _host.With<ITextRoomModerationService, Response<string>>(svc =>
                svc.KickUserAsync(room.RoomId, targetStaff.Id, "Valid moderation reason", modActor));

            Assert.Equal(HttpStatusCode.Forbidden, del.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, mute.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, kick.StatusCode);
        }

        [Fact]
        public async Task ModeratorActingOnSelf_ReturnsBadRequest()
        {
            var admin = await _host.CreateUserAsync(AppRoles.Admin);
            var moderator = await _host.CreateUserAsync(AppRoles.Moderator);
            var modActor = TextRoomTestHost.ModeratorActor(moderator);

            var room = await TextRoomTestFixtures.CreateRoomAsync(_host, TextRoomTestHost.AdminActor(admin));
            await TextRoomTestFixtures.JoinAndGoOnlineAsync(_host, room.RoomId, moderator.Id, "c-mod", isStaff: true);
            var ownMsgId = await SendMemberMessageAsync(room.RoomId, moderator.Id, isStaff: true);

            var del = await _host.With<ITextRoomModerationService, Response<string>>(svc =>
                svc.DeleteMessageAsync(ownMsgId, "Self sanction attempt", modActor));
            var mute = await _host.With<ITextRoomModerationService, Response<string>>(svc =>
                svc.MuteUserAsync(room.RoomId, moderator.Id, 10, "Self sanction attempt", modActor));
            var kick = await _host.With<ITextRoomModerationService, Response<string>>(svc =>
                svc.KickUserAsync(room.RoomId, moderator.Id, "Self sanction attempt", modActor));

            Assert.Equal(HttpStatusCode.BadRequest, del.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, mute.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, kick.StatusCode);
        }

        [Fact]
        public async Task AdminActingOnModerator_SucceedsForDeleteMuteAndKick()
        {
            var admin = await _host.CreateUserAsync(AppRoles.Admin);
            var moderator = await _host.CreateUserAsync(AppRoles.Moderator);
            var adminActor = TextRoomTestHost.AdminActor(admin);

            var room = await TextRoomTestFixtures.CreateRoomAsync(_host, adminActor);
            await TextRoomTestFixtures.JoinAndGoOnlineAsync(_host, room.RoomId, moderator.Id, "c-mod", isStaff: true);
            var modMsgId = await SendMemberMessageAsync(room.RoomId, moderator.Id, isStaff: true);

            var del = await _host.With<ITextRoomModerationService, Response<string>>(svc =>
                svc.DeleteMessageAsync(modMsgId, "Admin override", adminActor));
            var mute = await _host.With<ITextRoomModerationService, Response<string>>(svc =>
                svc.MuteUserAsync(room.RoomId, moderator.Id, 15, "Admin override", adminActor));
            var kick = await _host.With<ITextRoomModerationService, Response<string>>(svc =>
                svc.KickUserAsync(room.RoomId, moderator.Id, "Admin override", adminActor));

            Assert.True(del.Succeeded);
            Assert.True(mute.Succeeded);
            Assert.True(kick.Succeeded);
        }

        [Fact]
        public async Task DeleteMessage_SoftDeletesRow_BroadcastsAndRecordsAudit()
        {
            var admin = await _host.CreateUserAsync(AppRoles.Admin);
            var sender = await _host.CreateUserAsync(AppRoles.User);
            var adminActor = TextRoomTestHost.AdminActor(admin);

            var room = await TextRoomTestFixtures.CreateRoomAsync(_host, adminActor);
            await TextRoomTestFixtures.JoinAndGoOnlineAsync(_host, room.RoomId, sender.Id, "c1");
            var msgId = await SendMemberMessageAsync(room.RoomId, sender.Id, content: "Off-topic ad");

            var result = await _host.With<ITextRoomModerationService, Response<string>>(svc =>
                svc.DeleteMessageAsync(msgId, "  Unsolicited promotion  ", adminActor));

            Assert.True(result.Succeeded);

            var persisted = await _host.WithDb(db =>
                db.TextRoomMessages.AsNoTracking().SingleOrDefaultAsync(m => m.Id == msgId));
            Assert.NotNull(persisted);
            Assert.True(persisted!.IsDeleted);
            Assert.Equal(admin.Id, persisted.DeletedById);
            Assert.Equal(_host.Clock.UtcNow, persisted.DeletedAt);

            var broadcast = Assert.Single(_host.Broadcaster.CallsTo(nameof(ITextRoomBroadcaster.MessageDeletedAsync)));
            Assert.Equal(room.RoomId, broadcast[0]);
            Assert.Equal(msgId, broadcast[1]);

            var audit = Assert.Single(_host.Audit.ByAction(AuditActions.TextRoomMessageDeleted));
            Assert.Equal(admin.Id, audit.ActorUserId);
            Assert.Equal(sender.Id, audit.TargetUserId);
            Assert.Equal("Unsolicited promotion", audit.Reason);
            Assert.Equal(AuditCategories.TextRoom, audit.Category);
            Assert.Equal(AuditEntityTypes.TextRoomMessage, audit.EntityType);
            Assert.Equal(msgId.ToString(), audit.EntityId);
        }

        [Fact]
        public async Task MuteUser_SetsMutedUntil_BlocksSendingUntilExpiryThenAllows()
        {
            var admin = await _host.CreateUserAsync(AppRoles.Admin);
            var user = await _host.CreateUserAsync(AppRoles.User);
            var adminActor = TextRoomTestHost.AdminActor(admin);

            var room = await TextRoomTestFixtures.CreateRoomAsync(_host, adminActor);
            await TextRoomTestFixtures.JoinAndGoOnlineAsync(_host, room.RoomId, user.Id, "c1");

            var expectedMutedUntil = _host.Clock.UtcNow.AddMinutes(20);
            var mute = await _host.With<ITextRoomModerationService, Response<string>>(svc =>
                svc.MuteUserAsync(room.RoomId, user.Id, 20, "Repeated flooding", adminActor));
            Assert.True(mute.Succeeded);

            var participant = await _host.WithDb(db =>
                db.TextRoomParticipants.AsNoTracking()
                    .SingleAsync(p => p.TextRoomId == room.RoomId && p.UserId == user.Id));
            Assert.Equal(expectedMutedUntil, participant.MutedUntil);
            Assert.Equal(admin.Id, participant.MutedById);

            var mutedBroadcast = Assert.Single(_host.Broadcaster.CallsTo(nameof(ITextRoomBroadcaster.UserMutedAsync)));
            Assert.Equal(room.RoomId, mutedBroadcast[0]);
            Assert.Equal(user.Id, mutedBroadcast[1]);
            Assert.Equal(expectedMutedUntil, mutedBroadcast[2]);

            var audit = Assert.Single(_host.Audit.ByAction(AuditActions.TextRoomUserMuted));
            Assert.Equal(AuditCategories.TextRoom, audit.Category);
            Assert.Equal(user.Id, audit.TargetUserId);
            Assert.Equal("Repeated flooding", audit.Reason);

            var blockedSend = await _host.With<ITextRoomService, SendTextRoomMessageResultDto>(svc =>
                svc.SendMessageAsync(room.RoomId, user.Id, "Still muted?", senderIsStaff: false));
            Assert.Equal(SendTextRoomMessageStatus.Muted, blockedSend.Status);
            Assert.Equal(expectedMutedUntil, blockedSend.MutedUntil);

            _host.Clock.Advance(TimeSpan.FromMinutes(20).Add(TimeSpan.FromSeconds(1)));

            var unmutedSend = await _host.With<ITextRoomService, SendTextRoomMessageResultDto>(svc =>
                svc.SendMessageAsync(room.RoomId, user.Id, "Back after mute", senderIsStaff: false));
            Assert.Equal(SendTextRoomMessageStatus.Sent, unmutedSend.Status);
        }

        [Theory]
        [InlineData(0, false)]
        [InlineData(1, true)]
        [InlineData(TextRoomLimits.MuteMinutesMax, true)]
        [InlineData(TextRoomLimits.MuteMinutesMax + 1, false)]
        public async Task MuteUser_MinutesBoundaries_EnforcesLimits(int minutes, bool shouldSucceed)
        {
            var admin = await _host.CreateUserAsync(AppRoles.Admin);
            var user = await _host.CreateUserAsync(AppRoles.User);
            var adminActor = TextRoomTestHost.AdminActor(admin);

            var room = await TextRoomTestFixtures.CreateRoomAsync(_host, adminActor);
            await TextRoomTestFixtures.JoinAndGoOnlineAsync(_host, room.RoomId, user.Id, "c1");

            var result = await _host.With<ITextRoomModerationService, Response<string>>(svc =>
                svc.MuteUserAsync(room.RoomId, user.Id, minutes, "Boundary check reason", adminActor));

            Assert.Equal(shouldSucceed, result.Succeeded);
            if (!shouldSucceed)
                Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        }

        [Fact]
        public async Task KickUser_MarksParticipantKicked_DropsAllConnections_BroadcastsAndAudits()
        {
            var admin = await _host.CreateUserAsync(AppRoles.Admin);
            var user = await _host.CreateUserAsync(AppRoles.User);
            var adminActor = TextRoomTestHost.AdminActor(admin);
            var tracker = _host.Services.GetRequiredService<ITextRoomPresenceTracker>();

            var room = await TextRoomTestFixtures.CreateRoomAsync(_host, adminActor);
            await TextRoomTestFixtures.JoinAndGoOnlineAsync(_host, room.RoomId, user.Id, "conn-1");
            tracker.AddConnection(room.RoomId, user.Id, "conn-2");

            var kick = await _host.With<ITextRoomModerationService, Response<string>>(svc =>
                svc.KickUserAsync(room.RoomId, user.Id, "Severe harassment", adminActor));

            Assert.True(kick.Succeeded);

            var participant = await _host.WithDb(db =>
                db.TextRoomParticipants.AsNoTracking()
                    .SingleAsync(p => p.TextRoomId == room.RoomId && p.UserId == user.Id));
            Assert.True(participant.IsKicked);
            Assert.Equal(admin.Id, participant.KickedById);
            Assert.Equal(_host.Clock.UtcNow, participant.KickedAt);

            Assert.False(tracker.IsOnline(room.RoomId, user.Id));
            Assert.Equal(0, tracker.GetOnlineCount(room.RoomId));

            var broadcast = Assert.Single(_host.Broadcaster.CallsTo(nameof(ITextRoomBroadcaster.UserKickedAsync)));
            Assert.Equal(room.RoomId, broadcast[0]);
            Assert.Equal(user.Id, broadcast[1]);
            Assert.Equal("Severe harassment", broadcast[2]);
            var removedConnections = Assert.IsAssignableFrom<IReadOnlyList<string>>(broadcast[3]);
            Assert.Equal(new[] { "conn-1", "conn-2" }, removedConnections.OrderBy(c => c).ToArray());

            var rejoin = await _host.With<ITextRoomService, TextRoomJoinResultDto>(svc =>
                svc.JoinAsync(room.RoomId, user.Id, viewerIsStaff: false, TextRoomTestFixtures.NewConnectionId()));
            Assert.Equal(TextRoomJoinStatus.Kicked, rejoin.Status);

            var audit = Assert.Single(_host.Audit.ByAction(AuditActions.TextRoomUserKicked));
            Assert.Equal(admin.Id, audit.ActorUserId);
            Assert.Equal(user.Id, audit.TargetUserId);
            Assert.Equal(AuditCategories.TextRoom, audit.Category);
            Assert.Equal(AuditEntityTypes.TextRoom, audit.EntityType);
            Assert.Equal(room.RoomId.ToString(), audit.EntityId);
            Assert.Equal("Severe harassment", audit.Reason);
        }

        [Fact]
        public async Task MuteAndKick_UserWithoutParticipantRow_ReturnNotFound()
        {
            var admin = await _host.CreateUserAsync(AppRoles.Admin);
            var outsider = await _host.CreateUserAsync(AppRoles.User);
            var adminActor = TextRoomTestHost.AdminActor(admin);

            var room = await TextRoomTestFixtures.CreateRoomAsync(_host, adminActor);

            var mute = await _host.With<ITextRoomModerationService, Response<string>>(svc =>
                svc.MuteUserAsync(room.RoomId, outsider.Id, 10, "Valid moderation reason", adminActor));
            var kick = await _host.With<ITextRoomModerationService, Response<string>>(svc =>
                svc.KickUserAsync(room.RoomId, outsider.Id, "Valid moderation reason", adminActor));

            Assert.Equal(HttpStatusCode.NotFound, mute.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, kick.StatusCode);
        }
    }
}
