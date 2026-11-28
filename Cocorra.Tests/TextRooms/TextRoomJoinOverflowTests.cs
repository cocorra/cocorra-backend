using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Cocorra.BLL.Base;
using Cocorra.BLL.Services.TextRooms;
using Cocorra.DAL.DTOS.TextRoomDto;
using Cocorra.DAL.Models;
using Cocorra.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cocorra.Tests.TextRooms
{
    public class TextRoomJoinOverflowTests : IDisposable
    {
        private readonly TextRoomTestHost _host = new();

        public void Dispose() => _host.Dispose();

        [Fact]
        public async Task Join_FullRoomWithoutOverflow_ReturnsFullForNewUser_JoinedForStaffAndOnlineUser()
        {
            var admin = await _host.CreateUserAsync(AppRoles.Admin);
            var u1 = await _host.CreateUserAsync(AppRoles.User);
            var u2 = await _host.CreateUserAsync(AppRoles.User);
            var u3 = await _host.CreateUserAsync(AppRoles.User);

            var room = await TextRoomTestFixtures.CreateRoomAsync(
                _host,
                TextRoomTestHost.AdminActor(admin),
                TextRoomTestFixtures.ValidRoomDto(configureSettings: s =>
                {
                    s.MaxParticipants = 2;
                    s.AutoOverflowEnabled = false;
                }));

            await TextRoomTestFixtures.JoinAndGoOnlineAsync(_host, room.RoomId, u1.Id, "c1");
            await TextRoomTestFixtures.JoinAndGoOnlineAsync(_host, room.RoomId, u2.Id, "c2");

            var outsiderJoin = await _host.With<ITextRoomService, TextRoomJoinResultDto>(svc =>
                svc.JoinAsync(room.RoomId, u3.Id, viewerIsStaff: false, TextRoomTestFixtures.NewConnectionId()));
            Assert.Equal(TextRoomJoinStatus.Full, outsiderJoin.Status);

            var staffJoin = await _host.With<ITextRoomService, TextRoomJoinResultDto>(svc =>
                svc.JoinAsync(room.RoomId, admin.Id, viewerIsStaff: true, TextRoomTestFixtures.NewConnectionId()));
            Assert.Equal(TextRoomJoinStatus.Joined, staffJoin.Status);
            Assert.Equal(room.RoomId, staffJoin.RoomId);

            var reconnectJoin = await _host.With<ITextRoomService, TextRoomJoinResultDto>(svc =>
                svc.JoinAsync(room.RoomId, u1.Id, viewerIsStaff: false, TextRoomTestFixtures.NewConnectionId()));
            Assert.Equal(TextRoomJoinStatus.Joined, reconnectJoin.Status);
            Assert.Equal(room.RoomId, reconnectJoin.RoomId);
        }

        [Fact]
        public async Task Join_FullRoomWithAutoOverflow_RedirectsToOverflowRoomCloningRoomConfiguration()
        {
            var admin = await _host.CreateUserAsync(AppRoles.Admin);
            var u1 = await _host.CreateUserAsync(AppRoles.User);
            var u2 = await _host.CreateUserAsync(AppRoles.User);
            var u3 = await _host.CreateUserAsync(AppRoles.User);

            var dto = TextRoomTestFixtures.ValidRoomDto(
                title: "Overflow Lounge",
                configureSettings: s =>
                {
                    s.MaxParticipants = 2;
                    s.AutoOverflowEnabled = true;
                    s.MaxOverflowInstances = 3;
                    s.SlowModeSeconds = 10;
                    s.MaxMessageLength = 750;
                    s.ShowOnlineCountToUsers = true;
                },
                configure: d =>
                {
                    d.Subject = "Books";
                    d.Description = "Discuss your favorite books.";
                    d.DiscussionPrompts = new List<string> { "Best novel this year?", "Favorite author?" };
                });

            var primary = await TextRoomTestFixtures.CreateRoomAsync(_host, TextRoomTestHost.AdminActor(admin), dto);

            await TextRoomTestFixtures.JoinAndGoOnlineAsync(_host, primary.RoomId, u1.Id, "c1");
            await TextRoomTestFixtures.JoinAndGoOnlineAsync(_host, primary.RoomId, u2.Id, "c2");

            var join = await _host.With<ITextRoomService, TextRoomJoinResultDto>(svc =>
                svc.JoinAsync(primary.RoomId, u3.Id, viewerIsStaff: false, TextRoomTestFixtures.NewConnectionId()));

            Assert.Equal(TextRoomJoinStatus.Redirected, join.Status);
            Assert.NotNull(join.RoomId);
            Assert.NotEqual(primary.RoomId, join.RoomId);

            var overflow = await _host.WithDb(db =>
                db.TextRooms.AsNoTracking().SingleAsync(r => r.Id == join.RoomId!.Value));

            Assert.Equal(2, overflow.OverflowIndex);
            Assert.Equal(primary.RoomId, overflow.ParentRoomId);
            Assert.Equal(dto.Title, overflow.Title);
            Assert.Equal(dto.Subject, overflow.Subject);
            Assert.Equal(dto.Description, overflow.Description);
            Assert.Equal(dto.DiscussionPrompts, overflow.DiscussionPrompts);
            Assert.Equal(dto.Settings.MaxParticipants, overflow.Settings.MaxParticipants);
            Assert.Equal(dto.Settings.AutoOverflowEnabled, overflow.Settings.AutoOverflowEnabled);
            Assert.Equal(dto.Settings.MaxOverflowInstances, overflow.Settings.MaxOverflowInstances);
            Assert.Equal(dto.Settings.SlowModeSeconds, overflow.Settings.SlowModeSeconds);
            Assert.Equal(dto.Settings.MaxMessageLength, overflow.Settings.MaxMessageLength);
            Assert.Equal(dto.Settings.ShowOnlineCountToUsers, overflow.Settings.ShowOnlineCountToUsers);
        }

        [Fact]
        public async Task Join_SecondOverflowUser_ReusesExistingOverflowInstanceWithSpace()
        {
            var admin = await _host.CreateUserAsync(AppRoles.Admin);
            var u1 = await _host.CreateUserAsync(AppRoles.User);
            var u2 = await _host.CreateUserAsync(AppRoles.User);
            var u3 = await _host.CreateUserAsync(AppRoles.User);
            var u4 = await _host.CreateUserAsync(AppRoles.User);
            var tracker = _host.Services.GetRequiredService<ITextRoomPresenceTracker>();

            var primary = await TextRoomTestFixtures.CreateRoomAsync(
                _host,
                TextRoomTestHost.AdminActor(admin),
                TextRoomTestFixtures.ValidRoomDto(configureSettings: s =>
                {
                    s.MaxParticipants = 2;
                    s.AutoOverflowEnabled = true;
                    s.MaxOverflowInstances = 3;
                }));

            await TextRoomTestFixtures.JoinAndGoOnlineAsync(_host, primary.RoomId, u1.Id, "c1");
            await TextRoomTestFixtures.JoinAndGoOnlineAsync(_host, primary.RoomId, u2.Id, "c2");

            var firstOverflowJoin = await _host.With<ITextRoomService, TextRoomJoinResultDto>(svc =>
                svc.JoinAsync(primary.RoomId, u3.Id, viewerIsStaff: false, TextRoomTestFixtures.NewConnectionId()));
            Assert.Equal(TextRoomJoinStatus.Redirected, firstOverflowJoin.Status);

            var secondOverflowJoin = await _host.With<ITextRoomService, TextRoomJoinResultDto>(svc =>
                svc.JoinAsync(primary.RoomId, u4.Id, viewerIsStaff: false, TextRoomTestFixtures.NewConnectionId()));

            Assert.Equal(TextRoomJoinStatus.Redirected, secondOverflowJoin.Status);
            Assert.Equal(firstOverflowJoin.RoomId, secondOverflowJoin.RoomId);

            var overflowCount = await _host.WithDb(db =>
                db.TextRooms.CountAsync(r => r.ParentRoomId == primary.RoomId));
            Assert.Equal(1, overflowCount);
        }

        [Fact]
        public async Task Join_MaxOverflowInstancesOneAndAllInstancesFull_ReturnsFullWithoutCreatingThirdRoom()
        {
            var admin = await _host.CreateUserAsync(AppRoles.Admin);
            var u1 = await _host.CreateUserAsync(AppRoles.User);
            var u2 = await _host.CreateUserAsync(AppRoles.User);
            var u3 = await _host.CreateUserAsync(AppRoles.User);
            var u4 = await _host.CreateUserAsync(AppRoles.User);
            var u5 = await _host.CreateUserAsync(AppRoles.User);

            var primary = await TextRoomTestFixtures.CreateRoomAsync(
                _host,
                TextRoomTestHost.AdminActor(admin),
                TextRoomTestFixtures.ValidRoomDto(configureSettings: s =>
                {
                    s.MaxParticipants = 2;
                    s.AutoOverflowEnabled = true;
                    s.MaxOverflowInstances = 1;
                }));

            await TextRoomTestFixtures.JoinAndGoOnlineAsync(_host, primary.RoomId, u1.Id, "c1");
            await TextRoomTestFixtures.JoinAndGoOnlineAsync(_host, primary.RoomId, u2.Id, "c2");
            var overflowId = await TextRoomTestFixtures.JoinAndGoOnlineAsync(_host, primary.RoomId, u3.Id, "c3");
            await TextRoomTestFixtures.JoinAndGoOnlineAsync(_host, primary.RoomId, u4.Id, "c4");

            Assert.NotEqual(primary.RoomId, overflowId);

            var fifthJoin = await _host.With<ITextRoomService, TextRoomJoinResultDto>(svc =>
                svc.JoinAsync(primary.RoomId, u5.Id, viewerIsStaff: false, TextRoomTestFixtures.NewConnectionId()));

            Assert.Equal(TextRoomJoinStatus.Full, fifthJoin.Status);

            var familyCount = await _host.WithDb(db =>
                db.TextRooms.CountAsync(r => r.Id == primary.RoomId || r.ParentRoomId == primary.RoomId));
            Assert.Equal(2, familyCount);
        }

        [Fact]
        public async Task Join_TwentyConcurrentUsersWithAutoOverflowCapThree_FillsEverySeatExactlyOnceAndRejectsTheRest()
        {
            var admin = await _host.CreateUserAsync(AppRoles.Admin);
            var seed1 = await _host.CreateUserAsync(AppRoles.User);
            var seed2 = await _host.CreateUserAsync(AppRoles.User);
            var tracker = _host.Services.GetRequiredService<ITextRoomPresenceTracker>();

            var primary = await TextRoomTestFixtures.CreateRoomAsync(
                _host,
                TextRoomTestHost.AdminActor(admin),
                TextRoomTestFixtures.ValidRoomDto(configureSettings: s =>
                {
                    s.MaxParticipants = 2;
                    s.AutoOverflowEnabled = true;
                    s.MaxOverflowInstances = 3;
                }));

            await TextRoomTestFixtures.JoinAndGoOnlineAsync(_host, primary.RoomId, seed1.Id, "seed-1");
            await TextRoomTestFixtures.JoinAndGoOnlineAsync(_host, primary.RoomId, seed2.Id, "seed-2");

            var users = new List<ApplicationUser>(20);
            for (var i = 0; i < 20; i++)
                users.Add(await _host.CreateUserAsync(AppRoles.User));

            var results = await Task.WhenAll(users.Select((user, idx) => Task.Run(() =>
                _host.With<ITextRoomService, TextRoomJoinResultDto>(svc =>
                    svc.JoinAsync(primary.RoomId, user.Id, viewerIsStaff: false, $"parallel-{idx}")))));

            var family = await _host.WithDb(db =>
                db.TextRooms.AsNoTracking()
                    .Where(r => r.Id == primary.RoomId || r.ParentRoomId == primary.RoomId)
                    .ToListAsync());
            var overflows = family.Where(r => r.ParentRoomId == primary.RoomId).ToList();

            // 3 overflow rooms x 2 seats: exactly 6 of the 20 get in, the other 14 are turned away.
            Assert.Equal(3, overflows.Count);
            Assert.Equal(6, results.Count(r => r.Status == TextRoomJoinStatus.Redirected));
            Assert.Equal(14, results.Count(r => r.Status == TextRoomJoinStatus.Full));
            Assert.All(family, room => Assert.True(
                tracker.GetOnlineCount(room.Id) <= 2,
                $"Room #{room.OverflowIndex} holds {tracker.GetOnlineCount(room.Id)} users, above its cap of 2."));
            Assert.Equal(family.Count, family.Select(r => r.OverflowIndex).Distinct().Count());
        }

        [Fact]
        public async Task Join_KickedUser_ReturnsKicked()
        {
            var admin = await _host.CreateUserAsync(AppRoles.Admin);
            var user = await _host.CreateUserAsync(AppRoles.User);
            var adminActor = TextRoomTestHost.AdminActor(admin);

            var room = await TextRoomTestFixtures.CreateRoomAsync(_host, adminActor);
            await TextRoomTestFixtures.JoinAndGoOnlineAsync(_host, room.RoomId, user.Id, "c1");

            var kick = await _host.With<ITextRoomModerationService, Response<string>>(svc =>
                svc.KickUserAsync(room.RoomId, user.Id, "Violated room rules", adminActor));
            Assert.True(kick.Succeeded);

            var rejoin = await _host.With<ITextRoomService, TextRoomJoinResultDto>(svc =>
                svc.JoinAsync(room.RoomId, user.Id, viewerIsStaff: false, TextRoomTestFixtures.NewConnectionId()));

            Assert.Equal(TextRoomJoinStatus.Kicked, rejoin.Status);
        }

        [Fact]
        public async Task Join_ClosedRoom_ReturnsClosed()
        {
            var admin = await _host.CreateUserAsync(AppRoles.Admin);
            var user = await _host.CreateUserAsync(AppRoles.User);
            var adminActor = TextRoomTestHost.AdminActor(admin);

            var room = await TextRoomTestFixtures.CreateRoomAsync(_host, adminActor);
            var close = await _host.With<ITextRoomAdminService, Response<string>>(svc =>
                svc.CloseRoomAsync(room.RoomId, "Session ended", adminActor));
            Assert.True(close.Succeeded);

            var join = await _host.With<ITextRoomService, TextRoomJoinResultDto>(svc =>
                svc.JoinAsync(room.RoomId, user.Id, viewerIsStaff: false, TextRoomTestFixtures.NewConnectionId()));

            Assert.Equal(TextRoomJoinStatus.Closed, join.Status);
        }

        [Fact]
        public async Task Join_FutureScheduledRoom_ReturnsNotOpenYet()
        {
            var admin = await _host.CreateUserAsync(AppRoles.Admin);
            var user = await _host.CreateUserAsync(AppRoles.User);

            var room = await TextRoomTestFixtures.CreateRoomAsync(
                _host,
                TextRoomTestHost.AdminActor(admin),
                TextRoomTestFixtures.ValidRoomDto(configure: d => d.OpensAt = _host.Clock.UtcNow.AddHours(1)));

            var join = await _host.With<ITextRoomService, TextRoomJoinResultDto>(svc =>
                svc.JoinAsync(room.RoomId, user.Id, viewerIsStaff: false, TextRoomTestFixtures.NewConnectionId()));

            Assert.Equal(TextRoomJoinStatus.NotOpenYet, join.Status);
        }

        [Fact]
        public async Task Join_UnknownRoomId_ReturnsNotFound()
        {
            var user = await _host.CreateUserAsync(AppRoles.User);

            var join = await _host.With<ITextRoomService, TextRoomJoinResultDto>(svc =>
                svc.JoinAsync(Guid.NewGuid(), user.Id, viewerIsStaff: false, TextRoomTestFixtures.NewConnectionId()));

            Assert.Equal(TextRoomJoinStatus.NotFound, join.Status);
        }
    }
}
