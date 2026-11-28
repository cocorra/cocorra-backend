using System;
using System.Threading;
using System.Threading.Tasks;
using Cocorra.DAL.AppMetaData;
using Cocorra.DAL.Data;
using Cocorra.DAL.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Cocorra.BLL.Services.TextRooms
{
    public sealed class TextRoomSettingsProvider : ITextRoomSettingsProvider
    {
        private const string CacheKey = "textroom:global-settings";
        private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

        private readonly AppDbContext _db;
        private readonly IMemoryCache _cache;

        public TextRoomSettingsProvider(AppDbContext db, IMemoryCache cache)
        {
            _db = db;
            _cache = cache;
        }

        public async Task<TextRoomGlobalSettings> GetAsync(CancellationToken ct = default)
        {
            if (_cache.TryGetValue(CacheKey, out TextRoomGlobalSettings? cached) && cached is not null)
                return CloneSettings(cached);

            var globalSettings = await LoadOrSeedSingletonAsync(ct);

            _cache.Set(CacheKey, CloneSettings(globalSettings), new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = CacheDuration
            });

            return CloneSettings(globalSettings);
        }

        public void Invalidate() => _cache.Remove(CacheKey);

        public async Task<(int Before, int After)> GetReportContextWindowAsync(
            TextRoomSettings roomSettings, CancellationToken ct = default)
        {
            if (roomSettings.ReportContextMessagesBefore.HasValue && roomSettings.ReportContextMessagesAfter.HasValue)
            {
                return (
                    Math.Clamp(roomSettings.ReportContextMessagesBefore.Value, 0, TextRoomLimits.ReportContextMax),
                    Math.Clamp(roomSettings.ReportContextMessagesAfter.Value, 0, TextRoomLimits.ReportContextMax));
            }

            var global = await GetAsync(ct);
            var before = roomSettings.ReportContextMessagesBefore ?? global.ReportContextMessagesBefore;
            var after = roomSettings.ReportContextMessagesAfter ?? global.ReportContextMessagesAfter;

            return (
                Math.Clamp(before, 0, TextRoomLimits.ReportContextMax),
                Math.Clamp(after, 0, TextRoomLimits.ReportContextMax));
        }

        public async Task<int> GetRetentionHoursAsync(
            TextRoomSettings roomSettings, CancellationToken ct = default)
        {
            var retentionHours = roomSettings.MessageRetentionHours
                ?? (await GetAsync(ct)).DefaultMessageRetentionHours;

            return Math.Clamp(retentionHours, TextRoomLimits.RetentionHoursMin, TextRoomLimits.RetentionHoursMax);
        }

        private async Task<TextRoomGlobalSettings> LoadOrSeedSingletonAsync(CancellationToken ct)
        {
            var existing = await _db.TextRoomGlobalSettings
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == TextRoomGlobalSettings.SingletonId, ct);

            if (existing is not null)
                return existing;

            var seeded = new TextRoomGlobalSettings { Id = TextRoomGlobalSettings.SingletonId };
            _db.TextRoomGlobalSettings.Add(seeded);

            try
            {
                await _db.SaveChangesAsync(ct);
                _db.Entry(seeded).State = EntityState.Detached;
                return seeded;
            }
            catch (DbUpdateException)
            {
                // Detach so a concurrent first-read insert cannot poison subsequent saves on this DbContext.
                _db.Entry(seeded).State = EntityState.Detached;

                var reloaded = await _db.TextRoomGlobalSettings
                    .AsNoTracking()
                    .FirstOrDefaultAsync(x => x.Id == TextRoomGlobalSettings.SingletonId, ct);

                if (reloaded is not null)
                    return reloaded;

                throw;
            }
        }

        private static TextRoomGlobalSettings CloneSettings(TextRoomGlobalSettings source) => new()
        {
            Id = source.Id,
            Defaults = source.Defaults.Clone(),
            ReportContextMessagesBefore = source.ReportContextMessagesBefore,
            ReportContextMessagesAfter = source.ReportContextMessagesAfter,
            DefaultMessageRetentionHours = source.DefaultMessageRetentionHours,
            AllowModeratorsToManageRooms = source.AllowModeratorsToManageRooms,
            UpdatedAt = source.UpdatedAt,
            UpdatedById = source.UpdatedById
        };
    }
}
