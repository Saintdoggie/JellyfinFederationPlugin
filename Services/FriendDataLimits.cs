using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Owner side: what each friend may take from this server. Two per-friend
    /// limits live here, next to each other so they stay one model: a monthly
    /// data cap (bytes sent to the friend in the UTC calendar month, all paths:
    /// Direct play, the Peer/Play remote-transcode gateway, downloads) and a limit
    /// on streams at once (Direct plays and Peer/Play handoffs share it; downloads
    /// don't count). The global
    /// <see cref="Configuration.PluginConfiguration.MaxRemoteTranscodesPerFriend"/>
    /// still applies separately, in <see cref="RemoteTranscodeSessionRegistry"/>.
    /// </summary>
    public sealed class FriendDataLimits : IDisposable
    {
        /// <summary>Response header carrying why a request was refused.</summary>
        public const string RefusalHeader = "X-Federation-Refusal";

        /// <summary>Refusal reason: the monthly data cap is used up.</summary>
        public const string DataLimitReason = "data-limit";

        /// <summary>Refusal reason: the friend already has its allowed number of streams.</summary>
        public const string StreamLimitReason = "stream-limit";

        /// <summary>Bytes in one configured "GB" (decimal, as storage and ISPs count).</summary>
        public const long BytesPerGb = 1_000_000_000L;

        /// <summary>How long a stream with no request in flight still counts (a pause, a buffered-ahead player).</summary>
        internal static readonly TimeSpan ActiveWindow = TimeSpan.FromMinutes(2);

        private const int MonthsKept = 13;
        private static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(10);

        private readonly object _lock = new();
        private readonly object _saveLock = new();
        private readonly ILogger<FriendDataLimits>? _logger;
        private readonly Func<string?> _pathProvider;
        private readonly object _loadLock = new();
        private readonly ConcurrentDictionary<string, UsageCell> _usage = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<(string Friend, string Key), StreamEntry> _streams = new();
        private volatile bool _loaded;
        private int _dirty;
        private Timer? _saveTimer;
        private bool _disposed;

        /// <summary>Initializes a new instance of the <see cref="FriendDataLimits"/> class.</summary>
        public FriendDataLimits(ILogger<FriendDataLimits>? logger = null)
            : this(logger, () => Plugin.Instance?.DataFolderPath is { Length: > 0 } dir ? Path.Combine(dir, "federation-friend-usage.json") : null)
        {
        }

        internal FriendDataLimits(ILogger<FriendDataLimits>? logger, Func<string?> pathProvider)
        {
            _logger = logger;
            _pathProvider = pathProvider;
        }

        /// <summary>Clock, replaceable in tests.</summary>
        internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

        /// <summary>Converts a configured cap in GB to bytes (0 = no limit).</summary>
        public static long CapBytes(int gb) => gb <= 0 ? 0 : gb * BytesPerGb;

        /// <summary>Bytes sent to this friend so far in the current UTC month.</summary>
        public long UsedThisMonth(string friendId)
        {
            EnsureLoaded();
            return _usage.TryGetValue(friendId, out var cell) ? cell.Current(UtcNow()) : 0;
        }

        /// <summary>True when a cap is set and this month's total has reached it.</summary>
        public bool IsOverCap(string friendId, long capBytes) => capBytes > 0 && UsedThisMonth(friendId) >= capBytes;

        /// <summary>
        /// Books up to <paramref name="bytes"/> against this month's total and
        /// returns how many fit under <paramref name="capBytes"/> (all of them when
        /// there is no cap). With a cap the add is a compare-and-swap against the
        /// live total, so concurrent streams cannot each spend the whole remainder
        /// and nothing overshoots; without one it is a single atomic add. No lock
        /// and no file I/O on this path: totals reach disk from a background timer.
        /// </summary>
        public long Reserve(string friendId, long bytes, long capBytes)
        {
            if (bytes <= 0)
            {
                return 0;
            }

            EnsureLoaded();
            var cell = _usage.GetOrAdd(friendId, _ => new UsageCell());
            var granted = cell.Add(UtcNow(), bytes, capBytes);
            if (granted > 0)
            {
                Volatile.Write(ref _dirty, 1);
                EnsureSaveTimer();
            }

            return granted;
        }

        /// <summary>
        /// Wraps <paramref name="body"/> so every byte written is booked against
        /// the friend's cap. When the cap is reached mid-stream only the bytes that
        /// still fit are written, <paramref name="limitReached"/> is cancelled and
        /// the write throws, which stops the relay without it retrying.
        /// </summary>
        internal Stream Meter(Stream body, string friendId, long capBytes, CancellationTokenSource limitReached)
            => new MeteredStream(body, this, friendId, capBytes, limitReached);

        /// <summary>
        /// Registers a stream of <paramref name="friendId"/> identified by
        /// <paramref name="key"/> (one play, however many requests it makes). A key
        /// already active always passes; a new one is refused when
        /// <paramref name="limit"/> (0 = none) is reached and
        /// <paramref name="enforce"/> is set (a request that is mid-play, not the
        /// start of one, is registered but never refused). Checked and registered
        /// under one lock. The lease keeps the stream active while a request runs.
        /// </summary>
        public StreamLease? TryOpenStream(string friendId, string key, int limit, bool enforce = true)
        {
            lock (_lock)
            {
                var now = UtcNow();
                PruneStreams(now);
                var id = (friendId, key.ToLowerInvariant());
                var created = false;
                if (!_streams.TryGetValue(id, out var entry))
                {
                    if (enforce && limit > 0 && ActiveStreamsLocked(friendId, now) >= limit)
                    {
                        return null;
                    }

                    entry = new StreamEntry();
                    _streams[id] = entry;
                    created = true;
                }

                entry.LastSeenUtc = now;
                entry.InFlight++;
                return new StreamLease(this, id, created);
            }
        }

        /// <summary>Marks a known stream as alive (a pause ping) without a request.</summary>
        public void TouchStream(string friendId, string key)
        {
            lock (_lock)
            {
                if (_streams.TryGetValue((friendId, key.ToLowerInvariant()), out var entry))
                {
                    entry.LastSeenUtc = UtcNow();
                }
            }
        }

        /// <summary>Forgets a stream the friend stopped, so it stops counting at once.</summary>
        public void ForgetStream(string friendId, string key)
        {
            lock (_lock)
            {
                _streams.Remove((friendId, key.ToLowerInvariant()));
            }
        }

        /// <summary>Streams of this friend active right now.</summary>
        public int ActiveStreams(string friendId)
        {
            lock (_lock)
            {
                var now = UtcNow();
                PruneStreams(now);
                return ActiveStreamsLocked(friendId, now);
            }
        }

        /// <summary>Writes pending totals to disk now (shutdown, tests).</summary>
        public void Flush()
        {
            if (Volatile.Read(ref _dirty) != 0)
            {
                Save();
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            Timer? timer;
            lock (_loadLock)
            {
                _disposed = true;
                timer = _saveTimer;
                _saveTimer = null;
            }

            timer?.Dispose();
            Flush();
        }

        /// <summary>
        /// Answers a refused request: 429 with <see cref="RefusalHeader"/> and a
        /// JSON body naming the reason, so the friend's server (or a person) can
        /// tell a limit from a failure.
        /// </summary>
        public static async Task WriteRefusalAsync(HttpResponse response, string reason, DateTime utcNow, CancellationToken cancellationToken = default)
        {
            if (response.HasStarted)
            {
                return;
            }

            var message = reason == DataLimitReason
                ? "This server's monthly data limit for your server is used up. It resets at the start of next month (UTC)."
                : "This server's limit of streams at once for your server is reached. Stop another stream and try again.";
            response.StatusCode = StatusCodes.Status429TooManyRequests;
            response.Headers[RefusalHeader] = reason;
            if (reason == DataLimitReason)
            {
                response.Headers.RetryAfter = ((int)Math.Max(60, (NextMonthStart(utcNow) - utcNow).TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            response.ContentType = "application/json";
            var body = JsonSerializer.SerializeToUtf8Bytes(new { error = message, reason });
            response.ContentLength = body.Length;
            await response.Body.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Plain-English meaning of a refusal reason, for logs and error messages.</summary>
        public static string Describe(string? reason) => reason switch
        {
            DataLimitReason => "its monthly data limit for this server is used up (it resets at the start of next month, UTC)",
            StreamLimitReason => "its limit of streams at once for this server is reached",
            _ => "a limit set by that server's admin",
        };

        /// <summary>First instant of the UTC month after <paramref name="utcNow"/>.</summary>
        internal static DateTime NextMonthStart(DateTime utcNow)
            => new DateTime(utcNow.Year, utcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1);

        private static string MonthKey(DateTime utc) => utc.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);

        private static void TrimMonths(Dictionary<string, long> months, DateTime now)
        {
            if (months.Count <= MonthsKept)
            {
                return;
            }

            foreach (var old in months.Keys.OrderByDescending(k => k, StringComparer.Ordinal).Skip(MonthsKept).ToList())
            {
                months.Remove(old);
            }
        }

        private int ActiveStreamsLocked(string friendId, DateTime now)
        {
            var since = now - ActiveWindow;
            return _streams.Count(kv => kv.Key.Friend == friendId && (kv.Value.InFlight > 0 || kv.Value.LastSeenUtc >= since));
        }

        private void PruneStreams(DateTime now)
        {
            var since = now - ActiveWindow;
            foreach (var kv in _streams.Where(kv => kv.Value.InFlight <= 0 && kv.Value.LastSeenUtc < since).ToList())
            {
                _streams.Remove(kv.Key);
            }
        }

        private void Release((string Friend, string Key) id)
        {
            lock (_lock)
            {
                if (_streams.TryGetValue(id, out var entry))
                {
                    entry.InFlight = Math.Max(0, entry.InFlight - 1);
                    entry.LastSeenUtc = UtcNow();
                }
            }
        }

        private void EnsureSaveTimer()
        {
            if (_saveTimer != null)
            {
                return;
            }

            lock (_loadLock)
            {
                // The timer saves off the request path; it only does work when something changed.
                _saveTimer ??= _disposed ? null : new Timer(_ => Flush(), null, SaveInterval, SaveInterval);
            }
        }

        private void EnsureLoaded()
        {
            if (_loaded)
            {
                return;
            }

            lock (_loadLock)
            {
                if (_loaded)
                {
                    return;
                }

                var path = _pathProvider();
                if (string.IsNullOrEmpty(path))
                {
                    // No data folder yet (very early startup): keep counting in memory
                    // and try again next call, so totals aren't dropped. Nothing is
                    // marked for saving until a path exists.
                    return;
                }

                _loaded = true;
                try
                {
                    if (File.Exists(path))
                    {
                        var read = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, long>>>(File.ReadAllText(path));
                        if (read != null)
                        {
                            foreach (var (friend, months) in read)
                            {
                                _usage.GetOrAdd(friend, _ => new UsageCell()).Merge(months, UtcNow());
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "[Federation] Could not read the friends' monthly data totals; starting from zero");
                }

                // Anything counted before the folder resolved still needs writing.
                if (!_usage.IsEmpty)
                {
                    Volatile.Write(ref _dirty, 1);
                }
            }
        }

        private void Save()
        {
            var path = _pathProvider();
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            // _saveLock keeps two saves from racing each other; the unique temp
            // name means even an overlapping process can't corrupt this one's file.
            lock (_saveLock)
            {
                if (Interlocked.Exchange(ref _dirty, 0) == 0)
                {
                    return;
                }

                var now = UtcNow();
                var snapshot = new Dictionary<string, Dictionary<string, long>>();
                foreach (var kv in _usage)
                {
                    snapshot[kv.Key] = kv.Value.Snapshot(now);
                }

                var json = JsonSerializer.Serialize(snapshot);
                var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllText(tmp, json);
                    File.Move(tmp, path, overwrite: true);
                }
                catch (Exception ex)
                {
                    Volatile.Write(ref _dirty, 1);
                    try
                    {
                        File.Delete(tmp);
                    }
                    catch (IOException)
                    {
                    }

                    _logger?.LogWarning(ex, "[Federation] Could not save the friends' monthly data totals");
                }
            }
        }

        /// <summary>
        /// One friend's totals: the current month as a lock-free counter, earlier
        /// months in a small dictionary touched only at rollover, load and save.
        /// </summary>
        private sealed class UsageCell
        {
            private readonly object _gate = new();
            private readonly Dictionary<string, long> _past = new(StringComparer.Ordinal);
            private int _monthId = -1;
            private string _monthKey = string.Empty;
            private long _used;

            public long Current(DateTime now)
            {
                RollTo(now);
                return Interlocked.Read(ref _used);
            }

            public long Add(DateTime now, long bytes, long capBytes)
            {
                RollTo(now);
                if (capBytes <= 0)
                {
                    Interlocked.Add(ref _used, bytes);
                    return bytes;
                }

                while (true)
                {
                    var used = Interlocked.Read(ref _used);
                    var granted = Math.Clamp(capBytes - used, 0, bytes);
                    if (granted == 0 || Interlocked.CompareExchange(ref _used, used + granted, used) == used)
                    {
                        return granted;
                    }
                }
            }

            public void Merge(Dictionary<string, long> months, DateTime now)
            {
                RollTo(now);
                lock (_gate)
                {
                    foreach (var (month, bytes) in months)
                    {
                        if (month == _monthKey)
                        {
                            Interlocked.Add(ref _used, bytes);
                        }
                        else
                        {
                            _past[month] = _past.TryGetValue(month, out var have) ? have + bytes : bytes;
                        }
                    }
                }
            }

            public Dictionary<string, long> Snapshot(DateTime now)
            {
                RollTo(now);
                lock (_gate)
                {
                    var copy = new Dictionary<string, long>(_past, StringComparer.Ordinal);
                    var used = Interlocked.Read(ref _used);
                    if (used > 0)
                    {
                        copy[_monthKey] = used;
                    }

                    TrimMonths(copy, now);
                    return copy;
                }
            }

            private void RollTo(DateTime now)
            {
                var id = (now.Year * 12) + now.Month;
                if (Volatile.Read(ref _monthId) == id)
                {
                    return;
                }

                lock (_gate)
                {
                    if (_monthId == id)
                    {
                        return;
                    }

                    var key = MonthKey(now);
                    if (_monthId >= 0)
                    {
                        _past[_monthKey] = Interlocked.Read(ref _used);
                    }

                    _past.TryGetValue(key, out var carried);
                    _past.Remove(key);
                    Interlocked.Exchange(ref _used, carried);
                    _monthKey = key;
                    Volatile.Write(ref _monthId, id);
                }
            }
        }

        /// <summary>A registered stream; disposing it ends one request's hold on it.</summary>
        public sealed class StreamLease : IDisposable
        {
            private readonly FriendDataLimits _owner;
            private readonly (string Friend, string Key) _id;
            private int _released;

            internal StreamLease(FriendDataLimits owner, (string Friend, string Key) id, bool created)
            {
                _owner = owner;
                _id = id;
                Created = created;
            }

            /// <summary>Gets a value indicating whether this request started the stream (it was not already counted).</summary>
            public bool Created { get; }

            /// <inheritdoc />
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _released, 1) == 0)
                {
                    _owner.Release(_id);
                }
            }
        }

        private sealed class StreamEntry
        {
            public int InFlight { get; set; }

            public DateTime LastSeenUtc { get; set; }
        }

        private sealed class MeteredStream : Stream
        {
            private readonly Stream _inner;
            private readonly FriendDataLimits _owner;
            private readonly string _friendId;
            private readonly long _capBytes;
            private readonly CancellationTokenSource _limitReached;

            public MeteredStream(Stream inner, FriendDataLimits owner, string friendId, long capBytes, CancellationTokenSource limitReached)
            {
                _inner = inner;
                _owner = owner;
                _friendId = friendId;
                _capBytes = capBytes;
                _limitReached = limitReached;
            }

            public override bool CanRead => false;

            public override bool CanSeek => false;

            public override bool CanWrite => true;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Flush() => _inner.Flush();

            public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count)
            {
                var granted = (int)_owner.Reserve(_friendId, count, _capBytes);
                if (granted > 0)
                {
                    _inner.Write(buffer, offset, granted);
                }

                if (granted < count)
                {
                    StopAtCap();
                }
            }

            public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                var granted = (int)_owner.Reserve(_friendId, buffer.Length, _capBytes);
                if (granted > 0)
                {
                    await _inner.WriteAsync(buffer.Slice(0, granted), cancellationToken).ConfigureAwait(false);
                }

                if (granted < buffer.Length)
                {
                    StopAtCap();
                }
            }

            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
                => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

            private void StopAtCap()
            {
                _limitReached.Cancel();
                throw new OperationCanceledException(_limitReached.Token);
            }
        }
    }
}
