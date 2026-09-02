using System;
using System.IO;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace Cocorra.DAL.Data
{
    /// <summary>
    /// Design-time factory for <see cref="AppDbContext"/>, used by the <c>dotnet ef</c> tooling.
    ///
    /// <para>
    /// <b>Why this exists.</b> Without it, EF Tools fall back to building the application host to
    /// obtain a <c>DbContext</c>. That executes <c>Program.cs</c>, which deliberately throws when
    /// <c>Analytics:IpHashSalt</c> is absent — and it is absent by design, because the salt now
    /// comes from the environment rather than from tracked configuration. The result was that
    /// every migration command failed with a confusing two-part error: the salt guard, followed
    /// by "Unable to resolve service for type 'DbContextOptions&lt;AppDbContext&gt;'".
    /// </para>
    ///
    /// <para>
    /// EF prefers this factory over host-building, so <c>Program.cs</c> never runs during design
    /// time and the guard is never reached. <b>The guard itself is unchanged.</b> Weakening it —
    /// for example by only throwing in Production — was rejected: a development environment
    /// running without a salt would silently write reversible hashes, which is the exact failure
    /// the guard prevents.
    /// </para>
    ///
    /// <para>
    /// This factory needs only a connection string. It does not read the salt, does not construct
    /// the analytics pipeline, and does not start any background service.
    /// </para>
    /// </summary>
    public class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
    {
        private const string ConnectionName = "DefaultConnection";

        public AppDbContext CreateDbContext(string[] args)
        {
            var connectionString = ResolveConnectionString()
                ?? throw new InvalidOperationException(
                    $"Design-time connection string '{ConnectionName}' could not be resolved.\n" +
                    "Either run the command from the repository root or from Cocorra.API, or set it explicitly:\n" +
                    "  PowerShell:  $env:ConnectionStrings__DefaultConnection = '<connection string>'\n" +
                    "  bash:        export ConnectionStrings__DefaultConnection='<connection string>'");

            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlServer(connectionString, sql =>
                {
                    // Must match Program.cs, or `migrations add` writes into the wrong assembly
                    // and the new migration is invisible to the running application.
                    sql.MigrationsAssembly("Cocorra.DAL");
                })
                .Options;

            return new AppDbContext(options);
        }

        /// <summary>
        /// Environment variables win, so a deployer can point a migration at a specific database
        /// without editing a file. Otherwise the API project's appsettings.json is located by
        /// trying the directories `dotnet ef` is plausibly invoked from — the repository root,
        /// the API project itself, or a sibling project such as Cocorra.DAL.
        /// </summary>
        private static string? ResolveConnectionString()
        {
            var fromEnvironment = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection");
            if (!string.IsNullOrWhiteSpace(fromEnvironment))
            {
                return fromEnvironment;
            }

            var cwd = Directory.GetCurrentDirectory();

            var candidates = new[]
            {
                Path.Combine(cwd, "appsettings.json"),                          // run from Cocorra.API
                Path.Combine(cwd, "Cocorra.API", "appsettings.json"),           // run from the repo root
                Path.Combine(cwd, "..", "Cocorra.API", "appsettings.json"),     // run from a sibling project
                Path.Combine(AppContext.BaseDirectory, "appsettings.json")      // the build output
            };

            foreach (var path in candidates)
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                var value = new ConfigurationBuilder()
                    .AddJsonFile(Path.GetFullPath(path), optional: false)
                    .AddEnvironmentVariables()
                    .Build()
                    .GetConnectionString(ConnectionName);

                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            return null;
        }
    }
}
