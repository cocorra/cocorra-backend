using System;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Cocorra.BLL.Base;
using Cocorra.BLL.Services.ContentModeration;
using Cocorra.BLL.Services.TextRooms;
using Cocorra.DAL.DTOS.TextRoomDto;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;
using Cocorra.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using Xunit;

namespace Cocorra.Tests.TextRooms
{
    public class TextRoomSendMessageTests : IDisposable
    {
        private readonly TextRoomTestHost _host = new();

        public void Dispose() => _host.Dispose();

        private static async Task SeedWordAsync(
            TextRoomTestHost host,
            string term,
            WordAction action,
            WordCategory category = WordCategory.Profanity,
            ModerationSeverity severity = ModerationSeverity.High)
        {
            await host.WithDb(async db =>
            {
                db.WordDictionaryEntries.Add(new WordDictionaryEntry
                {
                    Term = term,
                    NormalizedTerm = term.ToLowerInvariant(),
                    MatchType = WordMatchType.WholeWord,
                    Category = category,
                    Severity = severity,
                    Action = action,
                    IsActive = true
                });
                await db.SaveChangesAsync();
            });

            await using var scope = host.CreateScope();
            scope.ServiceProvider.GetRequiredService<IWordFilter>().InvalidateCache();
        }

        [Fact]
        public async Task SendMessage_UnknownRoom_ReturnsNotFound()
        {
            var user = await _host.CreateUserAsync(AppRoles.User);

            var result = await _host.With<ITextRoomService, SendTextRoomMessageResultDto>(svc =>
                svc.SendMessageAsync(Guid.NewGuid(), user.Id, "Hello", senderIsStaff: false));

            Assert.Equal(SendTextRoomMessageStatus.NotFound, result.Status);
        }

        [Fact]
        public async Task SendMessage_ClosedRoom_ReturnsClosedBeforeMembershipOrContentChecks()
        {
            var admin = await _host.CreateUserAsync(AppRoles.Admin);
            var user = await _host.CreateUserAsync(AppRoles.User);
            var adminActor = TextRoomTestHost.AdminActor(admin);

            var room = await TextRoomTestFixtures.CreateRoomAsync(_host, adminActor);
            await TextRoomTestFixtures.JoinAndGoOnlineAsync(_host, room.RoomId, user.Id, "c1");
            await _host.With<ITextRoomAdminService, Response<string>>(svc =>
                svc.CloseRoomAsync(room.RoomId, "Done", adminActor));

            // Empty content verifies room status (Closed) is checked ahead of content validation.
            var result = await _host.With<ITextRoomService, SendTextRoomMessageResultDto>(svc =>
                svc.SendMessageAsync(room.RoomId, user.Id, "", senderIsStaff: false));

            Assert.Equal(SendTextRoomMessageStatus.Closed, result.Status);
        }

        [Fact]
        public async Task SendMessage_NonMemberWithoutParticipantRow_ReturnsNotMember()
        {
            var admin = await _host.CreateUserAsync(AppRoles.Admin);
            var outsider = await _host.CreateUserAsync(AppRoles.User);
            var room = await TextRoomTestFixtures.CreateRoomAsync(_host, TextRoomTestHost.AdminActor(admin));

            var result = await _host.With<ITextRoomService, SendTextRoomMessageResultDto>(svc =>
                svc.SendMessageAsync(room.RoomId, outsider.Id, "Hello", senderIsStaff: false));

            Assert.Equal(SendTextRoomMessageStatus.NotMember, result.Status);
        }

        [Fact]
        public async Task SendMessage_ParticipantNotOnline_ReturnsNotMember()
        {
            var admin = await _host.CreateUserAsync(AppRoles.Admin);
            var user = await _host.CreateUserAsync(AppRoles.User);
            var room = await TextRoomTestFixtures.CreateRoomAsync(_host, TextRoomTestHost.AdminActor(admin));

            var connectionId = TextRoomTestFixtures.NewConnectionId();
            var join = await _host.With<ITextRoomService, TextRoomJoinResultDto>(svc =>
                svc.JoinAsync(room.RoomId, user.Id, viewerIsStaff: false, connectionId));
            Assert.Equal(TextRoomJoinStatus.Joined, join.Status);
            await _host.With<ITextRoomService, bool>(async svc =>
            {
                await svc.LeaveAsync(room.RoomId, user.Id, connectionId);
                return true;
            });

            var result = await _host.With<ITextRoomService, SendTextRoomMessageResultDto>(svc =>
                svc.SendMessageAsync(room.RoomId, user.Id, "Hello", senderIsStaff: false));

            Assert.Equal(SendTextRoomMessageStatus.NotMember, result.Status);
        }

