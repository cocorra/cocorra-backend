using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Cocorra.BLL.Base;
using Cocorra.BLL.Services.TextRooms;
using Cocorra.DAL.DTOS.TextRoomDto;
using Cocorra.DAL.Models;
using Cocorra.Tests.Helpers;
using Xunit;

namespace Cocorra.Tests.TextRooms
{
    public class TextRoomAuditTests : IDisposable
    {
        private readonly TextRoomTestHost _host = new();

        public void Dispose() => _host.Dispose();

        [Fact]
        public async Task RoomLifecycleMutations_EachWriteExactlyOneAuditRowWithExpectedMetadata()
        {
            var admin = await _host.CreateUserAsync(AppRoles.Admin);
            var adminActor = TextRoomTestHost.AdminActor(admin);

            var created = await TextRoomTestFixtures.CreateRoomAsync(_host, adminActor);
            var createEntry = Assert.Single(_host.Audit.ByAction(AuditActions.TextRoomCreated));
            Assert.Equal(AuditCategories.TextRoom, createEntry.Category);
            Assert.Equal(AuditEntityTypes.TextRoom, createEntry.EntityType);
            Assert.Equal(created.RoomId.ToString(), createEntry.EntityId);
            Assert.Equal(admin.Id, createEntry.ActorUserId);

            var update = await _host.With<ITextRoomAdminService, Response<TextRoomStaffViewDto>>(svc =>
                svc.UpdateRoomAsync(created.RoomId, TextRoomTestFixtures.ValidRoomDto("Renamed Room"), adminActor));
            Assert.True(update.Succeeded);
            var updateEntry = Assert.Single(_host.Audit.ByAction(AuditActions.TextRoomUpdated));
            Assert.Equal(AuditCategories.TextRoom, updateEntry.Category);
            Assert.Equal(AuditEntityTypes.TextRoom, updateEntry.EntityType);
            Assert.Equal(created.RoomId.ToString(), updateEntry.EntityId);
            Assert.Equal(admin.Id, updateEntry.ActorUserId);

            var close = await _host.With<ITextRoomAdminService, Response<string>>(svc =>
                svc.CloseRoomAsync(created.RoomId, "Wrapping up", adminActor));
            Assert.True(close.Succeeded);
            var closeEntry = Assert.Single(_host.Audit.ByAction(AuditActions.TextRoomClosed));
            Assert.Equal(AuditCategories.TextRoom, closeEntry.Category);
            Assert.Equal(AuditEntityTypes.TextRoom, closeEntry.EntityType);
            Assert.Equal(created.RoomId.ToString(), closeEntry.EntityId);
            Assert.Equal(admin.Id, closeEntry.ActorUserId);

            Assert.Equal(3, _host.Audit.Entries.Count);
        }

        [Fact]
        public async Task ScheduleMutations_CreateUpdateToggleAndDelete_EachWriteExactlyOneAuditRow()
        {
            var admin = await _host.CreateUserAsync(AppRoles.Admin);
            var adminActor = TextRoomTestHost.AdminActor(admin);

            var schedule = await TextRoomTestFixtures.CreateScheduleAsync(_host, adminActor);

            var updated = await _host.With<ITextRoomAdminService, Response<TextRoomScheduleDto>>(svc =>
                svc.UpdateScheduleAsync(schedule.Id, TextRoomTestFixtures.ValidScheduleDto("Updated Schedule"), adminActor));
            Assert.True(updated.Succeeded);

            var disabled = await _host.With<ITextRoomAdminService, Response<TextRoomScheduleDto>>(svc =>
                svc.SetScheduleEnabledAsync(schedule.Id, false, adminActor));
            Assert.True(disabled.Succeeded);

            var enabled = await _host.With<ITextRoomAdminService, Response<TextRoomScheduleDto>>(svc =>
                svc.SetScheduleEnabledAsync(schedule.Id, true, adminActor));
            Assert.True(enabled.Succeeded);

            var deleted = await _host.With<ITextRoomAdminService, Response<string>>(svc =>
                svc.DeleteScheduleAsync(schedule.Id, adminActor));
            Assert.True(deleted.Succeeded);

            var scheduleEntries = _host.Audit.ByAction(AuditActions.TextRoomScheduleChanged);
            Assert.Equal(5, scheduleEntries.Count);
            Assert.Equal(5, _host.Audit.Entries.Count);

            Assert.All(scheduleEntries, entry =>
            {
                Assert.Equal(AuditCategories.TextRoom, entry.Category);
                Assert.Equal(AuditEntityTypes.TextRoomSchedule, entry.EntityType);
                Assert.Equal(schedule.Id.ToString(), entry.EntityId);
                Assert.Equal(admin.Id, entry.ActorUserId);
            });

            Assert.Equal(
                new[] { "Created", "Updated", "Disabled", "Enabled", "Deleted" },
                scheduleEntries.Select(e => e.ToState).ToArray());
        }

