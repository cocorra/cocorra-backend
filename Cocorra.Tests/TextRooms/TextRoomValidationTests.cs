using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Cocorra.BLL.Base;
using Cocorra.BLL.Services.TextRooms;
using Cocorra.DAL.AppMetaData;
using Cocorra.DAL.DTOS.TextRoomDto;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;
using Cocorra.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Cocorra.Tests.TextRooms
{
    public class TextRoomValidationTests : IDisposable
    {
        private readonly TextRoomTestHost _host = new();

        public void Dispose() => _host.Dispose();

        private async Task<StaffActor> CreateAdminActorAsync() =>
            TextRoomTestHost.AdminActor(await _host.CreateUserAsync(AppRoles.Admin));

        [Theory]
        [InlineData(2, false)]
        [InlineData(3, true)]
        [InlineData(120, true)]
        [InlineData(121, false)]
        public async Task CreateRoom_TitleLengthBoundaries_EnforcesLimits(int length, bool shouldSucceed)
        {
            var admin = await CreateAdminActorAsync();
            var dto = TextRoomTestFixtures.ValidRoomDto(new string('A', length));

            var result = await _host.With<ITextRoomAdminService, Response<TextRoomStaffViewDto>>(svc =>
                svc.CreateRoomAsync(dto, admin));

            Assert.Equal(shouldSucceed, result.Succeeded);
            if (!shouldSucceed)
                Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("\t\r\n")]
        public async Task CreateRoom_BlankOrWhitespaceTitle_ReturnsBadRequest(string title)
        {
            var admin = await CreateAdminActorAsync();
            var dto = TextRoomTestFixtures.ValidRoomDto(title);

            var result = await _host.With<ITextRoomAdminService, Response<TextRoomStaffViewDto>>(svc =>
                svc.CreateRoomAsync(dto, admin));

            Assert.False(result.Succeeded);
            Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        }

        [Theory]
        [InlineData(TextRoomLimits.MaxPrompts, true)]
        [InlineData(TextRoomLimits.MaxPrompts + 1, false)]
        public async Task CreateRoom_DiscussionPromptCountBoundaries_EnforcesLimits(int count, bool shouldSucceed)
        {
            var admin = await CreateAdminActorAsync();
            var dto = TextRoomTestFixtures.ValidRoomDto(configure: d =>
                d.DiscussionPrompts = Enumerable.Range(1, count).Select(i => $"Prompt #{i}").ToList());

            var result = await _host.With<ITextRoomAdminService, Response<TextRoomStaffViewDto>>(svc =>
                svc.CreateRoomAsync(dto, admin));

            Assert.Equal(shouldSucceed, result.Succeeded);
            if (!shouldSucceed)
                Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public async Task CreateRoom_BlankDiscussionPrompt_ReturnsBadRequest(string blankPrompt)
        {
            var admin = await CreateAdminActorAsync();
            var dto = TextRoomTestFixtures.ValidRoomDto(configure: d =>
                d.DiscussionPrompts = new List<string> { "Valid prompt", blankPrompt });

            var result = await _host.With<ITextRoomAdminService, Response<TextRoomStaffViewDto>>(svc =>
                svc.CreateRoomAsync(dto, admin));

            Assert.False(result.Succeeded);
            Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        }

        [Theory]
        [InlineData(TextRoomLimits.PromptMax, true)]
        [InlineData(TextRoomLimits.PromptMax + 1, false)]
        public async Task CreateRoom_DiscussionPromptLengthBoundaries_EnforcesLimits(int length, bool shouldSucceed)
        {
            var admin = await CreateAdminActorAsync();
            var dto = TextRoomTestFixtures.ValidRoomDto(configure: d =>
                d.DiscussionPrompts = new List<string> { new string('P', length) });

            var result = await _host.With<ITextRoomAdminService, Response<TextRoomStaffViewDto>>(svc =>
                svc.CreateRoomAsync(dto, admin));

            Assert.Equal(shouldSucceed, result.Succeeded);
            if (!shouldSucceed)
                Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        }

        [Theory]
        [InlineData(null, false)]
        [InlineData(4, false)]
        [InlineData(5, true)]
        [InlineData(10080, true)]
        [InlineData(10081, false)]
        public async Task CreateRoom_TimedDurationBoundaries_EnforcesLimits(int? durationMinutes, bool shouldSucceed)
        {
            var admin = await CreateAdminActorAsync();
            var dto = TextRoomTestFixtures.ValidRoomDto(configureSettings: s =>
            {
                s.IsAlwaysOn = false;
                s.DurationMinutes = durationMinutes;
            });

            var result = await _host.With<ITextRoomAdminService, Response<TextRoomStaffViewDto>>(svc =>
                svc.CreateRoomAsync(dto, admin));

            Assert.Equal(shouldSucceed, result.Succeeded);
            if (!shouldSucceed)
                Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        }

        [Fact]
        public async Task CreateRoom_RotationOnTimedRoom_ReturnsBadRequest()
        {
            var admin = await CreateAdminActorAsync();
            var dto = TextRoomTestFixtures.ValidRoomDto(configureSettings: s =>
            {
                s.IsAlwaysOn = false;
                s.DurationMinutes = 60;
                s.RotateTopicEveryMinutes = 30;
            });

            var result = await _host.With<ITextRoomAdminService, Response<TextRoomStaffViewDto>>(svc =>
                svc.CreateRoomAsync(dto, admin));

            Assert.False(result.Succeeded);
            Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        }

        [Theory]
        [InlineData(4, false)]
        [InlineData(5, true)]
        [InlineData(10080, true)]
        [InlineData(10081, false)]
        public async Task CreateRoom_AlwaysOnRotationIntervalBoundaries_EnforcesLimits(int rotateMinutes, bool shouldSucceed)
        {
            var admin = await CreateAdminActorAsync();
            var dto = TextRoomTestFixtures.ValidRoomDto(configureSettings: s =>
            {
                s.IsAlwaysOn = true;
                s.DurationMinutes = null;
                s.RotateTopicEveryMinutes = rotateMinutes;
            });

            var result = await _host.With<ITextRoomAdminService, Response<TextRoomStaffViewDto>>(svc =>
                svc.CreateRoomAsync(dto, admin));

            Assert.Equal(shouldSucceed, result.Succeeded);
            if (!shouldSucceed)
                Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        }

        [Theory]
        [InlineData(1, false)]
        [InlineData(2, true)]
        [InlineData(10000, true)]
        [InlineData(10001, false)]
        public async Task CreateRoom_MaxParticipantsBoundaries_EnforcesLimits(int maxParticipants, bool shouldSucceed)
        {
            var admin = await CreateAdminActorAsync();
            var dto = TextRoomTestFixtures.ValidRoomDto(configureSettings: s =>
                s.MaxParticipants = maxParticipants);

            var result = await _host.With<ITextRoomAdminService, Response<TextRoomStaffViewDto>>(svc =>
                svc.CreateRoomAsync(dto, admin));

            Assert.Equal(shouldSucceed, result.Succeeded);
            if (!shouldSucceed)
                Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        }

        [Theory]
        [InlineData(10, 11, false)]
        [InlineData(10, 0, false)]
        [InlineData(10, 10, true)]
        public async Task CreateRoom_OverflowTriggerAgainstMaxParticipants_EnforcesLimits(
            int maxParticipants, int triggerCount, bool shouldSucceed)
        {
            var admin = await CreateAdminActorAsync();
            var dto = TextRoomTestFixtures.ValidRoomDto(configureSettings: s =>
            {
                s.MaxParticipants = maxParticipants;
                s.OverflowTriggerCount = triggerCount;
            });

            var result = await _host.With<ITextRoomAdminService, Response<TextRoomStaffViewDto>>(svc =>
                svc.CreateRoomAsync(dto, admin));

            Assert.Equal(shouldSucceed, result.Succeeded);
            if (!shouldSucceed)
                Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        }

        [Theory]
        [InlineData(0, false)]
        [InlineData(1, true)]
        [InlineData(20, true)]
        [InlineData(21, false)]
        public async Task CreateRoom_MaxOverflowInstancesBoundaries_EnforcesLimits(int maxOverflow, bool shouldSucceed)
        {
            var admin = await CreateAdminActorAsync();
            var dto = TextRoomTestFixtures.ValidRoomDto(configureSettings: s =>
            {
                s.MaxParticipants = 10;
                s.AutoOverflowEnabled = true;
                s.MaxOverflowInstances = maxOverflow;
            });

            var result = await _host.With<ITextRoomAdminService, Response<TextRoomStaffViewDto>>(svc =>
                svc.CreateRoomAsync(dto, admin));

            Assert.Equal(shouldSucceed, result.Succeeded);
            if (!shouldSucceed)
                Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        }

        [Fact]
        public async Task CreateRoom_AutoOverflowWithoutCapOrTrigger_ReturnsBadRequest()
        {
            var admin = await CreateAdminActorAsync();
            var dto = TextRoomTestFixtures.ValidRoomDto(configureSettings: s =>
            {
                s.AutoOverflowEnabled = true;
                s.MaxParticipants = null;
                s.OverflowTriggerCount = null;
            });

            var result = await _host.With<ITextRoomAdminService, Response<TextRoomStaffViewDto>>(svc =>
                svc.CreateRoomAsync(dto, admin));

            Assert.False(result.Succeeded);
            Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        }

        [Theory]
        [InlineData(-1, false)]
        [InlineData(0, true)]
        [InlineData(3600, true)]
        [InlineData(3601, false)]
        public async Task CreateRoom_SlowModeSecondsBoundaries_EnforcesLimits(int slowModeSeconds, bool shouldSucceed)
        {
            var admin = await CreateAdminActorAsync();
            var dto = TextRoomTestFixtures.ValidRoomDto(configureSettings: s =>
                s.SlowModeSeconds = slowModeSeconds);

            var result = await _host.With<ITextRoomAdminService, Response<TextRoomStaffViewDto>>(svc =>
                svc.CreateRoomAsync(dto, admin));

            Assert.Equal(shouldSucceed, result.Succeeded);
            if (!shouldSucceed)
                Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        }

        [Theory]
        [InlineData(0, false)]
        [InlineData(1, true)]
        [InlineData(4000, true)]
        [InlineData(4001, false)]
        public async Task CreateRoom_MaxMessageLengthBoundaries_EnforcesLimits(int maxLength, bool shouldSucceed)
        {
            var admin = await CreateAdminActorAsync();
            var dto = TextRoomTestFixtures.ValidRoomDto(configureSettings: s =>
                s.MaxMessageLength = maxLength);

            var result = await _host.With<ITextRoomAdminService, Response<TextRoomStaffViewDto>>(svc =>
                svc.CreateRoomAsync(dto, admin));

            Assert.Equal(shouldSucceed, result.Succeeded);
            if (!shouldSucceed)
                Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        }

        [Theory]
        [InlineData(0, false)]
        [InlineData(1, true)]
        [InlineData(8760, true)]
        [InlineData(8761, false)]
        public async Task CreateRoom_MessageRetentionHoursBoundaries_EnforcesLimits(int retentionHours, bool shouldSucceed)
        {
            var admin = await CreateAdminActorAsync();
            var dto = TextRoomTestFixtures.ValidRoomDto(configureSettings: s =>
                s.MessageRetentionHours = retentionHours);

            var result = await _host.With<ITextRoomAdminService, Response<TextRoomStaffViewDto>>(svc =>
                svc.CreateRoomAsync(dto, admin));

            Assert.Equal(shouldSucceed, result.Succeeded);
            if (!shouldSucceed)
                Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        }

        [Theory]
        [InlineData(-1, 10, false)]
        [InlineData(101, 10, false)]
        [InlineData(10, -1, false)]
        [InlineData(10, 101, false)]
        [InlineData(0, 0, true)]
        [InlineData(100, 100, true)]
        public async Task CreateRoom_ReportContextWindowBoundaries_EnforcesLimits(
            int before, int after, bool shouldSucceed)
        {
            var admin = await CreateAdminActorAsync();
            var dto = TextRoomTestFixtures.ValidRoomDto(configureSettings: s =>
            {
                s.ReportContextMessagesBefore = before;
                s.ReportContextMessagesAfter = after;
            });

            var result = await _host.With<ITextRoomAdminService, Response<TextRoomStaffViewDto>>(svc =>
                svc.CreateRoomAsync(dto, admin));

            Assert.Equal(shouldSucceed, result.Succeeded);
            if (!shouldSucceed)
                Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        }

        [Fact]
        public async Task CreateSchedule_EmptyTopicPool_ReturnsBadRequest()
        {
            var admin = await CreateAdminActorAsync();
            var dto = TextRoomTestFixtures.ValidScheduleDto(configure: s =>
                s.TopicPool = new List<TextRoomTopicDto>());

            var result = await _host.With<ITextRoomAdminService, Response<TextRoomScheduleDto>>(svc =>
                svc.CreateScheduleAsync(dto, admin));

            Assert.False(result.Succeeded);
            Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        }

        [Theory]
        [InlineData(540, null)]
        [InlineData(null, 1020)]
        public async Task CreateSchedule_OnlyOneWindowBoundSet_ReturnsBadRequest(int? startMinute, int? endMinute)
        {
            var admin = await CreateAdminActorAsync();
            var dto = TextRoomTestFixtures.ValidScheduleDto(configure: s =>
            {
                s.WindowStartMinuteOfDay = startMinute;
                s.WindowEndMinuteOfDay = endMinute;
            });

            var result = await _host.With<ITextRoomAdminService, Response<TextRoomScheduleDto>>(svc =>
                svc.CreateScheduleAsync(dto, admin));

            Assert.False(result.Succeeded);
            Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        }

        [Theory]
        [InlineData(4, false)]
        [InlineData(5, true)]
        [InlineData(10080, true)]
        [InlineData(10081, false)]
        public async Task CreateSchedule_SpawnEveryMinutesBoundaries_EnforcesLimits(int spawnEveryMinutes, bool shouldSucceed)
        {
            var admin = await CreateAdminActorAsync();
            var dto = TextRoomTestFixtures.ValidScheduleDto(configure: s =>
                s.SpawnEveryMinutes = spawnEveryMinutes);

            var result = await _host.With<ITextRoomAdminService, Response<TextRoomScheduleDto>>(svc =>
                svc.CreateScheduleAsync(dto, admin));

            Assert.Equal(shouldSucceed, result.Succeeded);
            if (!shouldSucceed)
                Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        }

        [Fact]
        public async Task CreateRoom_OpensAtTwoMinutesInThePast_ReturnsBadRequest()
        {
            var admin = await CreateAdminActorAsync();
            var dto = TextRoomTestFixtures.ValidRoomDto(configure: d =>
                d.OpensAt = _host.Clock.UtcNow.AddMinutes(-2));

            var result = await _host.With<ITextRoomAdminService, Response<TextRoomStaffViewDto>>(svc =>
                svc.CreateRoomAsync(dto, admin));

            Assert.False(result.Succeeded);
            Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        }

        [Fact]
        public async Task CreateRoom_FutureOpensAt_CreatesScheduledRoom()
        {
            var admin = await CreateAdminActorAsync();
            var opensAt = _host.Clock.UtcNow.AddMinutes(30);
            var dto = TextRoomTestFixtures.ValidRoomDto(configure: d => d.OpensAt = opensAt);

            var result = await _host.With<ITextRoomAdminService, Response<TextRoomStaffViewDto>>(svc =>
                svc.CreateRoomAsync(dto, admin));

            Assert.True(result.Succeeded);
            Assert.Equal(TextRoomStatus.Scheduled.ToString(), result.Data!.Status);
            Assert.Equal(opensAt, result.Data.OpensAt);
        }

        [Theory]
        [InlineData("غرفة \u200Fالنقاش 🌍🔥\u200E اليوم")]
        [InlineData("<script>alert('xss')</script>")]
        public async Task CreateRoom_HostileAndUnicodeTitles_PersistVerbatimRoundTrip(string title)
        {
            var admin = await CreateAdminActorAsync();
            var created = await TextRoomTestFixtures.CreateRoomAsync(_host, admin, TextRoomTestFixtures.ValidRoomDto(title));

            Assert.Equal(title, created.Title);

            var fetched = await _host.With<ITextRoomAdminService, Response<TextRoomStaffViewDto>>(svc =>
                svc.GetRoomAsync(created.RoomId));
            Assert.True(fetched.Succeeded);
            Assert.Equal(title, fetched.Data!.Title);

            var persistedTitle = await _host.WithDb(db =>
                db.TextRooms.AsNoTracking()
                    .Where(r => r.Id == created.RoomId)
                    .Select(r => r.Title)
                    .SingleAsync());
            Assert.Equal(title, persistedTitle);
        }
    }
}