        [Fact]
        public async Task SendMessage_MutedMember_ReturnsMutedWithMutedUntilAheadOfEmptyCheck()
        {
            var admin = await _host.CreateUserAsync(AppRoles.Admin);
            var user = await _host.CreateUserAsync(AppRoles.User);
            var adminActor = TextRoomTestHost.AdminActor(admin);

            var room = await TextRoomTestFixtures.CreateRoomAsync(_host, adminActor);
            await TextRoomTestFixtures.JoinAndGoOnlineAsync(_host, room.RoomId, user.Id, "c1");

            var mute = await _host.With<ITextRoomModerationService, Response<string>>(svc =>
                svc.MuteUserAsync(room.RoomId, user.Id, 10, "Spamming the chat", adminActor));
            Assert.True(mute.Succeeded);
            var expectedMutedUntil = _host.Clock.UtcNow.AddMinutes(10);

            var result = await _host.With<ITextRoomService, SendTextRoomMessageResultDto>(svc =>
                svc.SendMessageAsync(room.RoomId, user.Id, "", senderIsStaff: false));

            Assert.Equal(SendTextRoomMessageStatus.Muted, result.Status);
            Assert.Equal(expectedMutedUntil, result.MutedUntil);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   \t\r\n  ")]
        public async Task SendMessage_EmptyOrWhitespaceContent_ReturnsEmpty(string? content)
        {
            var admin = await _host.CreateUserAsync(AppRoles.Admin);
            var user = await _host.CreateUserAsync(AppRoles.User);
            var room = await TextRoomTestFixtures.CreateRoomAsync(_host, TextRoomTestHost.AdminActor(admin));
            await TextRoomTestFixtures.JoinAndGoOnlineAsync(_host, room.RoomId, user.Id, "c1");

            var result = await _host.With<ITextRoomService, SendTextRoomMessageResultDto>(svc =>
                svc.SendMessageAsync(room.RoomId, user.Id, content, senderIsStaff: false));

            Assert.Equal(SendTextRoomMessageStatus.Empty, result.Status);
        }

        [Fact]
        public async Task SendMessage_ExactMaxMessageLengthSucceeds_PlusOneReturnsTooLong()
        {
            var admin = await _host.CreateUserAsync(AppRoles.Admin);
            var user = await _host.CreateUserAsync(AppRoles.User);
            var room = await TextRoomTestFixtures.CreateRoomAsync(
                _host,
                TextRoomTestHost.AdminActor(admin),
                TextRoomTestFixtures.ValidRoomDto(configureSettings: s => s.MaxMessageLength = 20));
            await TextRoomTestFixtures.JoinAndGoOnlineAsync(_host, room.RoomId, user.Id, "c1");

            var atLimit = await _host.With<ITextRoomService, SendTextRoomMessageResultDto>(svc =>
                svc.SendMessageAsync(room.RoomId, user.Id, new string('x', 20), senderIsStaff: false));
            Assert.Equal(SendTextRoomMessageStatus.Sent, atLimit.Status);

            var overLimit = await _host.With<ITextRoomService, SendTextRoomMessageResultDto>(svc =>
                svc.SendMessageAsync(room.RoomId, user.Id, new string('x', 21), senderIsStaff: false));
            Assert.Equal(SendTextRoomMessageStatus.TooLong, overLimit.Status);
        }

        [Fact]
        public async Task SendMessage_SlowModeFiveSeconds_BlocksAtFourSecondsWithRetryOne_AllowsAtFiveSeconds()
        {
            var admin = await _host.CreateUserAsync(AppRoles.Admin);
            var user = await _host.CreateUserAsync(AppRoles.User);
            var room = await TextRoomTestFixtures.CreateRoomAsync(
                _host,
                TextRoomTestHost.AdminActor(admin),
                TextRoomTestFixtures.ValidRoomDto(configureSettings: s => s.SlowModeSeconds = 5));
            await TextRoomTestFixtures.JoinAndGoOnlineAsync(_host, room.RoomId, user.Id, "c1");

            var first = await _host.With<ITextRoomService, SendTextRoomMessageResultDto>(svc =>
                svc.SendMessageAsync(room.RoomId, user.Id, "First", senderIsStaff: false));
            Assert.Equal(SendTextRoomMessageStatus.Sent, first.Status);

            _host.Clock.Advance(TimeSpan.FromSeconds(4));

            var tooSoon = await _host.With<ITextRoomService, SendTextRoomMessageResultDto>(svc =>
                svc.SendMessageAsync(room.RoomId, user.Id, "Second", senderIsStaff: false));
            Assert.Equal(SendTextRoomMessageStatus.SlowMode, tooSoon.Status);
            Assert.Equal(1, tooSoon.RetryAfterSeconds);

            _host.Clock.Advance(TimeSpan.FromSeconds(1));

            var allowed = await _host.With<ITextRoomService, SendTextRoomMessageResultDto>(svc =>
                svc.SendMessageAsync(room.RoomId, user.Id, "Third", senderIsStaff: false));
            Assert.Equal(SendTextRoomMessageStatus.Sent, allowed.Status);
        }

        [Fact]
        public async Task SendMessage_SlowMode_StaffIsExempt()
        {
            var admin = await _host.CreateUserAsync(AppRoles.Admin);
            var room = await TextRoomTestFixtures.CreateRoomAsync(
                _host,
                TextRoomTestHost.AdminActor(admin),
                TextRoomTestFixtures.ValidRoomDto(configureSettings: s => s.SlowModeSeconds = 30));
            await TextRoomTestFixtures.JoinAndGoOnlineAsync(_host, room.RoomId, admin.Id, "c-staff", isStaff: true);

            var first = await _host.With<ITextRoomService, SendTextRoomMessageResultDto>(svc =>
                svc.SendMessageAsync(room.RoomId, admin.Id, "Notice 1", senderIsStaff: true));
            var second = await _host.With<ITextRoomService, SendTextRoomMessageResultDto>(svc =>
                svc.SendMessageAsync(room.RoomId, admin.Id, "Notice 2", senderIsStaff: true));

            Assert.Equal(SendTextRoomMessageStatus.Sent, first.Status);
            Assert.Equal(SendTextRoomMessageStatus.Sent, second.Status);
        }

        [Fact]
        public async Task SendMessage_RejectedTooLongMessage_DoesNotStartSlowModeCooldown()
        {
            var admin = await _host.CreateUserAsync(AppRoles.Admin);
            var user = await _host.CreateUserAsync(AppRoles.User);
            var room = await TextRoomTestFixtures.CreateRoomAsync(
                _host,
                TextRoomTestHost.AdminActor(admin),
                TextRoomTestFixtures.ValidRoomDto(configureSettings: s =>
                {
                    s.SlowModeSeconds = 30;
                    s.MaxMessageLength = 10;
                }));
            await TextRoomTestFixtures.JoinAndGoOnlineAsync(_host, room.RoomId, user.Id, "c1");

            var rejected = await _host.With<ITextRoomService, SendTextRoomMessageResultDto>(svc =>
                svc.SendMessageAsync(room.RoomId, user.Id, new string('z', 11), senderIsStaff: false));
            Assert.Equal(SendTextRoomMessageStatus.TooLong, rejected.Status);

            var immediateValid = await _host.With<ITextRoomService, SendTextRoomMessageResultDto>(svc =>
                svc.SendMessageAsync(room.RoomId, user.Id, "short ok", senderIsStaff: false));
            Assert.Equal(SendTextRoomMessageStatus.Sent, immediateValid.Status);
        }

        [Fact]
        public async Task SendMessage_DictionaryBlockMessage_ReturnsBlockedWithNoRowAndNoBroadcast()
        {
            var admin = await _host.CreateUserAsync(AppRoles.Admin);
            var user = await _host.CreateUserAsync(AppRoles.User);
            var room = await TextRoomTestFixtures.CreateRoomAsync(_host, TextRoomTestHost.AdminActor(admin));
            await TextRoomTestFixtures.JoinAndGoOnlineAsync(_host, room.RoomId, user.Id, "c1");

            await SeedWordAsync(_host, "forbidden", WordAction.BlockMessage);

            var result = await _host.With<ITextRoomService, SendTextRoomMessageResultDto>(svc =>
                svc.SendMessageAsync(room.RoomId, user.Id, "hello forbidden world", senderIsStaff: false));

            Assert.Equal(SendTextRoomMessageStatus.Blocked, result.Status);
            Assert.Null(result.Message);

            var messageCount = await _host.WithDb(db => db.TextRoomMessages.CountAsync(m => m.TextRoomId == room.RoomId));
            Assert.Equal(0, messageCount);
            Assert.Empty(_host.Broadcaster.CallsTo(nameof(ITextRoomBroadcaster.MessageReceivedAsync)));
        }

        [Fact]
        public async Task SendMessage_DictionaryMaskWord_ReturnsMaskedAndPersistsAndBroadcastsMaskedContent()
        {
            var admin = await _host.CreateUserAsync(AppRoles.Admin);
            var user = await _host.CreateUserAsync(AppRoles.User);
            var room = await TextRoomTestFixtures.CreateRoomAsync(_host, TextRoomTestHost.AdminActor(admin));
            await TextRoomTestFixtures.JoinAndGoOnlineAsync(_host, room.RoomId, user.Id, "c1");

            await SeedWordAsync(_host, "curse", WordAction.MaskWord);

            var result = await _host.With<ITextRoomService, SendTextRoomMessageResultDto>(svc =>
                svc.SendMessageAsync(room.RoomId, user.Id, "please curse here", senderIsStaff: false));

            Assert.Equal(SendTextRoomMessageStatus.Masked, result.Status);
            Assert.NotNull(result.Message);
            Assert.True(result.Message!.IsMasked);
            Assert.Equal("please ***** here", result.Message.Content);

            var persisted = await _host.WithDb(db =>
                db.TextRoomMessages.AsNoTracking().SingleAsync(m => m.Id == result.Message.Id));
            Assert.True(persisted.IsMasked);
            Assert.Equal("please ***** here", persisted.Content);

            var broadcast = Assert.Single(_host.Broadcaster.CallsTo(nameof(ITextRoomBroadcaster.MessageReceivedAsync)));
            var dto = Assert.IsType<TextRoomMessageDto>(broadcast[0]);
            Assert.True(dto.IsMasked);
            Assert.Equal("please ***** here", dto.Content);
        }

        [Fact]
        public async Task SendMessage_DictionaryAllowAndAutoReport_DeliversMessageAndFilesOneAutoModerationReport()
        {
            var admin = await _host.CreateUserAsync(AppRoles.Admin);
            var user = await _host.CreateUserAsync(AppRoles.User);
            var room = await TextRoomTestFixtures.CreateRoomAsync(_host, TextRoomTestHost.AdminActor(admin));
            await TextRoomTestFixtures.JoinAndGoOnlineAsync(_host, room.RoomId, user.Id, "c1");

            await SeedWordAsync(_host, "flagged", WordAction.AllowAndAutoReport, WordCategory.Harassment, ModerationSeverity.High);

            var result = await _host.With<ITextRoomService, SendTextRoomMessageResultDto>(svc =>
                svc.SendMessageAsync(room.RoomId, user.Id, "this contains flagged speech", senderIsStaff: false));

            Assert.Equal(SendTextRoomMessageStatus.Sent, result.Status);
            Assert.Equal("this contains flagged speech", result.Message!.Content);

            var report = await _host.WithDb(db => db.Reports.AsNoTracking()
                .SingleAsync(r => r.ReportedTextRoomMessageId == result.Message.Id));
            Assert.Equal(ReportSource.AutoModeration, report.Source);
            Assert.Equal(ModerationSeverity.High, report.Severity);
            Assert.Equal(user.Id, report.ReportedUserId);
            Assert.Equal(room.RoomId, report.ReportedTextRoomId);

            var message = await _host.WithDb(db => db.TextRoomMessages.AsNoTracking()
                .SingleAsync(m => m.Id == result.Message.Id));
            Assert.True(message.IsReported);
        }

        [Fact]
        public async Task SendMessage_AutoReportServiceThrows_StillPersistsBroadcastsAndReturnsSent()
        {
            var reportMock = new Mock<ITextRoomReportService>();
            reportMock
                .Setup(r => r.CreateAutoReportAsync(
                    It.IsAny<TextRoomMessage>(),
                    It.IsAny<WordFilterHit>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("Simulated report pipeline outage"));

            using var isolatedHost = new TextRoomTestHost(services =>
                services.Replace(ServiceDescriptor.Singleton(reportMock.Object)));

            var admin = await isolatedHost.CreateUserAsync(AppRoles.Admin);
            var user = await isolatedHost.CreateUserAsync(AppRoles.User);
            var room = await TextRoomTestFixtures.CreateRoomAsync(isolatedHost, TextRoomTestHost.AdminActor(admin));
            await TextRoomTestFixtures.JoinAndGoOnlineAsync(isolatedHost, room.RoomId, user.Id, "c1");

            await SeedWordAsync(isolatedHost, "flagged", WordAction.AllowAndAutoReport, WordCategory.Harassment, ModerationSeverity.High);

            var result = await isolatedHost.With<ITextRoomService, SendTextRoomMessageResultDto>(svc =>
                svc.SendMessageAsync(room.RoomId, user.Id, "this contains flagged speech", senderIsStaff: false));

            Assert.Equal(SendTextRoomMessageStatus.Sent, result.Status);
            Assert.NotNull(result.Message);

            var exists = await isolatedHost.WithDb(db =>
                db.TextRoomMessages.AnyAsync(m => m.Id == result.Message!.Id));
            Assert.True(exists);
            Assert.Single(isolatedHost.Broadcaster.CallsTo(nameof(ITextRoomBroadcaster.MessageReceivedAsync)));
        }

        [Fact]
        public async Task SendMessage_ValidMessage_BroadcastsSenderNameWithNoEmailInDto()
        {
            var admin = await _host.CreateUserAsync(AppRoles.Admin);
            var user = await _host.CreateUserAsync(AppRoles.User, firstName: "Layla", lastName: "Hassan");
            var room = await TextRoomTestFixtures.CreateRoomAsync(_host, TextRoomTestHost.AdminActor(admin));
            await TextRoomTestFixtures.JoinAndGoOnlineAsync(_host, room.RoomId, user.Id, "c1");

            var result = await _host.With<ITextRoomService, SendTextRoomMessageResultDto>(svc =>
                svc.SendMessageAsync(room.RoomId, user.Id, "Hello everyone!", senderIsStaff: false));

            Assert.Equal(SendTextRoomMessageStatus.Sent, result.Status);
            Assert.NotNull(result.Message);

            var broadcast = Assert.Single(_host.Broadcaster.CallsTo(nameof(ITextRoomBroadcaster.MessageReceivedAsync)));
            var dto = Assert.IsType<TextRoomMessageDto>(broadcast[0]);

            Assert.Equal(result.Message!.Id, dto.Id);
            Assert.Equal(room.RoomId, dto.RoomId);
            Assert.Equal(user.Id, dto.SenderId);
            Assert.Equal("Layla Hassan", dto.SenderName);
            Assert.Equal("Hello everyone!", dto.Content);

            var serialized = JsonSerializer.Serialize(dto);
            Assert.DoesNotContain(user.Email!, serialized, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                typeof(TextRoomMessageDto).GetProperties(BindingFlags.Public | BindingFlags.Instance),
                p => p.Name.Contains("Email", StringComparison.OrdinalIgnoreCase));
        }
    }
}
