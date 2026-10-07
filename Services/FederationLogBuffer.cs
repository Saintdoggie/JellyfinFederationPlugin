using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// One captured federation log line for the dashboard Log tab.
    /// </summary>
    public sealed class FederationLogEntry
    {
        public long Id { get; init; }

        public DateTime TimestampUtc { get; init; }

        public string Level { get; init; } = "Information";

        public string Category { get; init; } = string.Empty;

        public string Message { get; init; } = string.Empty;

        public string? Detail { get; init; }
    }

    /// <summary>
    /// In-memory ring of recent plugin log lines. The dashboard Log tab reads
    /// this instead of the Jellyfin server log so routine friend-offline and
    /// timeout noise stays in one quiet place.
    /// </summary>
    public sealed class FederationLogBuffer
    {
        public const int Capacity = 800;

        private readonly object _sync = new();
        private readonly Queue<FederationLogEntry> _entries = new();
        private long _nextId = 1;

        public bool ProviderAttached { get; set; }

        public void Append(LogLevel level, string category, string message, Exception? exception)
        {
            if (string.IsNullOrEmpty(message) && exception == null)
            {
                return;
            }

            var mapped = MapLevel(level);
            var entry = new FederationLogEntry
            {
                Id = 0,
                TimestampUtc = DateTime.UtcNow,
                Level = mapped,
                Category = ShortCategory(category),
                Message = Truncate(message, 2000),
                Detail = FormatDetail(mapped, exception)
            };

            lock (_sync)
            {
                entry = new FederationLogEntry
                {
                    Id = _nextId++,
                    TimestampUtc = entry.TimestampUtc,
                    Level = entry.Level,
                    Category = entry.Category,
                    Message = entry.Message,
                    Detail = entry.Detail
                };
                _entries.Enqueue(entry);
                while (_entries.Count > Capacity)
                {
                    _entries.Dequeue();
                }
            }
        }

        public IReadOnlyList<FederationLogEntry> Snapshot(string? minLevel, long afterId, int limit)
        {
            var minRank = Rank(NormalizeLevel(minLevel) ?? "Debug");
            if (limit <= 0 || limit > Capacity)
            {
                limit = 200;
            }

            lock (_sync)
            {
                return _entries
                    .Where(e => e.Id > afterId && Rank(e.Level) >= minRank)
                    .TakeLast(limit)
                    .ToList();
            }
        }

        public void Clear()
        {
            lock (_sync)
            {
                _entries.Clear();
            }
        }

        internal static string MapLevel(LogLevel level)
        {
            return level switch
            {
                LogLevel.Trace or LogLevel.Debug => "Debug",
                LogLevel.Information => "Information",
                LogLevel.Warning => "Warning",
                LogLevel.Error or LogLevel.Critical => "Error",
                _ => "Information"
            };
        }

        internal static int Rank(string level)
        {
            return level switch
            {
                "Debug" => 0,
                "Information" => 1,
                "Warning" => 2,
                "Error" => 3,
                _ => 1
            };
        }

        internal static string ShortCategory(string category)
        {
            if (string.IsNullOrEmpty(category))
            {
                return "Federation";
            }

            var last = category.LastIndexOf('.');
            return last >= 0 && last < category.Length - 1 ? category[(last + 1)..] : category;
        }

        private static string? NormalizeLevel(string? minLevel)
        {
            if (string.IsNullOrWhiteSpace(minLevel))
            {
                return null;
            }

            return minLevel.Trim() switch
            {
                "debug" or "Debug" or "Trace" => "Debug",
                "info" or "information" or "Information" => "Information",
                "warn" or "warning" or "Warning" => "Warning",
                "error" or "Error" or "critical" or "Critical" => "Error",
                _ => "Debug"
            };
        }

        private static string Truncate(string value, int max)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= max)
            {
                return value ?? string.Empty;
            }

            return value.Substring(0, max);
        }

        private static string? FormatDetail(string level, Exception? exception)
        {
            if (exception == null)
            {
                return null;
            }

            var text = exception.GetType().Name + ": " + exception.Message;
            if (level == "Error" && exception.StackTrace != null)
            {
                var lines = exception.StackTrace.Split('\n');
                var take = Math.Min(3, lines.Length);
                text += "\n" + string.Join('\n', lines.Take(take)).Trim();
            }

            return Truncate(text, level == "Error" ? 1200 : 400);
        }
    }

    /// <summary>
    /// Captures log lines from this plugin's categories into
    /// <see cref="FederationLogBuffer"/>. Attached once on plugin startup.
    /// </summary>
    public sealed class FederationLoggerProvider : ILoggerProvider
    {
        private readonly FederationLogBuffer _buffer;

        public FederationLoggerProvider(FederationLogBuffer buffer)
        {
            _buffer = buffer;
        }

        public ILogger CreateLogger(string categoryName)
        {
            if (string.IsNullOrEmpty(categoryName)
                || categoryName.IndexOf("Federation", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return NullLogger.Instance;
            }

            return new FederationCaptureLogger(_buffer, categoryName);
        }

        public void Dispose()
        {
        }
    }

    internal sealed class FederationCaptureLogger : ILogger
    {
        private readonly FederationLogBuffer _buffer;
        private readonly string _category;

        public FederationCaptureLogger(FederationLogBuffer buffer, string category)
        {
            _buffer = buffer;
            _category = category;
        }

        IDisposable ILogger.BeginScope<TState>(TState state) => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information && logLevel != LogLevel.None;

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

            string message;
            try
            {
                message = formatter(state, exception) ?? string.Empty;
            }
            catch (Exception)
            {
                message = state?.ToString() ?? string.Empty;
            }

            _buffer.Append(logLevel, _category, message, exception);
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
