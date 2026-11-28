using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Cocorra.BLL.Base;
using Cocorra.BLL.Services.AuditLogs;
using Cocorra.BLL.Services.ChatService;
using Cocorra.BLL.Services.ContentModeration;
using Cocorra.BLL.Services.EventTracking;
using Cocorra.BLL.Services.TextRooms;
using Cocorra.DAL.Data;
using Cocorra.DAL.DTOS.LogsDto;
using Cocorra.DAL.DTOS.TextRoomDto;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Cocorra.Tests.Helpers
{
    /// <summary>
    /// Real text-room and content-moderation services over SQLite and real Identity, resolved
    /// through their DI extension methods so tests never depend on constructor shapes. Transport
    /// (broadcaster), audit, chat and clock are replaced by recording fakes.
    /// A file database with one connection per DbContext, not a shared in-memory connection:
    /// SqliteConnection is not thread-safe, and the concurrency tests need real parallel writers.
    /// </summary>
    public sealed class TextRoomTestHost : IDisposable
    {
        public static readonly DateTimeOffset DefaultNow = new(2026, 11, 28, 12, 0, 0, TimeSpan.Zero);

        private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"cocorra-textrooms-{Guid.NewGuid():N}.db");
        private readonly ServiceProvider _services;

        public MutableTimeProvider Clock { get; } = new(DefaultNow);
        public RecordingTextRoomBroadcaster Broadcaster { get; } = new();
        public RecordingAuditLogService Audit { get; } = new();
        public Mock<IChatService> Chat { get; } = new();

        public TextRoomTestHost(Action<IServiceCollection>? configure = null)
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = _databasePath,
                Pooling = false,
                DefaultTimeout = 30
            }.ToString();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddMemoryCache();
            services.AddDbContext<AppDbContext>(o => o.UseSqlite(connectionString));
            services.AddIdentityCore<ApplicationUser>()
                .AddRoles<IdentityRole<Guid>>()
                .AddEntityFrameworkStores<AppDbContext>();

            services.AddSingleton<TimeProvider>(Clock);
            services.AddSingleton<ITextRoomBroadcaster>(Broadcaster);
            services.AddSingleton<IAuditLogService>(Audit);
            services.AddSingleton(Chat.Object);
            services.AddSingleton(Mock.Of<IEventTracker>());

            services.AddTextRoomEngine();
            services.AddContentModeration();

            configure?.Invoke(services);

            _services = services.BuildServiceProvider();

            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Database.EnsureCreated();
            db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
            foreach (var role in AppRoles.All)
                roles.CreateAsync(new IdentityRole<Guid>(role)).GetAwaiter().GetResult();
        }

        public IServiceProvider Services => _services;

        public AsyncServiceScope CreateScope() => _services.CreateAsyncScope();

        /// <summary>Runs <paramref name="action"/> against a service resolved in a fresh scope.</summary>
        public async Task<TResult> With<TService, TResult>(Func<TService, Task<TResult>> action) where TService : notnull
        {
            await using var scope = CreateScope();
            return await action(scope.ServiceProvider.GetRequiredService<TService>());
        }

        public async Task WithDb(Func<AppDbContext, Task> action)
        {
            await using var scope = CreateScope();
            await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
        }

        public async Task<TResult> WithDb<TResult>(Func<AppDbContext, Task<TResult>> action)
        {
            await using var scope = CreateScope();
            return await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
        }

        public async Task<ApplicationUser> CreateUserAsync(
            string role = AppRoles.User, string firstName = "Test", string lastName = "User")
        {
            await using var scope = CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = new ApplicationUser
            {
                Id = Guid.NewGuid(),
                UserName = $"u{Guid.NewGuid():N}",
                Email = $"{Guid.NewGuid():N}@example.test",
                FirstName = firstName,
                LastName = lastName,
                Status = UserStatus.Active
            };
            var created = await users.CreateAsync(user);
            if (!created.Succeeded)
                throw new InvalidOperationException(string.Join("; ", created.Errors.Select(e => e.Description)));
            await users.AddToRoleAsync(user, role);
            return user;
        }

        public static StaffActor AdminActor(ApplicationUser user) => new(user.Id, IsAdmin: true, AppRoles.Admin);

        public static StaffActor ModeratorActor(ApplicationUser user) => new(user.Id, IsAdmin: false, AppRoles.Moderator);

        public void Dispose()
        {
            _services.Dispose();
            foreach (var path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
            {
                try { File.Delete(path); }
                catch (IOException) { /* a leftover temp file is harmless */ }
            }
        }
    }

    public sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;

        public MutableTimeProvider(DateTimeOffset utcNow) => _utcNow = utcNow.ToUniversalTime();

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public DateTime UtcNow => _utcNow.UtcDateTime;

        public void Advance(TimeSpan by) => _utcNow = _utcNow.Add(by);

        public void Set(DateTimeOffset utcNow) => _utcNow = utcNow.ToUniversalTime();
    }

    public sealed class RecordingAuditLogService : IAuditLogService
    {
        private readonly ConcurrentQueue<AuditEntry> _entries = new();

        public IReadOnlyList<AuditEntry> Entries => _entries.ToList();

        public IReadOnlyList<AuditEntry> ByAction(string action) => _entries.Where(e => e.Action == action).ToList();

        public Task RecordAsync(AuditEntry entry)
        {
            _entries.Enqueue(entry);
            return Task.CompletedTask;
        }

        public Task<Response<IEnumerable<AuditLogDto>>> GetAuditLogsAsync(AuditLogQueryDto query) =>
            throw new NotSupportedException("Audit reads are not part of the text-room test surface.");
    }

    /// <summary>Each call is recorded as (method name, arguments) in order.</summary>
    public sealed class RecordingTextRoomBroadcaster : ITextRoomBroadcaster
    {
        private readonly ConcurrentQueue<(string Method, object?[] Args)> _calls = new();

        public IReadOnlyList<(string Method, object?[] Args)> Calls => _calls.ToList();

        public IReadOnlyList<object?[]> CallsTo(string method) =>
            _calls.Where(c => c.Method == method).Select(c => c.Args).ToList();

        private Task Record(string method, params object?[] args)
        {
            _calls.Enqueue((method, args));
            return Task.CompletedTask;
        }

        public Task MessageReceivedAsync(TextRoomMessageDto message) => Record(nameof(MessageReceivedAsync), message);
        public Task MessageDeletedAsync(Guid roomId, long messageId) => Record(nameof(MessageDeletedAsync), roomId, messageId);
        public Task ConfigUpdatedAsync(TextRoomClientConfigDto config) => Record(nameof(ConfigUpdatedAsync), config);
        public Task PresenceChangedAsync(Guid roomId, int onlineCount, int totalParticipants, bool visibleToUsers) =>
            Record(nameof(PresenceChangedAsync), roomId, onlineCount, totalParticipants, visibleToUsers);
        public Task RoomClosedAsync(Guid roomId, string reason) => Record(nameof(RoomClosedAsync), roomId, reason);
        public Task HistoryClearedAsync(Guid roomId) => Record(nameof(HistoryClearedAsync), roomId);
        public Task UserMutedAsync(Guid roomId, Guid userId, DateTime mutedUntil, string reason) =>
            Record(nameof(UserMutedAsync), roomId, userId, mutedUntil, reason);
        public Task UserKickedAsync(Guid roomId, Guid userId, string reason, IReadOnlyList<string> connectionIds) =>
            Record(nameof(UserKickedAsync), roomId, userId, reason, connectionIds);
        public Task RoomListChangedAsync() => Record(nameof(RoomListChangedAsync));
    }
}
