using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Federation.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Activity;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Tracks which friend servers are streaming or downloading this server's own
    /// media. A friend's playback never shows up in Jellyfin's own sessions, since
    /// it arrives as anonymous token-gated <c>DirectStream</c> requests; this
    /// turns those requests into viewing sessions per friend server and item
    /// (friend users are never named, only an opaque id keeps two viewers apart),
    /// lists them on the plugin page and writes start/stop entries to Jellyfin's
    /// own activity log.
    /// <para>
    /// It also keeps the usage history behind the Usage tab and the per-friend
    /// limits (simultaneous streams, monthly data), and counts plays of this
    /// server's own titles by local users for the popularity list friends may
    /// be offered. Everything is stored per friend server and title, never per
    /// person.
    /// </para>
    /// </summary>
    public sealed class FederationFriendActivityService : IDisposable
    {
        /// <summary>
        /// A session is only reported once its requests span this long. Short
        /// requests (a friend's Jellyfin probing a file, a preflight) never
        /// become "watching".
        /// </summary>
        internal static readonly TimeSpan ConfirmAfter = TimeSpan.FromSeconds(20);

        /// <summary>
        /// A session without requests for this long has ended. Players pause
        /// with an open connection or fetch ahead in bursts, so this is generous.
        /// </summary>
        internal static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(2);

        /// <summary>
        /// A stream counts against <see cref="RemoteServer.MaxConcurrentStreams"/>
        /// while a request is open or for this long after the last one, so a
        /// stopped title frees its slot quickly.
        /// </summary>
        internal static readonly TimeSpan LiveWindow = TimeSpan.FromSeconds(30);

        /// <summary>A local play counts once this much of the title was watched (or it was finished).</summary>
        internal static readonly TimeSpan LocalPlayMinimum = TimeSpan.FromMinutes(5);

        private static readonly TimeSpan Retention = TimeSpan.FromDays(90);
        private const int MaxRecords = 5000;
        private const string ActivityType = "FederationFriendPlayback";
        private static readonly JsonSerializerOptions FileJson = new() { WriteIndented = false };

        private readonly IActivityManager _activityManager;
        private readonly ILibraryManager _libraryManager;
        private readonly ISessionManager? _sessionManager;
        private readonly ILogger<FederationFriendActivityService> _logger;
        private readonly TimeProvider _clock;
        private readonly string? _storePath;
        private readonly Timer? _sweepTimer;
        private readonly object _lock = new();
        private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
        private readonly UsageState _state;

        // Who last counted a play of which title, only in memory, to count a
        // title once per user a day (see RecordLocalPlay).
        private readonly Dictionary<(Guid User, Guid Title), DateTime> _recentLocalPlays = new();
        // History changed (saved on the next sweep) / only byte totals moved
        // (saved at most once per BytesSaveInterval, so a long stream does not
        // rewrite the file every sweep).
        private bool _dirty;
        private bool _bytesDirty;
        private DateTime _lastSave = DateTime.MinValue;
        private static readonly TimeSpan BytesSaveInterval = TimeSpan.FromMinutes(1);

        /// <summary>
        /// Initializes a new instance of the <see cref="FederationFriendActivityService"/> class.
        /// </summary>
        public FederationFriendActivityService(
            IActivityManager activityManager,
            ILibraryManager libraryManager,
            ISessionManager sessionManager,
            ILogger<FederationFriendActivityService> logger)
            : this(activityManager, libraryManager, sessionManager, logger, TimeProvider.System, startTimer: true, storePath: DefaultStorePath())
        {
        }

        internal FederationFriendActivityService(
            IActivityManager activityManager,
            ILibraryManager libraryManager,
            ISessionManager? sessionManager,
            ILogger<FederationFriendActivityService> logger,
            TimeProvider clock,
            bool startTimer,
            string? storePath)
        {
            _activityManager = activityManager;
            _libraryManager = libraryManager;
            _sessionManager = sessionManager;
            _logger = logger;
            _clock = clock;
            _storePath = storePath;
            _state = Load(storePath, logger);
            if (_sessionManager != null)
            {
                _sessionManager.PlaybackStopped += OnLocalPlaybackStopped;
            }

            if (startTimer)
            {
                _sweepTimer = new Timer(_ => Sweep(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
            }
        }

        private DateTime Now => _clock.GetUtcNow().UtcDateTime;

        /// <summary>
        /// Whether a new media request from <paramref name="friend"/> is within
        /// its limits. Returns null when it may go ahead, otherwise a short reason.
        /// A request continuing a title the friend is already streaming is never
        /// refused for the stream limit, only for the data limit.
        /// </summary>
        public string? CheckLimits(RemoteServer friend, Guid itemId, string? remoteUserId, bool download)
        {
            lock (_lock)
            {
                return CheckLimitsLocked(friend, itemId, remoteUserId, download, Now);
            }
        }

        /// <summary>
        /// Checks the friend's limits and, when the request may go ahead,
        /// registers it in the same step, so two requests arriving together
        /// cannot both slip under a stream limit. Returns null and the lease, or
        /// the refusal reason and no lease.
        /// </summary>
        public string? TryBegin(RemoteServer friend, Guid itemId, string? remoteUserId, bool download, bool transcoded, out FriendActivityLease? lease)
        {
            lease = null;
            var now = Now;
            lock (_lock)
            {
                var refusal = CheckLimitsLocked(friend, itemId, remoteUserId, download, now);
                if (refusal != null)
                {
                    return refusal;
                }

                lease = BeginLocked(friend, itemId, remoteUserId, download, transcoded, now);
            }

            return null;
        }

        /// <summary>
        /// Records one media request from <paramref name="friend"/> for
        /// <paramref name="itemId"/> without checking limits. Report relayed
        /// bytes on the result and dispose it when the request ends.
        /// </summary>
        public FriendActivityLease Begin(RemoteServer friend, Guid itemId, string? remoteUserId, bool download, bool transcoded)
        {
            lock (_lock)
            {
                return BeginLocked(friend, itemId, remoteUserId, download, transcoded, Now);
            }
        }

        private string? CheckLimitsLocked(RemoteServer friend, Guid itemId, string? remoteUserId, bool download, DateTime now)
        {
            if (friend.MonthlyDataCapGb > 0
                && MonthlyBytesLocked(friend.Id, now) >= friend.MonthlyDataCapGb * 1_000_000_000L)
            {
                return $"This friend has used its monthly limit of {friend.MonthlyDataCapGb} GB.";
            }

            if (!download
                && friend.MaxConcurrentStreams > 0
                && !_sessions.ContainsKey(Key(friend, itemId, remoteUserId, download)))
            {
                // A viewer moving on to the next title (autoplay, a new
                // choice) frees their previous one at once; other viewers'
                // titles stay counted for a short while after their last
                // request, since players pause between range requests.
                var live = _sessions.Values.Count(s => s.FriendId == friend.Id
                    && !s.IsDownload
                    && (s.ActiveRequests > 0
                        || (now - s.LastSeen < LiveWindow
                            && (string.IsNullOrEmpty(remoteUserId) || !string.Equals(s.RemoteUserId, remoteUserId, StringComparison.Ordinal)))));
                if (live >= friend.MaxConcurrentStreams)
                {
                    return $"This friend is already streaming {live} title{(live == 1 ? string.Empty : "s")}, its limit.";
                }
            }

            return null;
        }

        private FriendActivityLease BeginLocked(RemoteServer friend, Guid itemId, string? remoteUserId, bool download, bool transcoded, DateTime now)
        {
            var key = Key(friend, itemId, remoteUserId, download);
            if (!_sessions.TryGetValue(key, out var session))
            {
                var item = SafeGetItem(itemId);
                session = new Session
                {
                    FriendId = friend.Id,
                    FriendName = string.IsNullOrWhiteSpace(friend.Name) ? friend.Url : friend.Name,
                    RemoteUserId = remoteUserId ?? string.Empty,
                    ItemId = itemId,
                    TitleId = item == null ? itemId : TitleOf(item),
                    ItemName = DescribeItem(item),
                    IsDownload = download,
                    Started = now
                };
                _sessions[key] = session;
            }

            session.ActiveRequests++;
            session.LastSeen = now;
            session.Transcoded |= transcoded;

            // Bytes this request may still send before the monthly limit.
            long? remaining = friend.MonthlyDataCapGb > 0
                ? Math.Max(0, (friend.MonthlyDataCapGb * 1_000_000_000L) - MonthlyBytesLocked(friend.Id, now))
                : null;
            return new FriendActivityLease(this, session, remaining);
        }

        /// <summary>Confirmed sessions that are still going, newest first.</summary>
        public IReadOnlyList<FriendActivityEntry> GetActive()
        {
            lock (_lock)
            {
                return _sessions.Values
                    .Where(s => s.Confirmed)
                    .OrderByDescending(s => s.Started)
                    .Select(s => s.ToEntry(null, s.ActiveRequests > 0))
                    .ToList();
            }
        }

        /// <summary>Recently ended sessions, newest first.</summary>
        public IReadOnlyList<FriendActivityEntry> GetRecent(int limit = 50)
        {
            lock (_lock)
            {
                return _state.Sessions
                    .OrderByDescending(r => r.Ended)
                    .Take(limit)
                    .Select(r => r.ToEntry())
                    .ToList();
            }
        }

        /// <summary>
        /// Per-friend totals for this and last month (UTC calendar months) plus
        /// the most watched titles of the retained history (90 days).
        /// </summary>
        public FriendUsageSummary GetUsageSummary()
        {
            var now = Now;
            var thisMonth = MonthKey(now);
            var lastMonth = MonthKey(now.AddMonths(-1));
            lock (_lock)
            {
                var records = _state.Sessions.Select(r => r.ToEntry())
                    .Concat(_sessions.Values.Where(s => s.Confirmed).Select(s => s.ToEntry(null, s.ActiveRequests > 0)))
                    .ToList();
                var friendIds = records.Select(r => r.FriendId)
                    .Concat(_state.MonthlyBytes.Keys.Select(k => k.Split('|')[0]))
                    .Distinct(StringComparer.Ordinal);

                var friends = new List<FriendUsage>();
                foreach (var friendId in friendIds)
                {
                    var mine = records.Where(r => r.FriendId == friendId).ToList();
                    friends.Add(new FriendUsage
                    {
                        FriendId = friendId,
                        FriendName = mine.OrderByDescending(r => r.Started).Select(r => r.FriendName).FirstOrDefault() ?? string.Empty,
                        BytesThisMonth = MonthlyBytesLocked(friendId, now),
                        BytesLastMonth = _state.MonthlyBytes.GetValueOrDefault(friendId + "|" + lastMonth),
                        HoursThisMonth = WatchedHours(mine.Where(r => MonthKey(r.Started) == thisMonth), now),
                        SessionsThisMonth = mine.Count(r => MonthKey(r.Started) == thisMonth),
                        HoursLast30Days = WatchedHours(mine.Where(r => now - r.Started <= TimeSpan.FromDays(30)), now),
                        TopTitles = TopTitles(mine, 5)
                    });
                }

                return new FriendUsageSummary
                {
                    Friends = friends.OrderByDescending(f => f.BytesThisMonth).ThenBy(f => f.FriendName).ToList(),
                    TopTitles = TopTitles(records, 10)
                };
            }
        }

        /// <summary>
        /// This server's most played titles (movies, shows, albums) over the last
        /// <paramref name="days"/> days: completed friend sessions plus local
        /// plays. Counts only; nothing about who watched.
        /// </summary>
        public IReadOnlyList<(Guid TitleId, int Plays)> GetPopularTitles(int days = 30)
        {
            var since = Now - TimeSpan.FromDays(days);
            lock (_lock)
            {
                // A friend binge-watching a show counts once per day, not once
                // per episode; local plays are already counted that way.
                return _state.Sessions
                    .Where(r => !r.IsDownload && r.Started >= since)
                    .Select(r => (r.FriendId, r.TitleId, r.Started.Date))
                    .Distinct()
                    .Select(x => x.TitleId)
                    .Concat(_state.LocalPlays.Where(p => p.At >= since).Select(p => p.TitleId))
                    .Where(id => id != Guid.Empty)
                    .GroupBy(id => id)
                    .Select(g => (g.Key, g.Count()))
                    .OrderByDescending(t => t.Item2)
                    .ToList();
            }
        }

        /// <summary>
        /// Confirms sessions that have lasted long enough, ends idle ones, adds
        /// relayed bytes to the monthly totals and saves the history. Runs on a
        /// timer; internal for tests.
        /// </summary>
        internal void Sweep()
        {
            var now = Now;
            var started = new List<Session>();
            var ended = new List<UsageRecord>();
            lock (_lock)
            {
                foreach (var (key, s) in _sessions.ToList())
                {
                    FoldBytesLocked(s, now);
                    var spanEnd = s.ActiveRequests > 0 ? now : s.LastSeen;
                    if (!s.Confirmed && spanEnd - s.Started >= ConfirmAfter)
                    {
                        s.Confirmed = true;
                        started.Add(s);
                    }

                    if (s.ActiveRequests == 0 && now - s.LastSeen >= IdleTimeout)
                    {
                        _sessions.Remove(key);
                        if (s.Confirmed)
                        {
                            var record = UsageRecord.From(s);
                            ended.Add(record);
                            _state.Sessions.Add(record);
                            _dirty = true;
                        }
                    }
                }

                PruneLocked(now);
            }

            foreach (var s in started)
            {
                var verb = s.IsDownload ? "started downloading" : "started watching";
                Log($"{s.FriendName} {verb} {s.ItemName}", s.ItemId, s.IsDownload ? "Download" : (s.Transcoded ? "Transcoded for their connection" : "Original file"));
            }

            foreach (var e in ended)
            {
                var verb = e.IsDownload ? "stopped downloading" : "stopped watching";
                Log($"{e.FriendName} {verb} {e.ItemName}", e.ItemId, "After " + FormatDuration(e.Ended - e.Started));
            }

            Save();
        }

        /// <summary>
        /// Records a local user's play of one of this server's own titles, at
        /// most once per user and title (movie, show, album) a day, so a
        /// listened album or a binge counts once, like a movie. Only the title
        /// and time are stored, never the user. Internal for tests.
        /// </summary>
        internal void RecordLocalPlay(BaseItem? item, long? positionTicks, bool playedToCompletion, Guid userId = default)
        {
            if (item == null
                || (item.ProviderIds != null && item.ProviderIds.TryGetValue("FederationKey", out var key) && !string.IsNullOrEmpty(key))
                || !(playedToCompletion || (positionTicks ?? 0) >= LocalPlayMinimum.Ticks))
            {
                return;
            }

            var titleId = TitleOf(item);
            var now = Now;
            lock (_lock)
            {
                if (_recentLocalPlays.TryGetValue((userId, titleId), out var last) && now - last < TimeSpan.FromDays(1))
                {
                    return;
                }

                _recentLocalPlays[(userId, titleId)] = now;
                if (_recentLocalPlays.Count > 10_000)
                {
                    foreach (var old in _recentLocalPlays.Where(kv => now - kv.Value >= TimeSpan.FromDays(1)).Select(kv => kv.Key).ToList())
                    {
                        _recentLocalPlays.Remove(old);
                    }
                }

                _state.LocalPlays.Add(new LocalPlayRecord { TitleId = titleId, At = now });
                _dirty = true;
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            _sweepTimer?.Dispose();
            if (_sessionManager != null)
            {
                _sessionManager.PlaybackStopped -= OnLocalPlaybackStopped;
            }

            // Server shutdown: keep the data of streams still running and close
            // the confirmed ones into the history, so a restart loses nothing.
            var now = Now;
            lock (_lock)
            {
                foreach (var s in _sessions.Values)
                {
                    FoldBytesLocked(s, now);
                    if (s.Confirmed)
                    {
                        _state.Sessions.Add(UsageRecord.From(s));
                        _dirty = true;
                    }
                }

                _sessions.Clear();
            }

            Save(force: true);
        }

        internal static string FormatDuration(TimeSpan span)
        {
            if (span.TotalHours >= 1)
            {
                return $"{(int)span.TotalHours} h {span.Minutes} min";
            }

            return span.TotalMinutes >= 1 ? $"{(int)span.TotalMinutes} min" : "less than a minute";
        }

        private static string MonthKey(DateTime utc) => utc.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);

        private static string Key(RemoteServer friend, Guid itemId, string? remoteUserId, bool download)
            => string.Join('|', friend.Id, itemId.ToString("N"), remoteUserId ?? string.Empty, download ? "d" : "p");

        private static double WatchedHours(IEnumerable<FriendActivityEntry> records, DateTime now)
            => Math.Round(records.Where(r => !r.IsDownload).Sum(r => ((r.Ended ?? now) - r.Started).TotalHours), 1);

        private static List<TitleUsage> TopTitles(IEnumerable<FriendActivityEntry> records, int count)
            => records.Where(r => !r.IsDownload)
                .GroupBy(r => r.TitleId)
                .Select(g => new TitleUsage
                {
                    TitleId = g.Key,
                    Name = g.OrderByDescending(r => r.Started).First().ItemName,
                    Sessions = g.Count(),
                    Friends = g.Select(r => r.FriendId).Distinct().Count()
                })
                .OrderByDescending(t => t.Sessions)
                .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                .Take(count)
                .ToList();

        private static string? DefaultStorePath()
        {
            var dir = Plugin.Instance?.DataFolderPath;
            return string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, "federation-friend-usage.json");
        }

        private static UsageState Load(string? path, ILogger logger)
        {
            if (path == null || !File.Exists(path))
            {
                return new UsageState();
            }

            try
            {
                return JsonSerializer.Deserialize<UsageState>(File.ReadAllText(path), FileJson) ?? new UsageState();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[Federation] Could not read friend usage history; starting a new one");
                return new UsageState();
            }
        }

        private void Save(bool force = false)
        {
            string json;
            lock (_lock)
            {
                var bytesDue = _bytesDirty && (force || Now - _lastSave >= BytesSaveInterval);
                if (_storePath == null || !(_dirty || bytesDue))
                {
                    return;
                }

                json = JsonSerializer.Serialize(_state, FileJson);
                _dirty = false;
                _bytesDirty = false;
                _lastSave = Now;
            }

            try
            {
                var temp = _storePath + ".tmp";
                File.WriteAllText(temp, json);
                File.Move(temp, _storePath, overwrite: true);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[Federation] Could not save friend usage history");
            }
        }

        private void OnLocalPlaybackStopped(object? sender, PlaybackStopEventArgs e)
        {
            try
            {
                RecordLocalPlay(e.Item, e.PlaybackPositionTicks, e.PlayedToCompletion, e.Users?.FirstOrDefault()?.Id ?? Guid.Empty);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[Federation] Could not count a local play");
            }
        }

        private long MonthlyBytesLocked(string friendId, DateTime now)
        {
            var folded = _state.MonthlyBytes.GetValueOrDefault(friendId + "|" + MonthKey(now));
            var pending = _sessions.Values.Where(s => s.FriendId == friendId).Sum(s => Interlocked.Read(ref s.Bytes) - s.FoldedBytes);
            return folded + pending;
        }

        private void FoldBytesLocked(Session s, DateTime now)
        {
            var total = Interlocked.Read(ref s.Bytes);
            var delta = total - s.FoldedBytes;
            if (delta <= 0)
            {
                return;
            }

            var key = s.FriendId + "|" + MonthKey(now);
            _state.MonthlyBytes[key] = _state.MonthlyBytes.GetValueOrDefault(key) + delta;
            s.FoldedBytes = total;
            _bytesDirty = true;
        }

        private void PruneLocked(DateTime now)
        {
            var cutoff = now - Retention;
            var before = _state.Sessions.Count + _state.LocalPlays.Count + _state.MonthlyBytes.Count;
            _state.Sessions.RemoveAll(r => r.Ended < cutoff);
            if (_state.Sessions.Count > MaxRecords)
            {
                _state.Sessions = _state.Sessions.OrderByDescending(r => r.Ended).Take(MaxRecords).ToList();
            }

            _state.LocalPlays.RemoveAll(p => p.At < cutoff);
            if (_state.LocalPlays.Count > MaxRecords)
            {
                _state.LocalPlays = _state.LocalPlays.OrderByDescending(p => p.At).Take(MaxRecords).ToList();
            }

            var oldestMonth = MonthKey(now.AddMonths(-13));
            foreach (var key in _state.MonthlyBytes.Keys.Where(k => string.CompareOrdinal(k.Split('|').Last(), oldestMonth) < 0).ToList())
            {
                _state.MonthlyBytes.Remove(key);
            }

            if (_state.Sessions.Count + _state.LocalPlays.Count + _state.MonthlyBytes.Count != before)
            {
                _dirty = true;
            }
        }

        private void Log(string name, Guid itemId, string shortOverview)
        {
            _logger.LogInformation("[Federation] {Activity}", name);
            try
            {
                var log = new ActivityLog(name, ActivityType, Guid.Empty)
                {
                    ItemId = itemId.ToString("N"),
                    ShortOverview = shortOverview
                };
                _activityManager.CreateAsync(log).ContinueWith(
                    t => _logger.LogDebug(t.Exception, "[Federation] Could not write friend activity to the Jellyfin activity log"),
                    System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[Federation] Could not write friend activity to the Jellyfin activity log");
            }
        }

        private BaseItem? SafeGetItem(Guid itemId)
        {
            try
            {
                return _libraryManager.GetItemById(itemId);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[Federation] Could not resolve item {ItemId} for friend activity", itemId);
                return null;
            }
        }

        /// <summary>The title a play counts toward: an episode's show, a song's album, else the item.</summary>
        private Guid TitleOf(BaseItem item)
        {
            switch (item)
            {
                case Episode episode when episode.SeriesId != Guid.Empty:
                    return episode.SeriesId;
                case Season season when season.SeriesId != Guid.Empty:
                    return season.SeriesId;
                case Audio:
                    try
                    {
                        var album = item.ParentId == Guid.Empty ? null : _libraryManager.GetItemById(item.ParentId) as MusicAlbum;
                        return album?.Id ?? item.Id;
                    }
                    catch (Exception)
                    {
                        return item.Id;
                    }

                default:
                    return item.Id;
            }
        }

        private static string DescribeItem(BaseItem? item)
        {
            if (item == null)
            {
                return "an unknown title";
            }

            if (item is Episode episode && !string.IsNullOrEmpty(episode.SeriesName))
            {
                var number = episode.ParentIndexNumber.HasValue && episode.IndexNumber.HasValue
                    ? $" S{episode.ParentIndexNumber:00}E{episode.IndexNumber:00}"
                    : string.Empty;
                return $"{episode.SeriesName}{number} - {episode.Name}";
            }

            if (item is Audio audio && audio.Artists?.Count > 0)
            {
                return $"{audio.Artists[0]} - {audio.Name}";
            }

            return item.ProductionYear.HasValue ? $"{item.Name} ({item.ProductionYear})" : item.Name ?? "an unknown title";
        }

        private void End(Session session)
        {
            lock (_lock)
            {
                session.ActiveRequests = Math.Max(0, session.ActiveRequests - 1);
                session.LastSeen = Now;
            }
        }

        internal sealed class Session
        {
            public long Bytes;

            public string FriendId { get; init; } = string.Empty;

            public string FriendName { get; init; } = string.Empty;

            /// <summary>Gets the friend's opaque user id; never shown, only keeps viewers apart.</summary>
            public string RemoteUserId { get; init; } = string.Empty;

            public Guid ItemId { get; init; }

            public Guid TitleId { get; init; }

            public string ItemName { get; init; } = string.Empty;

            public bool IsDownload { get; init; }

            public DateTime Started { get; init; }

            public DateTime LastSeen { get; set; }

            public int ActiveRequests { get; set; }

            public bool Transcoded { get; set; }

            public bool Confirmed { get; set; }

            public long FoldedBytes { get; set; }

            public FriendActivityEntry ToEntry(DateTime? ended, bool streaming) => new()
            {
                FriendId = FriendId,
                FriendName = FriendName,
                ItemId = ItemId,
                TitleId = TitleId,
                ItemName = ItemName,
                IsDownload = IsDownload,
                Transcoded = Transcoded,
                Started = Started,
                LastSeen = LastSeen,
                Ended = ended,
                IsStreaming = streaming,
                Bytes = Interlocked.Read(ref Bytes)
            };
        }

        internal sealed class UsageState
        {
            public List<UsageRecord> Sessions { get; set; } = new();

            public List<LocalPlayRecord> LocalPlays { get; set; } = new();

            /// <summary>Relayed bytes keyed by "friendId|yyyy-MM".</summary>
            public Dictionary<string, long> MonthlyBytes { get; set; } = new(StringComparer.Ordinal);
        }

        internal sealed class UsageRecord
        {
            public string FriendId { get; set; } = string.Empty;

            public string FriendName { get; set; } = string.Empty;

            public Guid ItemId { get; set; }

            public Guid TitleId { get; set; }

            public string ItemName { get; set; } = string.Empty;

            public bool IsDownload { get; set; }

            public bool Transcoded { get; set; }

            public DateTime Started { get; set; }

            public DateTime Ended { get; set; }

            public long Bytes { get; set; }

            public static UsageRecord From(Session s) => new()
            {
                FriendId = s.FriendId,
                FriendName = s.FriendName,
                ItemId = s.ItemId,
                TitleId = s.TitleId,
                ItemName = s.ItemName,
                IsDownload = s.IsDownload,
                Transcoded = s.Transcoded,
                Started = s.Started,
                Ended = s.LastSeen,
                Bytes = Interlocked.Read(ref s.Bytes)
            };

            public FriendActivityEntry ToEntry() => new()
            {
                FriendId = FriendId,
                FriendName = FriendName,
                ItemId = ItemId,
                TitleId = TitleId,
                ItemName = ItemName,
                IsDownload = IsDownload,
                Transcoded = Transcoded,
                Started = DateTime.SpecifyKind(Started, DateTimeKind.Utc),
                LastSeen = DateTime.SpecifyKind(Ended, DateTimeKind.Utc),
                Ended = DateTime.SpecifyKind(Ended, DateTimeKind.Utc),
                Bytes = Bytes
            };
        }

        internal sealed class LocalPlayRecord
        {
            public Guid TitleId { get; set; }

            public DateTime At { get; set; }
        }

        /// <summary>
        /// One open media request of a friend session; counts relayed bytes.
        /// </summary>
        public sealed class FriendActivityLease : IDisposable
        {
            private readonly FederationFriendActivityService _owner;
            private readonly long? _remainingBytes;
            private Session? _session;
            private long _requestBytes;

            internal FriendActivityLease(FederationFriendActivityService owner, Session session, long? remainingBytes = null)
            {
                _owner = owner;
                _session = session;
                _remainingBytes = remainingBytes;
            }

            /// <summary>
            /// Adds bytes sent to the friend in this request. Returns false once
            /// this request has used up what was left of the friend's monthly
            /// data limit when it started; the caller then stops the request.
            /// </summary>
            public bool AddBytes(long count)
            {
                var session = _session;
                if (session != null && count > 0)
                {
                    Interlocked.Add(ref session.Bytes, count);
                }

                var sent = Interlocked.Add(ref _requestBytes, Math.Max(0, count));
                return _remainingBytes is not { } remaining || sent <= remaining;
            }

            /// <inheritdoc />
            public void Dispose()
            {
                var session = Interlocked.Exchange(ref _session, null);
                if (session != null)
                {
                    _owner.End(session);
                }
            }
        }
    }

    /// <summary>
    /// One friend server's viewing (or download) of one of this server's titles.
    /// </summary>
    public class FriendActivityEntry
    {
        public string FriendId { get; set; } = string.Empty;

        public string FriendName { get; set; } = string.Empty;

        public Guid ItemId { get; set; }

        /// <summary>Gets or sets the movie, show or album the item belongs to.</summary>
        public Guid TitleId { get; set; }

        public string ItemName { get; set; } = string.Empty;

        public bool IsDownload { get; set; }

        public bool Transcoded { get; set; }

        public DateTime Started { get; set; }

        public DateTime LastSeen { get; set; }

        public DateTime? Ended { get; set; }

        /// <summary>Whether data is flowing right now (false while paused or buffered ahead).</summary>
        public bool IsStreaming { get; set; }

        public long Bytes { get; set; }
    }

    /// <summary>Usage tab data, see <see cref="FederationFriendActivityService.GetUsageSummary"/>.</summary>
    public class FriendUsageSummary
    {
        public List<FriendUsage> Friends { get; set; } = new();

        public List<TitleUsage> TopTitles { get; set; } = new();
    }

    /// <summary>One friend server's usage of this server.</summary>
    public class FriendUsage
    {
        public string FriendId { get; set; } = string.Empty;

        public string FriendName { get; set; } = string.Empty;

        public long BytesThisMonth { get; set; }

        public long BytesLastMonth { get; set; }

        public double HoursThisMonth { get; set; }

        public int SessionsThisMonth { get; set; }

        public double HoursLast30Days { get; set; }

        public List<TitleUsage> TopTitles { get; set; } = new();
    }

    /// <summary>How often friends watched one title.</summary>
    public class TitleUsage
    {
        public Guid TitleId { get; set; }

        public string Name { get; set; } = string.Empty;

        public int Sessions { get; set; }

        public int Friends { get; set; }
    }
}