        [Fact]
        public async Task UpdateGlobalSettings_WritesExactlyOneSettingsAuditRow()
        {
            var admin = await _host.CreateUserAsync(AppRoles.Admin);
            var adminActor = TextRoomTestHost.AdminActor(admin);

            var result = await _host.With<ITextRoomAdminService, Response<TextRoomGlobalSettingsDto>>(svc =>
                svc.UpdateGlobalSettingsAsync(
                    TextRoomTestFixtures.ValidGlobalSettingsDto(g => g.DefaultMessageRetentionHours = 48),
                    adminActor));

            Assert.True(result.Succeeded);
            var entry = Assert.Single(_host.Audit.ByAction(AuditActions.TextRoomSettingsChanged));
            Assert.Equal(AuditCategories.TextRoom, entry.Category);
            Assert.Equal(AuditEntityTypes.TextRoomSettings, entry.EntityType);
            Assert.False(string.IsNullOrWhiteSpace(entry.EntityId));
            Assert.Equal(admin.Id, entry.ActorUserId);
        }

        [Fact]
        public async Task FailedValidationMutations_WriteNoAuditRows()
        {
            var admin = await _host.CreateUserAsync(AppRoles.Admin);
            var adminActor = TextRoomTestHost.AdminActor(admin);

            var badRoom = await _host.With<ITextRoomAdminService, Response<TextRoomStaffViewDto>>(svc =>
                svc.CreateRoomAsync(TextRoomTestFixtures.ValidRoomDto("ab"), adminActor));
            Assert.Equal(HttpStatusCode.BadRequest, badRoom.StatusCode);

            var badSchedule = await _host.With<ITextRoomAdminService, Response<TextRoomScheduleDto>>(svc =>
                svc.CreateScheduleAsync(
                    TextRoomTestFixtures.ValidScheduleDto(configure: s => s.TopicPool = new List<TextRoomTopicDto>()),
                    adminActor));
            Assert.Equal(HttpStatusCode.BadRequest, badSchedule.StatusCode);

            var badSettings = await _host.With<ITextRoomAdminService, Response<TextRoomGlobalSettingsDto>>(svc =>
                svc.UpdateGlobalSettingsAsync(
                    TextRoomTestFixtures.ValidGlobalSettingsDto(g => g.DefaultMessageRetentionHours = 0),
                    adminActor));
            Assert.Equal(HttpStatusCode.BadRequest, badSettings.StatusCode);

            Assert.Empty(_host.Audit.Entries);
        }

        [Fact]
        public async Task GetPresets_ReturnsExactFourDocumentedKeysAndDurations()
        {
            var admin = await _host.CreateUserAsync(AppRoles.Admin);
            var adminActor = TextRoomTestHost.AdminActor(admin);

            var presets = await _host.With<ITextRoomAdminService, IReadOnlyList<TextRoomPresetDto>>(svc =>
                Task.FromResult(svc.GetPresets()));

            Assert.Equal(4, presets.Count);
            Assert.Equal(
                new[] { "always-on-open", "daily-12h", "flash-1h", "lounge-2h" },
                presets.Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal).ToArray());

            var byKey = presets.ToDictionary(p => p.Key, StringComparer.Ordinal);

            Assert.True(byKey["always-on-open"].Settings.IsAlwaysOn);
            Assert.Equal(720, byKey["always-on-open"].Settings.RotateTopicEveryMinutes);
            Assert.False(byKey["always-on-open"].Settings.ClearHistoryOnRotate);

            Assert.False(byKey["flash-1h"].Settings.IsAlwaysOn);
            Assert.Equal(60, byKey["flash-1h"].Settings.DurationMinutes);
            Assert.Equal(3, byKey["flash-1h"].Settings.SlowModeSeconds);

            Assert.False(byKey["lounge-2h"].Settings.IsAlwaysOn);
            Assert.Equal(120, byKey["lounge-2h"].Settings.DurationMinutes);

            Assert.False(byKey["daily-12h"].Settings.IsAlwaysOn);
            Assert.Equal(720, byKey["daily-12h"].Settings.DurationMinutes);

            // Every preset's settings must be accepted as-is by room creation validation.
            foreach (var preset in presets)
            {
                var created = await _host.With<ITextRoomAdminService, Response<TextRoomStaffViewDto>>(svc =>
                    svc.CreateRoomAsync(
                        new TextRoomUpsertDto
                        {
                            Title = $"Room from {preset.Key}",
                            Settings = preset.Settings
                        },
                        adminActor));
                Assert.True(created.Succeeded, $"Preset '{preset.Key}' failed validation: {created.Message}");
            }
        }
    }
}
