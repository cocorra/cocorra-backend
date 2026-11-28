using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Cocorra.BLL.Base;
using Cocorra.BLL.Services.TextRooms;
using Cocorra.DAL.DTOS.TextRoomDto;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;
using Cocorra.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Cocorra.Tests.TextRooms
{
    public class TextRoomMaintenanceTests : IDisposable
    {
        private readonly TextRoomTestHost _host = new();

        public void Dispose() => _host.Dispose();

        [Fact]
        public async Task ProcessRoomLifecycle_ActivatesScheduledRoomWhenOpensAtPasses()
        {
            var admin = await _host.CreateUserAsync(AppRoles.Admin);
            var room = await TextRoomTestFixtures.CreateRoomAsync(
                _host,
                TextRoomTestHost.AdminActor(admin),
                TextRoomTestFixtures.ValidRoomDto(configure: d => d.OpensAt = _host.Clock.UtcNow.AddMinutes(30)));

            Assert.Equal(TextRoomStatus.Scheduled.ToString(), room.Status);

            _host.Clock.Advance(TimeSpan.FromMinutes(29));
            var beforeDue = await _host.With<ITextRoomMaintenance, int>(m => m.ProcessRoomLifecycleAsync());
            Assert.Equal(0, beforeDue);

            _host.Clock.Advance(TimeSpan.FromMinutes(1));
            var onDue = await _host.With<ITextRoomMaintenance, int>(m => m.ProcessRoomLifecycleAsync());
            Assert.Equal(1, onDue);

            var status = await _host.WithDb(db =>
                db.TextRooms.AsNoTracking().Where(r => r.Id == room.RoomId).Select(r => r.Status).SingleAsync());
            Assert.Equal(TextRoomStatus.Active, status);
        }

        [Fact]
        public async Task ProcessRoomLifecycle_ClosesTimedRoomAtClosesAtWithExpiredReasonAndBroadcast()
        {
            var admin = await _host.CreateUserAsync(AppRoles.Admin);
            var room = await TextRoomTestFixtures.CreateRoomAsync(
                _host,
                TextRoomTestHost.AdminActor(admin),
                TextRoomTestFixtures.ValidRoomDto(configureSettings: s =>
                {
                    s.IsAlwaysOn = false;
                    s.DurationMinutes = 60;
                }));

            _host.Clock.Advance(TimeSpan.FromMinutes(59));
            var beforeExpiry = await _host.With<ITextRoomMaintenance, int>(m => m.ProcessRoomLifecycleAsync());
            Assert.Equal(0, beforeExpiry);

            _host.Clock.Advance(TimeSpan.FromMinutes(1));
            var atExpiry = await _host.With<ITextRoomMaintenance, int>(m => m.ProcessRoomLifecycleAsync());
            Assert.Equal(1, atExpiry);

            var entity = await _host.WithDb(db => db.TextRooms.AsNoTracking().SingleAsync(r => r.Id == room.RoomId));
            Assert.Equal(TextRoomStatus.Closed, entity.Status);
            Assert.Equal("Expired", entity.CloseReason);
            Assert.Equal(_host.Clock.UtcNow, entity.ClosedAt);

            var closedBroadcast = Assert.Single(_host.Broadcaster.CallsTo(nameof(ITextRoomBroadcaster.RoomClosedAsync)));
            Assert.Equal(room.RoomId, closedBroadcast[0]);
            Assert.Equal("Expired", closedBroadcast[1]);
        }

        [Fact]
        public async Task ProcessRoomLifecycle_AlwaysOnRoomNeverCloses()
        {
            var admin = await _host.CreateUserAsync(AppRoles.Admin);
            var room = await TextRoomTestFixtures.CreateRoomAsync(
                _host,
                TextRoomTestHost.AdminActor(admin),
                TextRoomTestFixtures.ValidRoomDto(configureSettings: s =>
                {
                    s.IsAlwaysOn = true;
                    s.DurationMinutes = null;
                }));

            _host.Clock.Advance(TimeSpan.FromDays(365));
            var affected = await _host.With<ITextRoomMaintenance, int>(m => m.ProcessRoomLifecycleAsync());

            Assert.Equal(0, affected);
            var entity = await _host.WithDb(db => db.TextRooms.AsNoTracking().SingleAsync(r => r.Id == room.RoomId));
            Assert.Equal(TextRoomStatus.Active, entity.Status);
            Assert.Null(entity.ClosedAt);
        }

        [Fact]
        public async Task ProcessRoomLifecycle_BacklogLargerThanOneBatch_ClosesMostOverdueRoomFirst()
        {
            const int batchLimit = 200;
            var now = _host.Clock.UtcNow;
            var openedAt = now.AddHours(-11);
            // Last by primary key, insertion order and OpensAt index, so only ordering by ClosesAt picks it.
            var longOverdueId = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
            await _host.WithDb(async db =>
            {
                for (var i = 0; i < batchLimit; i++)
                    db.TextRooms.Add(DueRoom(Guid.NewGuid(), openedAt, closesAt: now.AddMinutes(-1)));
                await db.SaveChangesAsync();
                db.TextRooms.Add(DueRoom(longOverdueId, openedAt, closesAt: now.AddHours(-10)));
                await db.SaveChangesAsync();
            });

            var closed = await _host.With<ITextRoomMaintenance, int>(m => m.ProcessRoomLifecycleAsync());

            Assert.Equal(batchLimit, closed);
            var longOverdueStatus = await _host.WithDb(db =>
                db.TextRooms.AsNoTracking().Where(r => r.Id == longOverdueId).Select(r => r.Status).SingleAsync());
            Assert.Equal(TextRoomStatus.Closed, longOverdueStatus);

            static TextRoom DueRoom(Guid id, DateTime opensAt, DateTime closesAt) => new()
            {
                Id = id,
                Title = "Backlog room",
                Status = TextRoomStatus.Active,
                OpensAt = opensAt,
                ClosesAt = closesAt,
                Settings = new TextRoomSettings { IsAlwaysOn = false, DurationMinutes = 60 }
            };
        }

        [Fact]
        public async Task CloseRoomByStaff_ClosesWholeOverflowFamily_AndSubsequentUpdateReturnsConflict()
        {
            var admin = await _host.CreateUserAsync(AppRoles.Admin);
            var u1 = await _host.CreateUserAsync(AppRoles.User);
            var u2 = await _host.CreateUserAsync(AppRoles.User);
            var u3 = await _host.CreateUserAsync(AppRoles.User);
            var adminActor = TextRoomTestHost.AdminActor(admin);

            var primary = await TextRoomTestFixtures.CreateRoomAsync(
                _host,
                adminActor,
                TextRoomTestFixtures.ValidRoomDto(configureSettings: s =>
                {
                    s.MaxParticipants = 2;
                    s.AutoOverflowEnabled = true;
                    s.MaxOverflowInstances = 2;
                }));

            await TextRoomTestFixtures.JoinAndGoOnlineAsync(_host, primary.RoomId, u1.Id, "c1");
            await TextRoomTestFixtures.JoinAndGoOnlineAsync(_host, primary.RoomId, u2.Id, "c2");
            var overflowId = await TextRoomTestFixtures.JoinAndGoOnlineAsync(_host, primary.RoomId, u3.Id, "c3");
            Assert.NotEqual(primary.RoomId, overflowId);

            var close = await _host.With<ITextRoomAdminService, Response<string>>(svc =>
                svc.CloseRoomAsync(primary.RoomId, "Scheduled maintenance", adminActor));
            Assert.True(close.Succeeded);

            var family = await _host.WithDb(db =>
                db.TextRooms.AsNoTracking()
                    .Where(r => r.Id == primary.RoomId || r.Id == overflowId)
                    .ToListAsync());
            Assert.Equal(2, family.Count);
            Assert.All(family, r =>
            {
                Assert.Equal(TextRoomStatus.Closed, r.Status);
                Assert.Equal("Scheduled maintenance", r.CloseReason);
                Assert.Equal(_host.Clock.UtcNow, r.ClosedAt);
            });

            var closedRoomIds = _host.Broadcaster
                .CallsTo(nameof(ITextRoomBroadcaster.RoomClosedAsync))
                .Select(args => (Guid)args[0]!)
                .ToHashSet();
            Assert.Contains(primary.RoomId, closedRoomIds);
            Assert.Contains(overflowId, closedRoomIds);

            var updateClosed = await _host.With<ITextRoomAdminService, Response<TextRoomStaffViewDto>>(svc =>
                svc.UpdateRoomAsync(primary.RoomId, TextRoomTestFixtures.ValidRoomDto("Cannot Edit Closed"), adminActor));
            Assert.False(updateClosed.Succeeded);
            Assert.Equal(HttpStatusCode.Conflict, updateClosed.StatusCode);
        }

        [Fact]
        public async Task UpdateRoom_OnLongRunningAlwaysOnRoom_KeepsRotationClockInsteadOfRotatingImmediately()
        {
            var admin = await _host.CreateUserAsync(AppRoles.Admin);
            var actor = TextRoomTestHost.AdminActor(admin);
            var pool = new List<TextRoomTopicDto>
            {
                TextRoomTestFixtures.Topic("Topic A", "Prompt A"),
                TextRoomTestFixtures.Topic("Topic B", "Prompt B")
            };
            TextRoomUpsertDto Dto(string title) => TextRoomTestFixtures.ValidRoomDto(
                title: title,
                configureSettings: s =>
                {
                    s.IsAlwaysOn = true;
                    s.DurationMinutes = null;
                    s.RotateTopicEveryMinutes = 60;
                    s.ClearHistoryOnRotate = true;
                },
                configure: d => d.TopicPool = pool);

            var room = await TextRoomTestFixtures.CreateRoomAsync(_host, actor, Dto("Lobby"));
            _host.Clock.Advance(TimeSpan.FromMinutes(90));
            Assert.Equal(1, await _host.With<ITextRoomMaintenance, int>(m => m.RotateDueTopicsAsync()));
            var rotated = await _host.WithDb(db => db.TextRooms.AsNoTracking().SingleAsync(r => r.Id == room.RoomId));

            var update = await _host.With<ITextRoomAdminService, Response<TextRoomStaffViewDto>>(svc =>
                svc.UpdateRoomAsync(room.RoomId, Dto("Lobby (typo fixed)"), actor));
            Assert.True(update.Succeeded);

            _host.Clock.Advance(TimeSpan.FromMinutes(1));
            Assert.Equal(0, await _host.With<ITextRoomMaintenance, int>(m => m.RotateDueTopicsAsync()));
            var afterEdit = await _host.WithDb(db => db.TextRooms.AsNoTracking().SingleAsync(r => r.Id == room.RoomId));
            Assert.Equal(rotated.NextRotationAt, afterEdit.NextRotationAt);
            Assert.Equal(rotated.HistoryClearedAt, afterEdit.HistoryClearedAt);
        }

        [Fact]
        public async Task UpdateRoom_ChangingRotationInterval_RestartsTheClockFromNow()
        {
            var admin = await _host.CreateUserAsync(AppRoles.Admin);
            var actor = TextRoomTestHost.AdminActor(admin);
            TextRoomUpsertDto Dto(int rotateMinutes) => TextRoomTestFixtures.ValidRoomDto(configureSettings: s =>
            {
                s.IsAlwaysOn = true;
                s.DurationMinutes = null;
                s.RotateTopicEveryMinutes = rotateMinutes;
            });

            var room = await TextRoomTestFixtures.CreateRoomAsync(_host, actor, Dto(60));
            _host.Clock.Advance(TimeSpan.FromMinutes(30));

            await _host.With<ITextRoomAdminService, Response<TextRoomStaffViewDto>>(svc =>
                svc.UpdateRoomAsync(room.RoomId, Dto(120), actor));

            var updated = await _host.WithDb(db => db.TextRooms.AsNoTracking().SingleAsync(r => r.Id == room.RoomId));
            Assert.Equal(_host.Clock.UtcNow.AddMinutes(120), updated.NextRotationAt);
        }

        [Fact]
        public async Task RotateDueTopics_RotatesPoolInOrderAndWraps_ClearsHistoryAndBumpsConfigVersion()
        {
            var admin = await _host.CreateUserAsync(AppRoles.Admin);
            var topicA = TextRoomTestFixtures.Topic("Morning Reflections", "What are you grateful for?");
            var topicB = TextRoomTestFixtures.Topic("Night Wind-Down", "What went well today?", "Plans for tomorrow?");

            var room = await TextRoomTestFixtures.CreateRoomAsync(
                _host,
                TextRoomTestHost.AdminActor(admin),
                TextRoomTestFixtures.ValidRoomDto(
                    title: "Initial Lobby",
                    configureSettings: s =>
                    {
                        s.IsAlwaysOn = true;
                        s.DurationMinutes = null;
                        s.RotateTopicEveryMinutes = 60;
                        s.ClearHistoryOnRotate = true;
                    },
                    configure: d => d.TopicPool = new List<TextRoomTopicDto> { topicA, topicB }));

            var initialVersion = room.ConfigVersion;

            _host.Clock.Advance(TimeSpan.FromMinutes(60));
            var rot1 = await _host.With<ITextRoomMaintenance, int>(m => m.RotateDueTopicsAsync());
            Assert.Equal(1, rot1);

            var after1 = await _host.WithDb(db => db.TextRooms.AsNoTracking().SingleAsync(r => r.Id == room.RoomId));
            Assert.Equal(topicA.Title, after1.Title);
            Assert.Equal(topicA.Prompts, after1.DiscussionPrompts);
            Assert.Equal(_host.Clock.UtcNow, after1.HistoryClearedAt);
            Assert.Equal(initialVersion + 1, after1.ConfigVersion);

            _host.Clock.Advance(TimeSpan.FromMinutes(60));
            var rot2 = await _host.With<ITextRoomMaintenance, int>(m => m.RotateDueTopicsAsync());
            Assert.Equal(1, rot2);

            var after2 = await _host.WithDb(db => db.TextRooms.AsNoTracking().SingleAsync(r => r.Id == room.RoomId));
            Assert.Equal(topicB.Title, after2.Title);
            Assert.Equal(topicB.Prompts, after2.DiscussionPrompts);
            Assert.Equal(_host.Clock.UtcNow, after2.HistoryClearedAt);
            Assert.Equal(initialVersion + 2, after2.ConfigVersion);

            _host.Clock.Advance(TimeSpan.FromMinutes(60));
            var rot3 = await _host.With<ITextRoomMaintenance, int>(m => m.RotateDueTopicsAsync());
            Assert.Equal(1, rot3);

            var after3 = await _host.WithDb(db => db.TextRooms.AsNoTracking().SingleAsync(r => r.Id == room.RoomId));
            Assert.Equal(topicA.Title, after3.Title);
            Assert.Equal(topicA.Prompts, after3.DiscussionPrompts);
            Assert.Equal(initialVersion + 3, after3.ConfigVersion);

            var historyClearedCalls = _host.Broadcaster
                .CallsTo(nameof(ITextRoomBroadcaster.HistoryClearedAsync))
                .Where(args => (Guid)args[0]! == room.RoomId)
                .ToList();
            Assert.Equal(3, historyClearedCalls.Count);

            var configUpdatedCalls = _host.Broadcaster
                .CallsTo(nameof(ITextRoomBroadcaster.ConfigUpdatedAsync))
                .Select(args => (TextRoomClientConfigDto)args[0]!)
                .Where(c => c.RoomId == room.RoomId)
                .ToList();
            Assert.Equal(3, configUpdatedCalls.Count);
            Assert.Equal(initialVersion + 3, configUpdatedCalls[^1].ConfigVersion);
        }

        [Fact]
        public async Task SpawnDueSchedules_SpawnsTopicsInOrderAndClosesPreviousRoomWhenConfigured()
        {
            var admin = await _host.CreateUserAsync(AppRoles.Admin);
            var topic0 = TextRoomTestFixtures.Topic("Session Alpha", "Alpha Q1");
            var topic1 = TextRoomTestFixtures.Topic("Session Beta", "Beta Q1");

            var schedule = await TextRoomTestFixtures.CreateScheduleAsync(
                _host,
                TextRoomTestHost.AdminActor(admin),
                TextRoomTestFixtures.ValidScheduleDto(
                    configureSettings: s =>
                    {
                        s.IsAlwaysOn = false;
                        s.DurationMinutes = 180;
                    },
                    configure: sc =>
                    {
                        sc.SpawnEveryMinutes = 60;
                        sc.ClosePreviousOnSpawn = true;
                        sc.FirstSpawnAt = _host.Clock.UtcNow;
                        sc.TopicPool = new List<TextRoomTopicDto> { topic0, topic1 };
                    }));

            var firstSpawn = await _host.With<ITextRoomMaintenance, int>(m => m.SpawnDueSchedulesAsync());
            Assert.Equal(1, firstSpawn);

            var room1 = await _host.WithDb(db =>
                db.TextRooms.AsNoTracking().SingleAsync(r => r.ScheduleId == schedule.Id));
            Assert.Equal(topic0.Title, room1.Title);
            Assert.Equal(topic0.Prompts, room1.DiscussionPrompts);
            Assert.Equal(TextRoomStatus.Active, room1.Status);

            _host.Clock.Advance(TimeSpan.FromMinutes(60));
            var secondSpawn = await _host.With<ITextRoomMaintenance, int>(m => m.SpawnDueSchedulesAsync());
            Assert.Equal(1, secondSpawn);

            var rooms = await _host.WithDb(db =>
                db.TextRooms.AsNoTracking()
                    .Where(r => r.ScheduleId == schedule.Id)
                    .OrderBy(r => r.OpensAt)
                    .ToListAsync());
            Assert.Equal(2, rooms.Count);
            Assert.Equal(TextRoomStatus.Closed, rooms[0].Status);
            Assert.Equal(topic1.Title, rooms[1].Title);
            Assert.Equal(topic1.Prompts, rooms[1].DiscussionPrompts);
            Assert.Equal(TextRoomStatus.Active, rooms[1].Status);
        }

        [Fact]
        public async Task SpawnDueSchedules_DisabledSchedule_SpawnsNothing()
        {
            var admin = await _host.CreateUserAsync(AppRoles.Admin);

            await TextRoomTestFixtures.CreateScheduleAsync(
                _host,
                TextRoomTestHost.AdminActor(admin),
                TextRoomTestFixtures.ValidScheduleDto(configure: sc =>
                {
                    sc.IsEnabled = false;
                    sc.FirstSpawnAt = _host.Clock.UtcNow;
                }));

            var spawned = await _host.With<ITextRoomMaintenance, int>(m => m.SpawnDueSchedulesAsync());

            Assert.Equal(0, spawned);
            var roomCount = await _host.WithDb(db => db.TextRooms.CountAsync());
            Assert.Equal(0, roomCount);
        }

        [Fact]
        public async Task SpawnDueSchedules_OutsideCairoWindow_SkipsSpawnAndAdvancesNextSpawnAt()
        {
            var admin = await _host.CreateUserAsync(AppRoles.Admin);
            var cairo0830Utc = TextRoomTestFixtures.CairoLocalToUtc(2026, 11, 28, 8, 30);
            _host.Clock.Set(new DateTimeOffset(cairo0830Utc, TimeSpan.Zero));

            var schedule = await TextRoomTestFixtures.CreateScheduleAsync(
                _host,
                TextRoomTestHost.AdminActor(admin),
                TextRoomTestFixtures.ValidScheduleDto(configure: sc =>
                {
                    sc.SpawnEveryMinutes = 60;
                    sc.WindowStartMinuteOfDay = 9 * 60;
                    sc.WindowEndMinuteOfDay = 17 * 60;
                    sc.FirstSpawnAt = cairo0830Utc;
                }));

            var outsideCount = await _host.With<ITextRoomMaintenance, int>(m => m.SpawnDueSchedulesAsync());
            Assert.Equal(0, outsideCount);
            Assert.Equal(0, await _host.WithDb(db => db.TextRooms.CountAsync()));

            var nextSpawnAt = await _host.WithDb(db =>
                db.TextRoomSchedules.AsNoTracking()
                    .Where(s => s.Id == schedule.Id)
                    .Select(s => s.NextSpawnAt)
                    .SingleAsync());
            Assert.True(nextSpawnAt > cairo0830Utc);

            // Advancing to the updated NextSpawnAt (Cairo 09:30) falls inside 09:00-17:00.
            _host.Clock.Set(new DateTimeOffset(nextSpawnAt, TimeSpan.Zero));
            var insideCount = await _host.With<ITextRoomMaintenance, int>(m => m.SpawnDueSchedulesAsync());
            Assert.Equal(1, insideCount);
        }

        [Fact]
        public async Task SpawnDueSchedules_MidnightWrappingCairoWindow_SpawnsAt2300Cairo()
        {
            var admin = await _host.CreateUserAsync(AppRoles.Admin);
            var cairo2300Utc = TextRoomTestFixtures.CairoLocalToUtc(2026, 11, 28, 23, 0);
            _host.Clock.Set(new DateTimeOffset(cairo2300Utc, TimeSpan.Zero));

            await TextRoomTestFixtures.CreateScheduleAsync(
                _host,
                TextRoomTestHost.AdminActor(admin),
                TextRoomTestFixtures.ValidScheduleDto(configure: sc =>
                {
                    sc.SpawnEveryMinutes = 60;
                    sc.WindowStartMinuteOfDay = 22 * 60;
                    sc.WindowEndMinuteOfDay = 2 * 60;
                    sc.FirstSpawnAt = cairo2300Utc;
                }));

            var spawned = await _host.With<ITextRoomMaintenance, int>(m => m.SpawnDueSchedulesAsync());

            Assert.Equal(1, spawned);
            Assert.Equal(1, await _host.WithDb(db => db.TextRooms.CountAsync()));
        }

        [Fact]
        public async Task SpawnDueSchedules_AfterTenHourClockJump_SpawnsOnceWithoutBurstAndAdvancesNextSpawnAtToFuture()
        {
            var admin = await _host.CreateUserAsync(AppRoles.Admin);
            var start = _host.Clock.UtcNow;

            var schedule = await TextRoomTestFixtures.CreateScheduleAsync(
                _host,
                TextRoomTestHost.AdminActor(admin),
                TextRoomTestFixtures.ValidScheduleDto(configure: sc =>
                {
                    sc.SpawnEveryMinutes = 60;
                    sc.ClosePreviousOnSpawn = false;
                    sc.FirstSpawnAt = start;
                }));

            _host.Clock.Advance(TimeSpan.FromHours(10).Add(TimeSpan.FromMinutes(5)));

            var firstPass = await _host.With<ITextRoomMaintenance, int>(m => m.SpawnDueSchedulesAsync());
            var secondPass = await _host.With<ITextRoomMaintenance, int>(m => m.SpawnDueSchedulesAsync());

            Assert.Equal(1, firstPass);
            Assert.Equal(0, secondPass);
            Assert.Equal(1, await _host.WithDb(db => db.TextRooms.CountAsync(r => r.ScheduleId == schedule.Id)));

            var nextSpawnAt = await _host.WithDb(db =>
                db.TextRoomSchedules.AsNoTracking()
                    .Where(s => s.Id == schedule.Id)
                    .Select(s => s.NextSpawnAt)
                    .SingleAsync());
            Assert.True(nextSpawnAt > _host.Clock.UtcNow);
        }

        [Fact]
        public async Task PurgeExpiredMessages_DeletesOldUnreported_KeepsReportedAndYounger()
        {
            var admin = await _host.CreateUserAsync(AppRoles.Admin);
            var sender = await _host.CreateUserAsync(AppRoles.User);
            var room = await TextRoomTestFixtures.CreateRoomAsync(_host, TextRoomTestHost.AdminActor(admin));
            var now = _host.Clock.UtcNow;

            var ids = await _host.WithDb(async db =>
            {
                var oldUnreported = new TextRoomMessage
                {
                    TextRoomId = room.RoomId,
                    SenderId = sender.Id,
                    Content = "purge-me",
                    CreatedAt = now.AddHours(-73),
                    IsReported = false
                };
                var oldReported = new TextRoomMessage
                {
                    TextRoomId = room.RoomId,
                    SenderId = sender.Id,
                    Content = "keep-reported",
                    CreatedAt = now.AddHours(-73),
                    IsReported = true
                };
                var youngUnreported = new TextRoomMessage
                {
                    TextRoomId = room.RoomId,
                    SenderId = sender.Id,
                    Content = "keep-young",
                    CreatedAt = now.AddHours(-71),
                    IsReported = false
                };

                db.TextRoomMessages.AddRange(oldUnreported, oldReported, youngUnreported);
                await db.SaveChangesAsync();
                return (oldUnreported.Id, oldReported.Id, youngUnreported.Id);
            });

            var purged = await _host.With<ITextRoomMaintenance, int>(m => m.PurgeExpiredMessagesAsync());

            Assert.Equal(1, purged);
            var remainingIds = await _host.WithDb(db =>
                db.TextRoomMessages.AsNoTracking().Select(m => m.Id).ToListAsync());

            Assert.DoesNotContain(ids.Item1, remainingIds);
            Assert.Contains(ids.Item2, remainingIds);
            Assert.Contains(ids.Item3, remainingIds);
        }

        [Fact]
        public async Task PurgeExpiredMessages_RoomRetentionOverridesGlobalDefaultInBothDirections()
        {
            var admin = await _host.CreateUserAsync(AppRoles.Admin);
            var sender = await _host.CreateUserAsync(AppRoles.User);
            var adminActor = TextRoomTestHost.AdminActor(admin);

            var shortRetentionRoom = await TextRoomTestFixtures.CreateRoomAsync(
                _host,
                adminActor,
                TextRoomTestFixtures.ValidRoomDto("Short Retention", s => s.MessageRetentionHours = 2));
            var longRetentionRoom = await TextRoomTestFixtures.CreateRoomAsync(
                _host,
                adminActor,
                TextRoomTestFixtures.ValidRoomDto("Long Retention", s => s.MessageRetentionHours = 200));

            var now = _host.Clock.UtcNow;
            var ids = await _host.WithDb(async db =>
            {
                // 10h is older than room override (2h) despite being younger than the 72h global default.
                var shortRoomMsg = new TextRoomMessage
                {
                    TextRoomId = shortRetentionRoom.RoomId,
                    SenderId = sender.Id,
                    Content = "short-override-expired",
                    CreatedAt = now.AddHours(-10),
                    IsReported = false
                };
                // 100h is older than the 72h global default but within room override (200h).
                var longRoomMsg = new TextRoomMessage
                {
                    TextRoomId = longRetentionRoom.RoomId,
                    SenderId = sender.Id,
                    Content = "long-override-retained",
                    CreatedAt = now.AddHours(-100),
                    IsReported = false
                };

                db.TextRoomMessages.AddRange(shortRoomMsg, longRoomMsg);
                await db.SaveChangesAsync();
                return (shortRoomMsg.Id, longRoomMsg.Id);
            });

            var purged = await _host.With<ITextRoomMaintenance, int>(m => m.PurgeExpiredMessagesAsync());

            Assert.Equal(1, purged);
            var remainingIds = await _host.WithDb(db =>
                db.TextRoomMessages.AsNoTracking().Select(m => m.Id).ToListAsync());

            Assert.DoesNotContain(ids.Item1, remainingIds);
            Assert.Contains(ids.Item2, remainingIds);
        }
    }
}
