using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Federation.Services.DirectLink
{
    /// <summary>
    /// Read-ahead for the direct-link relay. A player fetches one segment at a
    /// time, and one download from a friend can stop well short of the line's
    /// speed (live, 2026-10-22: about 110 Mbps on one download to friend-a, 178
    /// with four at once). While the player gets segment N, the next few are
    /// fetched over the link at the same time and held here; the player's request
    /// for one takes it (joining it while it is still arriving), so playback gets
    /// the speed of several downloads.
    /// <list type="bullet">
    /// <item>Only segments listed in a playlist the relay passed on, from the same
    /// friend, at the exact address the player will ask for (which carries the
    /// friend's play token). Each held segment is served once.</item>
    /// <item>Fetches start only once the friend has answered for segment N, so its
    /// conversion is already running there and the next segments are within the
    /// distance Jellyfin waits for rather than restarting at.</item>
    /// <item>A request outside the window (a seek) cancels what was fetched for
    /// the old position. Unclaimed segments go after <see cref="Keep"/>.</item>
    /// <item>Memory is capped globally and shared between live entries: producers
    /// pause until the player makes room, whether or not their entry is claimed.</item>
    /// </list>
    /// </summary>
    public sealed class SegmentReadAhead : IDisposable
    {
        /// <summary>Most segments fetched ahead of the player.</summary>
        public const int MaxDepth = 6;

        /// <summary>How long a held segment waits for the player.</summary>
        public static readonly TimeSpan Keep = TimeSpan.FromSeconds(60);

        private const int ChunkSize = 256 * 1024;
        private const int MaxPlaylists = 16;

        // A playlist this long (25 hours of 3 s segments) isn't a film: no read-ahead,
        // so a friend's oversized playlist can't fill memory here.
        private const int MaxSegmentsPerPlaylist = 30000;
        private const int MaxTargetCharsPerPlaylist = 1024 * 1024;
        private const int MaxEntries = 4 * MaxDepth;
        private const long MaxSegmentBytes = 512L * 1024 * 1024;
        private static readonly TimeSpan HeadTimeout = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan PlaylistKeep = TimeSpan.FromHours(6);

        private readonly object _gate = new();
        private readonly Dictionary<string, Playlist> _playlists = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (Playlist List, int Index)> _positions = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
        private readonly HashSet<Entry> _liveEntries = new();
        private readonly long _maxBytes;
        private readonly long _entryMaxBytes;
        private readonly Func<DateTime> _now;
        private readonly Timer? _sweeper;
        private long _bytes;
        private long _served;
        private long _missed;

        /// <summary>Initializes a new instance of the <see cref="SegmentReadAhead"/> class.</summary>
        /// <param name="maxBytes">Most bytes held across all claimed and unclaimed segments.</param>
        /// <param name="now">Clock (tests).</param>
        public SegmentReadAhead(long maxBytes = 384L * 1024 * 1024, Func<DateTime>? now = null)
        {
            _maxBytes = maxBytes;
            _entryMaxBytes = Math.Max(1, maxBytes / MaxEntries);
            _now = now ?? (() => DateTime.UtcNow);
            if (now == null)
            {
                _sweeper = new Timer(_ => Sweep(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
            }
        }

        /// <summary>Gets the instance the relay uses.</summary>
        public static SegmentReadAhead Shared { get; } = new();

        /// <summary>Gets the bytes held now (tests, status).</summary>
        public long HeldBytes => Interlocked.Read(ref _bytes);

        /// <summary>Gets how many listed segments were taken from read-ahead.</summary>
        public long Served => Interlocked.Read(ref _served);

        /// <summary>Gets how many listed segments the player had to ask the friend for itself.</summary>
        public long Missed => Interlocked.Read(ref _missed);

        /// <summary>Gets the number of segments fetched ahead and not yet claimed.</summary>
        public int Pending
        {
            get
            {
                lock (_gate)
                {
                    return _entries.Count;
                }
            }
        }

        /// <summary>
        /// Notes the segments of a playlist the relay passed on.
        /// <paramref name="playlistTarget"/> is its address on the link (from the
        /// friend gateway path on); entries are resolved against it.
        /// </summary>
        public void RememberPlaylist(string serverId, string playlistTarget, string text)
        {
            Uri origin;
            try
            {
                origin = new Uri("http://federation-direct-link" + playlistTarget);
            }
            catch (UriFormatException)
            {
                return;
            }

            var targets = new List<string>();
            long chars = 0;
            foreach (var raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#') || !Uri.TryCreate(origin, line, out var resolved))
                {
                    continue;
                }

                var pathAndQuery = resolved.PathAndQuery;
                var at = pathAndQuery.IndexOf(DirectLinkRelay.GatewayPath, StringComparison.OrdinalIgnoreCase);
                if (at < 0 || resolved.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                targets.Add(pathAndQuery.Substring(at));
                chars += pathAndQuery.Length - at;
                if (targets.Count > MaxSegmentsPerPlaylist || chars > MaxTargetCharsPerPlaylist)
                {
                    return;
                }
            }

            if (targets.Count < 2)
            {
                return;
            }

            var listKey = Key(serverId, playlistTarget);
            lock (_gate)
            {
                if (_playlists.Remove(listKey, out var old))
                {
                    Forget(old);
                }

                while (_playlists.Count >= MaxPlaylists)
                {
                    Forget(_playlists.Values.OrderBy(p => p.UsedUtc).First());
                }

                var list = new Playlist(listKey, serverId, targets.ToArray()) { UsedUtc = _now() };
                _playlists[listKey] = list;
                for (var i = 0; i < list.Targets.Length; i++)
                {
                    _positions[Key(serverId, list.Targets[i])] = (list, i);
                }
            }
        }

        /// <summary>
        /// Takes the held (or still arriving) segment for <paramref name="target"/>,
        /// or null. The caller serves it with <see cref="ServeAsync"/> and then
        /// disposes it.
        /// </summary>
        /// <returns>The segment, or null.</returns>
        public Entry? Take(string serverId, string target)
        {
            lock (_gate)
            {
                var key = Key(serverId, target);
                if (!_entries.Remove(key, out var entry))
                {
                    // Counted only for segments of a known playlist past its first.
                    if (_positions.TryGetValue(key, out var at) && at.Index > 0)
                    {
                        _missed++;
                    }

                    return null;
                }

                _served++;
                entry.Claim();
                return entry;
            }
        }

        /// <summary>
        /// The friend has answered for <paramref name="target"/> (the player's
        /// segment): fetch the next <paramref name="depth"/> with
        /// <paramref name="fetch"/> (a link address in, the friend's answer out),
        /// and drop what was fetched for any other position.
        /// </summary>
        public void After(string serverId, string target, int depth, Func<string, CancellationToken, Task<HttpResponseMessage>> fetch)
        {
            depth = Math.Clamp(depth, 0, MaxDepth);
            var started = new List<Entry>();
            lock (_gate)
            {
                if (!_positions.TryGetValue(Key(serverId, target), out var at))
                {
                    return;
                }

                var (list, index) = at;
                list.UsedUtc = _now();
                foreach (var stale in _entries.Values.Where(e => e.List == list && (e.Index <= index || e.Index > index + depth)).ToList())
                {
                    _entries.Remove(stale.Key);
                    stale.Dispose();
                }

                for (var i = index + 1; i <= index + depth && i < list.Targets.Length && _liveEntries.Count < MaxEntries; i++)
                {
                    var key = Key(serverId, list.Targets[i]);
                    if (!_entries.ContainsKey(key))
                    {
                        var entry = new Entry(this, key, list, i, list.Targets[i], _now());
                        _entries[key] = entry;
                        _liveEntries.Add(entry);
                        started.Add(entry);
                    }
                }
            }

            foreach (var entry in started)
            {
                _ = Task.Run(() => ProduceAsync(entry, fetch));
            }
        }

        /// <summary>Drops unclaimed segments older than <see cref="Keep"/> and playlists unused for hours.</summary>
        public void Sweep()
        {
            var now = _now();
            lock (_gate)
            {
                foreach (var old in _entries.Values.Where(e => now - e.CreatedUtc > Keep).ToList())
                {
                    _entries.Remove(old.Key);
                    old.Dispose();
                }

                foreach (var idle in _playlists.Values.Where(p => now - p.UsedUtc > PlaylistKeep).ToList())
                {
                    Forget(idle);
                }
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            _sweeper?.Dispose();
            lock (_gate)
            {
                foreach (var entry in _entries.Values)
                {
                    entry.Dispose();
                }

                _entries.Clear();
                _liveEntries.Clear();
                _playlists.Clear();
                _positions.Clear();
            }
        }

        // Equivalent escaping still meets (for example, a comma and %2C), but an
        // escaped delimiter inside a query value is not a real separator. Decode and
        // re-encode each path segment and query name/value independently so those two
        // cases cannot alias each other.
        private static string Key(string serverId, string target) => serverId + "\n" + CanonicalTarget(target);

        private static string CanonicalTarget(string target)
        {
            var queryAt = target.IndexOf('?');
            var path = queryAt < 0 ? target : target.Substring(0, queryAt);
            var canonicalPath = string.Join("/", path.Split('/').Select(CanonicalComponent));
            if (queryAt < 0)
            {
                return canonicalPath;
            }

            var query = target.Substring(queryAt + 1);
            return canonicalPath + "?" + string.Join("&", query.Split('&').Select(CanonicalQueryPart));
        }

        private static string CanonicalQueryPart(string part)
        {
            var equals = part.IndexOf('=');
            return equals < 0
                ? CanonicalComponent(part)
                : CanonicalComponent(part.Substring(0, equals)) + "=" + CanonicalComponent(part.Substring(equals + 1));
        }

        private static string CanonicalComponent(string value)
            => Uri.EscapeDataString(Uri.UnescapeDataString(value));

        private void Forget(Playlist list)
        {
            _playlists.Remove(list.Key);
            foreach (var target in list.Targets)
            {
                var key = Key(list.ServerId, target);
                if (_positions.TryGetValue(key, out var at) && at.List == list)
                {
                    _positions.Remove(key);
                }
            }

            foreach (var entry in _entries.Values.Where(e => e.List == list).ToList())
            {
                _entries.Remove(entry.Key);
                entry.Dispose();
            }
        }

        private async Task ProduceAsync(Entry entry, Func<string, CancellationToken, Task<HttpResponseMessage>> fetch)
        {
            try
            {
                HttpResponseMessage response;
                using (var head = CancellationTokenSource.CreateLinkedTokenSource(entry.Stopping))
                {
                    head.CancelAfter(HeadTimeout);
                    response = await fetch(entry.Target, head.Token).ConfigureAwait(false);
                }

                using (response)
                {
                    if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength > MaxSegmentBytes)
                    {
                        throw new IOException("the friend didn't send the segment");
                    }

                    entry.SetHead(response);
                    await using var body = await response.Content.ReadAsStreamAsync(entry.Stopping).ConfigureAwait(false);
                    long total = 0;
                    while (true)
                    {
                        var buffer = new byte[ChunkSize];
                        var read = await body.ReadAsync(buffer.AsMemory(0, (int)Math.Min(ChunkSize, _entryMaxBytes)), entry.Stopping).ConfigureAwait(false);
                        if (read == 0)
                        {
                            break;
                        }

                        total += read;
                        if (total > MaxSegmentBytes)
                        {
                            throw new IOException("segment too large to hold");
                        }

                        // Every live entry has an equal share of the global budget,
                        // so an unclaimed producer cannot fill the budget and starve
                        // a claimed producer whose reader is draining its share.
                        var chunk = read == buffer.Length ? buffer : buffer.AsSpan(0, read).ToArray();
                        while (!TryAddWithBudget(entry, chunk))
                        {
                            await Task.Delay(50, entry.Stopping).ConfigureAwait(false);
                        }
                    }

                    if (response.Content.Headers.ContentLength is { } declared && total != declared)
                    {
                        throw new IOException("segment length didn't match its Content-Length");
                    }

                    entry.Finish(null);
                }
            }
            catch (Exception ex)
            {
                entry.Finish(ex as IOException ?? new IOException("read-ahead stopped", ex));
                lock (_gate)
                {
                    // Not claimed yet: let the player's request go to the friend itself.
                    if (_entries.TryGetValue(entry.Key, out var held) && held == entry)
                    {
                        _entries.Remove(entry.Key);
                        entry.Dispose();
                    }
                }
            }
        }

        private bool TryAddWithBudget(Entry entry, byte[] chunk)
        {
            lock (_gate)
            {
                if (Interlocked.Read(ref _bytes) + chunk.Length > _maxBytes)
                {
                    return false;
                }

                return entry.Add(chunk, _entryMaxBytes);
            }
        }

        private void RemoveLiveEntry(Entry entry)
        {
            lock (_gate)
            {
                _liveEntries.Remove(entry);
            }
        }

        private void Account(long bytes) => Interlocked.Add(ref _bytes, bytes);

        private sealed class Playlist
        {
            public Playlist(string key, string serverId, string[] targets)
            {
                Key = key;
                ServerId = serverId;
                Targets = targets;
            }

            public string Key { get; }

            public string ServerId { get; }

            public string[] Targets { get; }

            public DateTime UsedUtc { get; set; }
        }

        /// <summary>One segment fetched ahead of the player.</summary>
        public sealed class Entry : IDisposable
        {
            private readonly SegmentReadAhead _owner;
            private readonly CancellationTokenSource _stop = new();
            private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleWriter = true });
            private readonly TaskCompletionSource<bool> _head = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly object _lock = new();
            private long _buffered;
            private bool _disposed;

            internal Entry(SegmentReadAhead owner, string key, object list, int index, string target, DateTime createdUtc)
            {
                _owner = owner;
                Key = key;
                List = list;
                Index = index;
                Target = target;
                CreatedUtc = createdUtc;
            }

            /// <summary>Gets the content type the friend sent.</summary>
            public string? ContentType { get; private set; }

            /// <summary>Gets the length the friend sent, if any.</summary>
            public long? ContentLength { get; private set; }

            /// <summary>Gets the friend's response headers (all of them; the caller picks).</summary>
            public IReadOnlyDictionary<string, string[]> Headers { get; private set; } = new Dictionary<string, string[]>();

            internal string Key { get; }

            internal object List { get; }

            internal int Index { get; }

            internal string Target { get; }

            internal DateTime CreatedUtc { get; }

            internal bool Claimed { get; private set; }

            internal CancellationToken Stopping => _stop.Token;

            /// <summary>Waits for the friend's answer.</summary>
            /// <returns>False when the friend didn't send it (fetch it the usual way).</returns>
            public Task<bool> WaitHeadAsync(CancellationToken cancellationToken) => _head.Task.WaitAsync(cancellationToken);

            /// <summary>Copies the segment to <paramref name="destination"/> as it arrives.</summary>
            /// <returns>A task.</returns>
            /// <exception cref="IOException">The link went away mid-segment.</exception>
            public async Task ServeAsync(Stream destination, CancellationToken cancellationToken)
            {
                await foreach (var chunk in _chunks.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                {
                    lock (_lock)
                    {
                        if (_disposed)
                        {
                            throw new ObjectDisposedException(nameof(Entry));
                        }

                        _buffered -= chunk.Length;
                    }

                    _owner.Account(-chunk.Length);
                    await destination.WriteAsync(chunk, cancellationToken).ConfigureAwait(false);
                }
            }

            /// <inheritdoc />
            public void Dispose()
            {
                _owner.RemoveLiveEntry(this);
                lock (_lock)
                {
                    if (_disposed)
                    {
                        return;
                    }

                    _disposed = true;
                    _owner.Account(-_buffered);
                    _buffered = 0;
                }

                _stop.Cancel();
                _head.TrySetResult(false);
                _chunks.Writer.TryComplete(new IOException("read-ahead dropped"));
            }

            internal void Claim()
            {
                lock (_lock)
                {
                    Claimed = true;
                }
            }

            internal void SetHead(HttpResponseMessage response)
            {
                ContentType = response.Content.Headers.ContentType?.ToString();
                ContentLength = response.Content.Headers.ContentLength;
                Headers = response.Headers.Concat(response.Content.Headers)
                    .GroupBy(h => h.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.SelectMany(h => h.Value).ToArray(), StringComparer.OrdinalIgnoreCase);
                _head.TrySetResult(true);
            }

            internal bool Add(byte[] chunk, long budget)
            {
                lock (_lock)
                {
                    if (_disposed || _buffered + chunk.Length > budget)
                    {
                        return false;
                    }

                    _buffered += chunk.Length;
                    _owner.Account(chunk.Length);
                    return _chunks.Writer.TryWrite(chunk);
                }
            }

            internal void Finish(Exception? error)
            {
                _head.TrySetResult(false);
                _chunks.Writer.TryComplete(error);
            }
        }
    }
}
