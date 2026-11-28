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
using Xunit;

namespace Cocorra.Tests.TextRooms
{
    public class TextRoomHistoryTests : IDisposable
    {
        private readonly TextRoomTestHost _host = new();

        public void Dispose() => _host.Dispose();

        [Fact]
        public async Task GetMessages_NonMember_ReturnsForbidden()
        {
            var admin = await _host.CreateUserAsync(AppRoles.Admin);
            var outsider = await _host.CreateUserAsync(AppRoles.User);
            var room = await TextRoomTestFixtures.CreateRoomAsync(_host, TextRoomTestHost.AdminActor(admin));

            var result = await _host.With<ITextRoomService, Response<List<TextRoomMessageDto>>>(svc =>
                svc.GetMessagesAsync(room.RoomId, outsider.Id, beforeId: null, limit: 20));

            Assert.False(result.Succeeded);
            Assert.Equal(HttpStatusCode.Forbidden, result.StatusCode);
        }

        [Fact]
        public async Task GetMessages_KickedMember_ReturnsForbidden()
        {
            var admin = await _host.CreateUserAsync(AppRoles.Admin);
            var user = await _host.CreateUserAsync(AppRoles.User);
            var adminActor = TextRoomTestHost.AdminActor(admin);

            var room = await TextRoomTestFixtures.CreateRoomAsync(_host, adminActor);
            await TextRoomTestFixtures.JoinAndGoOnlineAsync(_host, room.RoomId, user.Id, "c1");

            var kick = await _host.With<ITextRoomModerationService, Response<string>>(svc =>
                svc.KickUserAsync(room.RoomId, user.Id, "Abusive conduct", adminActor));
            Assert.True(kick.Succeeded);

            var result = await _host.With<ITextRoomService, Response<List<TextRoomMessageDto>>>(svc =>
                svc.GetMessagesAsync(room.RoomId, user.Id, beforeId: null, limit: 20));

            Assert.False(result.Succeeded);
            Assert.Equal(HttpStatusCode.Forbidden, result.StatusCode);
        }

        [Fact]
        public async Task GetMessages_ExcludesDeletedAndMessagesAtOrBeforeHistoryClearedAt()
        {
            var admin = await _host.CreateUserAsync(AppRoles.Admin);
            var member = await _host.CreateUserAsync(AppRoles.User);
            var adminActor = TextRoomTestHost.AdminActor(admin);

            var room = await TextRoomTestFixtures.CreateRoomAsync(_host, adminActor);
            await TextRoomTestFixtures.JoinAndGoOnlineAsync(_host, room.RoomId, member.Id, "c1");

            var baseTime = _host.Clock.UtcNow;
            var clearedAt = baseTime.AddSeconds(10);

            var seededIds = await _host.WithDb(async db =>
            {
                var messages = new[]
                {
                    new TextRoomMessage { TextRoomId = room.RoomId, SenderId = member.Id, Content = "before-clear", CreatedAt = baseTime },
                    new TextRoomMessage { TextRoomId = room.RoomId, SenderId = member.Id, Content = "at-clear", CreatedAt = clearedAt },
                    new TextRoomMessage { TextRoomId = room.RoomId, SenderId = member.Id, Content = "visible-1", CreatedAt = baseTime.AddSeconds(20) },
                    new TextRoomMessage { TextRoomId = room.RoomId, SenderId = member.Id, Content = "deleted-after", CreatedAt = baseTime.AddSeconds(30) },
                    new TextRoomMessage { TextRoomId = room.RoomId, SenderId = member.Id, Content = "visible-2", CreatedAt = baseTime.AddSeconds(40) }
                };

                db.TextRoomMessages.AddRange(messages);
                var entity = await db.TextRooms.SingleAsync(r => r.Id == room.RoomId);
                entity.HistoryClearedAt = clearedAt;
                await db.SaveChangesAsync();

                return messages.Select(m => m.Id).ToArray();
            });

            var delete = await _host.With<ITextRoomModerationService, Response<string>>(svc =>
                svc.DeleteMessageAsync(seededIds[3], "Removed by staff", adminActor));
            Assert.True(delete.Succeeded);

            var result = await _host.With<ITextRoomService, Response<List<TextRoomMessageDto>>>(svc =>
                svc.GetMessagesAsync(room.RoomId, member.Id, beforeId: null, limit: 50));

            Assert.True(result.Succeeded);
            Assert.Equal(new[] { seededIds[2], seededIds[4] }, result.Data!.Select(m => m.Id).ToArray());
            Assert.Equal(new[] { "visible-1", "visible-2" }, result.Data!.Select(m => m.Content).ToArray());
        }

