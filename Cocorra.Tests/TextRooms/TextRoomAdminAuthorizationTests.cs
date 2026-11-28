using System;
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
    public class TextRoomAdminAuthorizationTests : IDisposable
    {
        private readonly TextRoomTestHost _host = new();

        public void Dispose() => _host.Dispose();

        [Theory]
        [InlineData("CreateRoom")]
        [InlineData("UpdateRoom")]
        [InlineData("CloseRoom")]
        [InlineData("CreateSchedule")]
        [InlineData("UpdateSchedule")]
        [InlineData("SetScheduleEnabled")]
        [InlineData("DeleteSchedule")]
        public async Task ModeratorMutations_WhenAllowModeratorsToManageRoomsFalse_ReturnForbidden(string operation)
        {
            var admin = await _host.CreateUserAsync(AppRoles.Admin);
            var moderator = await _host.CreateUserAsync(AppRoles.Moderator);
            var adminActor = TextRoomTestHost.AdminActor(admin);
            var modActor = TextRoomTestHost.ModeratorActor(moderator);

            var room = await TextRoomTestFixtures.CreateRoomAsync(_host, adminActor);
            var schedule = await TextRoomTestFixtures.CreateScheduleAsync(_host, adminActor);
            var auditCountBefore = _host.Audit.Entries.Count;

            HttpStatusCode statusCode;
            bool succeeded;

            switch (operation)
            {
                case "CreateRoom":
                    var createRoom = await _host.With<ITextRoomAdminService, Response<TextRoomStaffViewDto>>(svc =>
                        svc.CreateRoomAsync(TextRoomTestFixtures.ValidRoomDto("Mod Attempt"), modActor));
                    (succeeded, statusCode) = (createRoom.Succeeded, createRoom.StatusCode);
                    break;
                case "UpdateRoom":
                    var updateRoom = await _host.With<ITextRoomAdminService, Response<TextRoomStaffViewDto>>(svc =>
                        svc.UpdateRoomAsync(room.RoomId, TextRoomTestFixtures.ValidRoomDto("Updated By Mod"), modActor));
                    (succeeded, statusCode) = (updateRoom.Succeeded, updateRoom.StatusCode);
                    break;
                case "CloseRoom":
                    var closeRoom = await _host.With<ITextRoomAdminService, Response<string>>(svc =>
                        svc.CloseRoomAsync(room.RoomId, "Closing attempt", modActor));
                    (succeeded, statusCode) = (closeRoom.Succeeded, closeRoom.StatusCode);
                    break;
                case "CreateSchedule":
                    var createSched = await _host.With<ITextRoomAdminService, Response<TextRoomScheduleDto>>(svc =>
                        svc.CreateScheduleAsync(TextRoomTestFixtures.ValidScheduleDto("Mod Schedule"), modActor));
                    (succeeded, statusCode) = (createSched.Succeeded, createSched.StatusCode);
                    break;
                case "UpdateSchedule":
                    var updateSched = await _host.With<ITextRoomAdminService, Response<TextRoomScheduleDto>>(svc =>
                        svc.UpdateScheduleAsync(schedule.Id, TextRoomTestFixtures.ValidScheduleDto("Updated Schedule"), modActor));
                    (succeeded, statusCode) = (updateSched.Succeeded, updateSched.StatusCode);
                    break;
                case "SetScheduleEnabled":
                    var toggleSched = await _host.With<ITextRoomAdminService, Response<TextRoomScheduleDto>>(svc =>
                        svc.SetScheduleEnabledAsync(schedule.Id, false, modActor));
                    (succeeded, statusCode) = (toggleSched.Succeeded, toggleSched.StatusCode);
                    break;
                case "DeleteSchedule":
                    var deleteSched = await _host.With<ITextRoomAdminService, Response<string>>(svc =>
                        svc.DeleteScheduleAsync(schedule.Id, modActor));
                    (succeeded, statusCode) = (deleteSched.Succeeded, deleteSched.StatusCode);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(operation), operation, null);
            }

            Assert.False(succeeded);
            Assert.Equal(HttpStatusCode.Forbidden, statusCode);
            Assert.Equal(auditCountBefore, _host.Audit.Entries.Count);
        }

        [Fact]
        public async Task ModeratorRoomManagement_AfterAdminEnablesFlag_AllowsCreateUpdateAndClose()
        {
            var admin = await _host.CreateUserAsync(AppRoles.Admin);
            var moderator = await _host.CreateUserAsync(AppRoles.Moderator);
            var adminActor = TextRoomTestHost.AdminActor(admin);
            var modActor = TextRoomTestHost.ModeratorActor(moderator);

            var enableResult = await _host.With<ITextRoomAdminService, Response<TextRoomGlobalSettingsDto>>(svc =>
                svc.UpdateGlobalSettingsAsync(
                    TextRoomTestFixtures.ValidGlobalSettingsDto(g => g.AllowModeratorsToManageRooms = true),
                    adminActor));
            Assert.True(enableResult.Succeeded);

            var created = await _host.With<ITextRoomAdminService, Response<TextRoomStaffViewDto>>(svc =>
                svc.CreateRoomAsync(TextRoomTestFixtures.ValidRoomDto("Mod Created Room"), modActor));
            Assert.True(created.Succeeded);
            Assert.NotNull(created.Data);

            var updated = await _host.With<ITextRoomAdminService, Response<TextRoomStaffViewDto>>(svc =>
                svc.UpdateRoomAsync(created.Data!.RoomId, TextRoomTestFixtures.ValidRoomDto("Mod Updated Room"), modActor));
            Assert.True(updated.Succeeded);
            Assert.Equal("Mod Updated Room", updated.Data!.Title);

            var closed = await _host.With<ITextRoomAdminService, Response<string>>(svc =>
                svc.CloseRoomAsync(created.Data!.RoomId, "Done for today", modActor));
            Assert.True(closed.Succeeded);
        }

        [Fact]
        public async Task ModeratorScheduleCrud_AfterAdminEnablesFlag_AllowsCreateUpdateToggleAndDelete()
        {
            var admin = await _host.CreateUserAsync(AppRoles.Admin);
            var moderator = await _host.CreateUserAsync(AppRoles.Moderator);
            var adminActor = TextRoomTestHost.AdminActor(admin);
            var modActor = TextRoomTestHost.ModeratorActor(moderator);

            var enableResult = await _host.With<ITextRoomAdminService, Response<TextRoomGlobalSettingsDto>>(svc =>
                svc.UpdateGlobalSettingsAsync(
                    TextRoomTestFixtures.ValidGlobalSettingsDto(g => g.AllowModeratorsToManageRooms = true),
                    adminActor));
            Assert.True(enableResult.Succeeded);

            var created = await _host.With<ITextRoomAdminService, Response<TextRoomScheduleDto>>(svc =>
                svc.CreateScheduleAsync(TextRoomTestFixtures.ValidScheduleDto("Mod Schedule"), modActor));
            Assert.True(created.Succeeded);
            Assert.NotNull(created.Data);

            var updated = await _host.With<ITextRoomAdminService, Response<TextRoomScheduleDto>>(svc =>
                svc.UpdateScheduleAsync(created.Data!.Id, TextRoomTestFixtures.ValidScheduleDto("Mod Schedule V2"), modActor));
            Assert.True(updated.Succeeded);
            Assert.Equal("Mod Schedule V2", updated.Data!.Name);

            var toggled = await _host.With<ITextRoomAdminService, Response<TextRoomScheduleDto>>(svc =>
                svc.SetScheduleEnabledAsync(created.Data!.Id, false, modActor));
            Assert.True(toggled.Succeeded);
            Assert.False(toggled.Data!.IsEnabled);

            var deleted = await _host.With<ITextRoomAdminService, Response<string>>(svc =>
                svc.DeleteScheduleAsync(created.Data!.Id, modActor));
            Assert.True(deleted.Succeeded);
        }

        [Fact]
        public async Task ModeratorUpdateGlobalSettings_EvenWhenFlagEnabled_ReturnsForbidden()
        {
            var admin = await _host.CreateUserAsync(AppRoles.Admin);
            var moderator = await _host.CreateUserAsync(AppRoles.Moderator);
            var adminActor = TextRoomTestHost.AdminActor(admin);
            var modActor = TextRoomTestHost.ModeratorActor(moderator);

            var enableResult = await _host.With<ITextRoomAdminService, Response<TextRoomGlobalSettingsDto>>(svc =>
                svc.UpdateGlobalSettingsAsync(
                    TextRoomTestFixtures.ValidGlobalSettingsDto(g =>
                    {
                        g.AllowModeratorsToManageRooms = true;
                        g.DefaultMessageRetentionHours = 96;
                    }),
                    adminActor));
            Assert.True(enableResult.Succeeded);

            var modAttempt = await _host.With<ITextRoomAdminService, Response<TextRoomGlobalSettingsDto>>(svc =>
                svc.UpdateGlobalSettingsAsync(
                    TextRoomTestFixtures.ValidGlobalSettingsDto(g => g.DefaultMessageRetentionHours = 12),
                    modActor));

            Assert.False(modAttempt.Succeeded);
            Assert.Equal(HttpStatusCode.Forbidden, modAttempt.StatusCode);

            var current = await _host.With<ITextRoomSettingsProvider, TextRoomGlobalSettings>(p => p.GetAsync());
            Assert.Equal(96, current.DefaultMessageRetentionHours);
        }

        [Fact]
        public async Task AdminUpdateGlobalSettings_SucceedsAndInvalidatesSettingsProviderCache()
        {
            var admin = await _host.CreateUserAsync(AppRoles.Admin);
            var adminActor = TextRoomTestHost.AdminActor(admin);

            // Populate the settings provider cache before mutating so stale cache entries would be caught.
            var initial = await _host.With<ITextRoomSettingsProvider, TextRoomGlobalSettings>(p => p.GetAsync());
            Assert.Equal(72, initial.DefaultMessageRetentionHours);
            Assert.False(initial.AllowModeratorsToManageRooms);

            var updatedDto = TextRoomTestFixtures.ValidGlobalSettingsDto(g =>
            {
                g.DefaultMessageRetentionHours = 144;
                g.ReportContextMessagesBefore = 12;
                g.ReportContextMessagesAfter = 18;
                g.AllowModeratorsToManageRooms = true;
                g.Defaults.MaxMessageLength = 800;
            });

            var result = await _host.With<ITextRoomAdminService, Response<TextRoomGlobalSettingsDto>>(svc =>
                svc.UpdateGlobalSettingsAsync(updatedDto, adminActor));

            Assert.True(result.Succeeded);

            var reloaded = await _host.With<ITextRoomSettingsProvider, TextRoomGlobalSettings>(p => p.GetAsync());
            Assert.Equal(144, reloaded.DefaultMessageRetentionHours);
            Assert.Equal(12, reloaded.ReportContextMessagesBefore);
            Assert.Equal(18, reloaded.ReportContextMessagesAfter);
            Assert.True(reloaded.AllowModeratorsToManageRooms);
            Assert.Equal(800, reloaded.Defaults.MaxMessageLength);
        }

        [Fact]
        public async Task GetOverview_DefaultSettings_CallerCanManageRoomsFalseForModeratorAndTrueForAdmin()
        {
            var admin = await _host.CreateUserAsync(AppRoles.Admin);
            var moderator = await _host.CreateUserAsync(AppRoles.Moderator);

            var modOverview = await _host.With<ITextRoomAdminService, Response<TextRoomOverviewDto>>(svc =>
                svc.GetOverviewAsync(TextRoomTestHost.ModeratorActor(moderator)));
            var adminOverview = await _host.With<ITextRoomAdminService, Response<TextRoomOverviewDto>>(svc =>
                svc.GetOverviewAsync(TextRoomTestHost.AdminActor(admin)));

            Assert.True(modOverview.Succeeded);
            Assert.False(modOverview.Data!.CallerCanManageRooms);

            Assert.True(adminOverview.Succeeded);
            Assert.True(adminOverview.Data!.CallerCanManageRooms);
        }

        [Fact]
        public async Task GetOverview_AfterAdminEnablesFlag_CallerCanManageRoomsTrueForModerator()
        {
            var admin = await _host.CreateUserAsync(AppRoles.Admin);
            var moderator = await _host.CreateUserAsync(AppRoles.Moderator);

            var update = await _host.With<ITextRoomAdminService, Response<TextRoomGlobalSettingsDto>>(svc =>
                svc.UpdateGlobalSettingsAsync(
                    TextRoomTestFixtures.ValidGlobalSettingsDto(g => g.AllowModeratorsToManageRooms = true),
                    TextRoomTestHost.AdminActor(admin)));
            Assert.True(update.Succeeded);

            var modOverview = await _host.With<ITextRoomAdminService, Response<TextRoomOverviewDto>>(svc =>
                svc.GetOverviewAsync(TextRoomTestHost.ModeratorActor(moderator)));

            Assert.True(modOverview.Succeeded);
            Assert.True(modOverview.Data!.CallerCanManageRooms);
        }
    }
}
