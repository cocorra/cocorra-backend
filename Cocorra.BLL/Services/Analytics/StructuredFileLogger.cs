using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json;
using Cocorra.BLL.Services.EventTracking;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cocorra.BLL.Services.Analytics
{
    /// <summary>
    /// AN-042 — structured log sink.
    ///
    /// Today errors reach Docker stdout with 10MB/3-file rotation and nothing else: no
    /// structured sink, no APM, no metrics export. A failing invariant written only there is
    /// one nobody sees, and the analytics pipeline now emits several warnings that matter
    /// operationally — dropped events, dead-lettered batches, snapshot gaps.
    ///
    /// Deliberately minimal. This is not a logging framework: it is newline-delimited JSON on
    /// disk so a warning survives a restart and can be grepped or shipped. It is OFF unless
    /// Analytics:StructuredLogPath is set, so the default deployment behaves exactly as before.
    /// </summary>
    [ProviderAlias("StructuredFile")]
    public sealed class StructuredFileLoggerProvider : ILoggerProvider
    {
        private readonly string _path;
        private readonly LogLevel _minimumLevel;
        private readonly ConcurrentDictionary<string, StructuredFileLogger> _loggers = new();
        private readonly object _writeLock = new();
        private bool _writeFailureReported;

        public StructuredFileLoggerProvider(IOptions<EventTrackingOptions> options)
        {
            var settings = options.Value;
            _path = settings.StructuredLogPath ?? string.Empty;
            _minimumLevel = Enum.TryParse<LogLevel>(settings.StructuredLogMinimumLevel, ignoreCase: true, out var level)
                ? level
                : LogLevel.Warning;
        }

        public bool IsEnabled => !string.IsNullOrWhiteSpace(_path);

        public ILogger CreateLogger(string categoryName) =>
            _loggers.GetOrAdd(categoryName, name => new StructuredFileLogger(name, _minimumLevel, Write));

        private void Write(string line)
        {
            if (!IsEnabled)
            {
                return;
            }

            try
            {
                lock (_writeLock)
                {
                    var directory = Path.GetDirectoryName(_path);
                    if (!string.IsNullOrEmpty(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch (Exception)
            {
                // A logging sink must never take the application down, and must never recurse
                // into itself trying to report its own failure. Reported once to stdout, then
                // silent — a broken sink is a monitoring problem, not a request-path problem.
                if (!_writeFailureReported)
                {
                    _writeFailureReported = true;
                    Console.Error.WriteLine($"StructuredFileLogger: unable to write to '{_path}'. Structured logging is degraded.");
                }
            }
        }

        public void Dispose() => _loggers.Clear();
    }

    internal sealed class StructuredFileLogger : ILogger
    {
        private readonly string _category;
        private readonly LogLevel _minimumLevel;
        private readonly Action<string> _write;

        public StructuredFileLogger(string category, LogLevel minimumLevel, Action<string> write)
        {
            _category = category;
            _minimumLevel = minimumLevel;
            _write = write;
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

            var entry = new
            {
                timestampUtc = DateTime.UtcNow.ToString("O"),
                level = logLevel.ToString(),
                category = _category,
                eventId = eventId.Id,
                message = formatter(state, exception),
                exceptionType = exception?.GetType().FullName,
                exceptionMessage = exception?.Message,
                // Stack traces are the reason a structured sink is worth having at all: the
                // rotating stdout log loses them first.
                stackTrace = exception?.StackTrace
            };

            _write(JsonSerializer.Serialize(entry));
        }
    }
}