        [Fact]
        public async Task GetMessages_ReturnsAscendingOrderAndPagesWithBeforeId()
        {
            var admin = await _host.CreateUserAsync(AppRoles.Admin);
            var member = await _host.CreateUserAsync(AppRoles.User);
            var room = await TextRoomTestFixtures.CreateRoomAsync(_host, TextRoomTestHost.AdminActor(admin));
            await TextRoomTestFixtures.JoinAndGoOnlineAsync(_host, room.RoomId, member.Id, "c1");

            var seededIds = await _host.WithDb(async db =>
            {
                var list = Enumerable.Range(1, 7)
                    .Select(i => new TextRoomMessage
                    {
                        TextRoomId = room.RoomId,
                        SenderId = member.Id,
                        Content = $"msg-{i}",
                        CreatedAt = _host.Clock.UtcNow.AddSeconds(i)
                    })
                    .ToList();

                db.TextRoomMessages.AddRange(list);
                await db.SaveChangesAsync();
                return list.Select(m => m.Id).ToArray();
            });

            var page1 = await _host.With<ITextRoomService, Response<List<TextRoomMessageDto>>>(svc =>
                svc.GetMessagesAsync(room.RoomId, member.Id, beforeId: null, limit: 3));
            Assert.True(page1.Succeeded);
            Assert.Equal(seededIds[4..7], page1.Data!.Select(m => m.Id).ToArray());

            var page2 = await _host.With<ITextRoomService, Response<List<TextRoomMessageDto>>>(svc =>
                svc.GetMessagesAsync(room.RoomId, member.Id, beforeId: page1.Data![0].Id, limit: 3));
            Assert.True(page2.Succeeded);
            Assert.Equal(seededIds[1..4], page2.Data!.Select(m => m.Id).ToArray());

            var page3 = await _host.With<ITextRoomService, Response<List<TextRoomMessageDto>>>(svc =>
                svc.GetMessagesAsync(room.RoomId, member.Id, beforeId: page2.Data![0].Id, limit: 3));
            Assert.True(page3.Succeeded);
            Assert.Equal(seededIds[0..1], page3.Data!.Select(m => m.Id).ToArray());

            var page4 = await _host.With<ITextRoomService, Response<List<TextRoomMessageDto>>>(svc =>
                svc.GetMessagesAsync(room.RoomId, member.Id, beforeId: page3.Data![0].Id, limit: 3));
            Assert.True(page4.Succeeded);
            Assert.Empty(page4.Data!);
        }

        [Theory]
        [InlineData(0, 1)]
        [InlineData(1000, TextRoomLimits.HistoryPageMax)]
        public async Task GetMessages_OutOfBoundsLimit_ClampsToOneAndHistoryPageMax(int requestedLimit, int expectedCount)
        {
            var admin = await _host.CreateUserAsync(AppRoles.Admin);
            var member = await _host.CreateUserAsync(AppRoles.User);
            var room = await TextRoomTestFixtures.CreateRoomAsync(_host, TextRoomTestHost.AdminActor(admin));
            await TextRoomTestFixtures.JoinAndGoOnlineAsync(_host, room.RoomId, member.Id, "c1");

            const int totalMessages = TextRoomLimits.HistoryPageMax + 15;
            var allIds = await _host.WithDb(async db =>
            {
                var messages = Enumerable.Range(1, totalMessages)
                    .Select(i => new TextRoomMessage
                    {
                        TextRoomId = room.RoomId,
                        SenderId = member.Id,
                        Content = $"entry-{i}",
                        CreatedAt = _host.Clock.UtcNow.AddSeconds(i)
                    })
                    .ToList();

                db.TextRoomMessages.AddRange(messages);
                await db.SaveChangesAsync();
                return messages.Select(m => m.Id).ToArray();
            });

            var result = await _host.With<ITextRoomService, Response<List<TextRoomMessageDto>>>(svc =>
                svc.GetMessagesAsync(room.RoomId, member.Id, beforeId: null, limit: requestedLimit));

            Assert.True(result.Succeeded);
            Assert.Equal(expectedCount, result.Data!.Count);
            Assert.Equal(allIds[^expectedCount..], result.Data.Select(m => m.Id).ToArray());
        }
    }
}
