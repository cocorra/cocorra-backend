using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Security.Claims;
using System.Threading.Channels;
using Cocorra.DAL.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cocorra.BLL.Services.SystemLogs
{
    /// <summary>
    /// Persists Warning-or-higher log entries so admins can browse them from the dashboard.
    ///
    /// Logging happens on request threads, so this only enqueues: <see cref="SystemLogFlushService"/>
    /// does the database work in batches. The channel drops its oldest entry when full, trading
    /// completeness for never blocking or failing the caller.
    /// </summary>
    [ProviderAlias("Database")]
    public sealed class DatabaseLoggerProvider : ILoggerProvider
    {
        private const string EntityFrameworkCategoryPrefix = "Microsoft.EntityFrameworkCore";
        private const int MaxTraceIdLength = 64;
        private const int MaxRequestMethodLength = 16;
        private const int MaxRequestPathLength = 512;

        // Persisting what EF Core or the sink's own services log would recurse: a failing
        // flush would log, and that log would be queued for the next failing flush.
        private static readonly string?[] SinkCategories =
        {
            typeof(SystemLogFlushService).FullName,
            typeof(SystemLogCleanupService).FullName
        };

        private readonly ChannelWriter<SystemLog> _queue;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly LogLevel _minimumLevel;
        private readonly ConcurrentDictionary<string, DatabaseLogger> _loggers = new();
        private bool _writeFailureReported;

        public DatabaseLoggerProvider(
            Channel<SystemLog> queue,
            IHttpContextAccessor httpContextAccessor,
            IOptions<SystemLogOptions> options)
        {
            _queue = queue.Writer;
            _httpContextAccessor = httpContextAccessor;
            _minimumLevel = Enum.TryParse<LogLevel>(options.Value.MinimumLevel, ignoreCase: true, out var level)
                ? level
                : LogLevel.Warning;
        }

        public ILogger CreateLogger(string categoryName) =>
            IsExcluded(categoryName)
                ? NullLogger.Instance
                : _loggers.GetOrAdd(categoryName, name => new DatabaseLogger(name, _minimumLevel, Write, ReportWriteFailure));

        public void Dispose() => _loggers.Clear();

        private static bool IsExcluded(string categoryName) =>
            categoryName.StartsWith(EntityFrameworkCategoryPrefix, StringComparison.Ordinal)
            || SinkCategories.Contains(categoryName);

        private void Write(SystemLog entry)
        {
            var httpContext = _httpContextAccessor.HttpContext;
            entry.TraceId = LogText.Truncate(Activity.Current?.Id ?? httpContext?.TraceIdentifier, MaxTraceIdLength);

            if (httpContext is not null)
            {
                entry.RequestMethod = LogText.Truncate(httpContext.Request.Method, MaxRequestMethodLength);
                entry.RequestPath = LogText.Truncate(httpContext.Request.Path.Value, MaxRequestPathLength);
                var userIdString = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                entry.UserId = Guid.TryParse(userIdString, out var userId) ? userId : null;
            }

            // DropOldest: a full queue evicts its oldest entry, so this write itself always lands.
            _queue.TryWrite(entry);
        }

        private void ReportWriteFailure()
        {
            // A logging sink must never take the application down, and must never recurse into
            // itself trying to report its own failure. Reported once to stderr, as the structured
            // file sink does, then silent.
            if (_writeFailureReported)
            {
                return;
            }

            _writeFailureReported = true;
            Console.Error.WriteLine("DatabaseLogger: unable to queue a log entry. Persisted system logs are degraded.");
        }
    }

    internal sealed class DatabaseLogger : ILogger
    {
        private const int MaxCategoryLength = 256;
        private const int MaxMessageLength = 4000;
        private const int MaxExceptionLength = 8000;

        private readonly string _category;
        private readonly LogLevel _minimumLevel;
        private readonly Action<SystemLog> _write;
        private readonly Action _reportWriteFailure;

        public DatabaseLogger(string category, LogLevel minimumLevel, Action<SystemLog> write, Action reportWriteFailure)
        {
            _category = category;
            _minimumLevel = minimumLevel;
            _write = write;
            _reportWriteFailure = reportWriteFailure;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= _minimumLevel && logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            try
            {
                _write(new SystemLog
                {
                    TimestampUtc = DateTime.UtcNow,
                    Level = logLevel.ToString(),
                    Category = LogText.Truncate(_category, MaxCategoryLength) ?? string.Empty,
                    // Id 0 is how Microsoft.Extensions.Logging spells "no event id".
                    EventId = eventId.Id == 0 ? null : eventId.Id,
                    Message = LogText.Truncate(formatter(state, exception), MaxMessageLength) ?? string.Empty,
                    Exception = LogText.Truncate(exception?.ToString(), MaxExceptionLength)
                });
            }
            catch (Exception)
            {
                // The formatter and the request context both run at an arbitrary point in the
                // request lifetime; neither may turn a log call into a failure of the caller.
                _reportWriteFailure();
            }
        }
    }
}
