using System;
using System.Linq;
using System.Threading.Tasks;
using Cocorra.DAL.Data;
using Microsoft.EntityFrameworkCore;

namespace Cocorra.BLL.Services.TextRooms
{
    internal static class TextRoomPresencePublisher
    {
        /// <summary>Broadcasts the room's current counts; a no-op when the room no longer exists.</summary>
        public static async Task PublishAsync(
            AppDbContext db, ITextRoomPresenceTracker presence, ITextRoomBroadcaster broadcaster, Guid roomId)
        {
            var showOnlineCount = await db.TextRooms
                .AsNoTracking()
                .Where(r => r.Id == roomId)
                .Select(r => (bool?)r.Settings.ShowOnlineCountToUsers)
                .FirstOrDefaultAsync();

            if (!showOnlineCount.HasValue)
                return;

            var totalParticipants = await db.TextRoomParticipants
                .AsNoTracking()
                .CountAsync(p => p.TextRoomId == roomId);

            await broadcaster.PresenceChangedAsync(
                roomId, presence.GetOnlineCount(roomId), totalParticipants, showOnlineCount.Value);
        }
    }
}
