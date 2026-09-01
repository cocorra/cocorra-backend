using System;
using Cocorra.DAL.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cocorra.Tests.Helpers
{
    /// <summary>
    /// Canonical SQLite-backed test host for anything that depends on a database constraint.
    ///
    /// B-5: Microsoft.EntityFrameworkCore.InMemory does NOT enforce unique indexes or
    /// DeleteBehavior, so an idempotency or duplicate-rejection test written against it
    /// passes whether or not the constraint exists. Use this host for those tests. The
    /// InMemory provider remains fine for pure query-shape tests.
    /// </summary>
    public sealed class SqliteTestHost : IDisposable
    {
        public const string SqliteProviderName = "Microsoft.EntityFrameworkCore.Sqlite";

        private readonly SqliteConnection _connection;
        private readonly ServiceProvider _services;

        public SqliteTestHost()
        {
            _connection = new SqliteConnection("DataSource=:memory:");
            _connection.Open();

            var services = new ServiceCollection();
            services.AddDbContext<AppDbContext>(options => options.UseSqlite(_connection));
            _services = services.BuildServiceProvider();

            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Database.EnsureCreated();
        }

        /// <summary>Root provider to hand to services that resolve a scoped AppDbContext themselves.</summary>
        public IServiceProvider Services => _services;

        public IServiceScope CreateScope() => _services.CreateScope();

        public void Dispose()
        {
            _services.Dispose();
            _connection.Dispose();
        }
    }
}
