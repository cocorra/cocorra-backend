using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Cocorra.BLL.Base;
using Cocorra.BLL.Services.TextRooms;
using Cocorra.DAL.DTOS.TextRoomDto;
using Cocorra.DAL.Models;
using Cocorra.Tests.Helpers;
using Xunit;

namespace Cocorra.Tests.TextRooms
{
    public class TextRoomVisibilityTests : IDisposable
    {
        private readonly TextRoomTestHost _host = new();

        public void Dispose() => _host.Dispose();

        [Fact]
        public async Task ShowOnlineCountFalse_RegularUserGetsNullCountsWhileStaffReceivesNumbers()
        {
            var admin = await _host.CreateUserAsync(AppRoles.Admin);
            var member = await _host.CreateUserAsync(AppRoles.User);
            var viewer = await _host.CreateUserAsync(AppRoles.User);
            var adminActor = TextRoomTestHost.AdminActor(admin);

            var room = await TextRoomTestFixtures.CreateRoomAsync(
                _host,
                adminActor,
                TextRoomTestFixtures.ValidRoomDto(configureSettings: s => s.ShowOnlineCountToUsers = false));

            await TextRoomTestFixtures.JoinAndGoOnlineAsync(_host, room.RoomId, member.Id, "conn-member");

            var userList = await _host.With<ITextRoomService, Response<List<TextRoomListItemDto>>>(svc =>
                svc.GetActiveRoomsAsync(viewer.Id, viewerIsStaff: false));
            var userListItem = Assert.Single(userList.Data!, r => r.RoomId == room.RoomId);
            Assert.Null(userListItem.OnlineCount);
            Assert.Null(userListItem.TotalParticipants);

            var userConfig = await _host.With<ITextRoomService, Response<TextRoomClientConfigDto>>(svc =>
                svc.GetRoomConfigAsync(room.RoomId, viewer.Id, viewerIsStaff: false));
            Assert.True(userConfig.Succeeded);
            Assert.False(userConfig.Data!.ShowOnlineCount);
            Assert.Null(userConfig.Data.OnlineCount);
            Assert.Null(userConfig.Data.TotalParticipants);

            var staffList = await _host.With<ITextRoomService, Response<List<TextRoomListItemDto>>>(svc =>
                svc.GetActiveRoomsAsync(admin.Id, viewerIsStaff: true));
            var staffListItem = Assert.Single(staffList.Data!, r => r.RoomId == room.RoomId);
            Assert.Equal(1, staffListItem.OnlineCount);
            Assert.Equal(1, staffListItem.TotalParticipants);

            var staffConfig = await _host.With<ITextRoomService, Response<TextRoomClientConfigDto>>(svc =>
                svc.GetRoomConfigAsync(room.RoomId, admin.Id, viewerIsStaff: true));
            Assert.True(staffConfig.Succeeded);
            Assert.Equal(1, staffConfig.Data!.OnlineCount);
            Assert.Equal(1, staffConfig.Data.TotalParticipants);

            var userJoin = await _host.With<ITextRoomService, TextRoomJoinResultDto>(svc =>
                svc.JoinAsync(room.RoomId, viewer.Id, viewerIsStaff: false, TextRoomTestFixtures.NewConnectionId()));
            Assert.Equal(TextRoomJoinStatus.Joined, userJoin.Status);
            Assert.Null(userJoin.Config!.OnlineCount);
            Assert.Null(userJoin.Config.TotalParticipants);

            var staffJoin = await _host.With<ITextRoomService, TextRoomJoinResultDto>(svc =>
                svc.JoinAsync(room.RoomId, admin.Id, viewerIsStaff: true, TextRoomTestFixtures.NewConnectionId()));
            Assert.Equal(TextRoomJoinStatus.Joined, staffJoin.Status);
            // member, viewer and the admin are all online after their joins.
            Assert.Equal(3, staffJoin.Config!.OnlineCount);
            Assert.Equal(3, staffJoin.Config.TotalParticipants);
        }

        [Fact]
        public async Task UpdateRoom_EnablingShowOnlineCount_ExposesCountsToUsersAndBroadcastsIncrementedConfigVersion()
        {
            var admin = await _host.CreateUserAsync(AppRoles.Admin);
            var member = await _host.CreateUserAsync(AppRoles.User);
            var viewer = await _host.CreateUserAsync(AppRoles.User);
            var adminActor = TextRoomTestHost.AdminActor(admin);

            var room = await TextRoomTestFixtures.CreateRoomAsync(
                _host,
                adminActor,
                TextRoomTestFixtures.ValidRoomDto(configureSettings: s => s.ShowOnlineCountToUsers = false));
            var initialVersion = room.ConfigVersion;

            await TextRoomTestFixtures.JoinAndGoOnlineAsync(_host, room.RoomId, member.Id, "conn-member");

            var updated = await _host.With<ITextRoomAdminService, Response<TextRoomStaffViewDto>>(svc =>
                svc.UpdateRoomAsync(
                    room.RoomId,
                    TextRoomTestFixtures.ValidRoomDto(configureSettings: s => s.ShowOnlineCountToUsers = true),
                    adminActor));

            Assert.True(updated.Succeeded);
            Assert.Equal(initialVersion + 1, updated.Data!.ConfigVersion);

            var broadcasts = _host.Broadcaster.CallsTo(nameof(ITextRoomBroadcaster.ConfigUpdatedAsync));
            var broadcastConfig = Assert.Single(
                broadcasts.Select(args => Assert.IsType<TextRoomClientConfigDto>(args[0])),
                c => c.RoomId == room.RoomId);
            Assert.Equal(initialVersion + 1, broadcastConfig.ConfigVersion);
            Assert.True(broadcastConfig.ShowOnlineCount);
            Assert.Equal(1, broadcastConfig.OnlineCount);
            Assert.Equal(1, broadcastConfig.TotalParticipants);

            var userList = await _host.With<ITextRoomService, Response<List<TextRoomListItemDto>>>(svc =>
                svc.GetActiveRoomsAsync(viewer.Id, viewerIsStaff: false));
            var listItem = Assert.Single(userList.Data!, r => r.RoomId == room.RoomId);
            Assert.Equal(1, listItem.OnlineCount);
            Assert.Equal(1, listItem.TotalParticipants);

            var userConfig = await _host.With<ITextRoomService, Response<TextRoomClientConfigDto>>(svc =>
                svc.GetRoomConfigAsync(room.RoomId, viewer.Id, viewerIsStaff: false));
            Assert.True(userConfig.Data!.ShowOnlineCount);
            Assert.Equal(1, userConfig.Data.OnlineCount);
            Assert.Equal(1, userConfig.Data.TotalParticipants);

            var userJoin = await _host.With<ITextRoomService, TextRoomJoinResultDto>(svc =>
                svc.JoinAsync(room.RoomId, viewer.Id, viewerIsStaff: false, TextRoomTestFixtures.NewConnectionId()));
            Assert.Equal(TextRoomJoinStatus.Joined, userJoin.Status);
            Assert.Equal(2, userJoin.Config!.OnlineCount);
            Assert.Equal(2, userJoin.Config.TotalParticipants);
        }
    }
}
