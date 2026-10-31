using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// One viewer's playback of a friend's title, as this server tells that
    /// friend (<c>Peer/Viewers</c>), so it shows on the friend's dashboard like
    /// a viewer connected to it directly.
    /// </summary>
    public sealed class PeerViewerReport
    {
        /// <summary>Start, progress or stop.</summary>
        public string Event { get; set; } = ViewerEvents.Progress;

        /// <summary>Gets or sets an opaque id for this playback on the reporting server.</summary>
        public string Key { get; set; } = string.Empty;

        /// <summary>Gets or sets the owner's item id.</summary>
        public Guid ItemId { get; set; }

        public long? PositionTicks { get; set; }

        public bool IsPaused { get; set; }

        /// <summary>Gets or sets the play method as the owner sees it (DirectPlay, DirectStream, Transcode).</summary>
        public string? PlayMethod { get; set; }

        /// <summary>Gets or sets how the stream travels (see <see cref="FederatedRoute"/>).</summary>
        public string? Route { get; set; }

        /// <summary>Gets or sets the viewer's user id on the reporting server (the remote user id per-user rules use).</summary>
        public string? ViewerId { get; set; }

        public string? ViewerName { get; set; }

        public string? Client { get; set; }

        public string? ClientVersion { get; set; }

        public string? DeviceName { get; set; }

        public string? DeviceId { get; set; }

        public string? PlaySessionId { get; set; }
    }

    /// <summary>Event names in <see cref="PeerViewerReport.Event"/>.</summary>
    public static class ViewerEvents
    {
        public const string Start = "start";
        public const string Progress = "progress";
        public const string Stop = "stop";
    }

    /// <summary>
    /// How a federated stream travels, from the viewer's server's point of view.
    /// </summary>
    public static class FederatedRoute
    {
        /// <summary>The player fetches the friend's file straight from the friend.</summary>
        public const string Direct = "direct";

        /// <summary>The file passes through this server unchanged.</summary>
        public const string Relay = "relay";

        /// <summary>The friend converts it and the player fetches the result from the friend.</summary>
        public const string FriendConverts = "friend-converts";

        /// <summary>This server reads the friend's file and converts it.</summary>
        public const string ConvertedHere = "converted-here";

        /// <summary>The transport route has not been observed yet.</summary>
        public const string Unknown = "unknown";

        /// <summary>Classifies a playback. <paramref name="handedOff"/>: the friend runs this session's conversion.</summary>
        public static string Classify(RemoteServer server, PlayMethod? playMethod, bool handedOff, string? observedRoute = null)
        {
            if (playMethod == MediaBrowser.Model.Session.PlayMethod.Transcode)
            {
                return handedOff ? FriendConverts : ConvertedHere;
            }

            return observedRoute is Direct or Relay ? observedRoute : Unknown;
        }

        /// <summary>The play method the friend (owner) sees: when this server converts, the friend only serves the original.</summary>
        public static string OwnerPlayMethod(string route, PlayMethod? viewerMethod)
            => route == ConvertedHere
                ? nameof(MediaBrowser.Model.Session.PlayMethod.DirectStream)
                : viewerMethod?.ToString() ?? "Unknown";
    }

    /// <summary>
    /// Viewer side: tells each friend what this server's viewers are watching of
    /// theirs. Start and stop go out at once; progress every 15 s, or sooner on
    /// pause, resume or a seek. One ordered queue, never blocking playback.
    /// </summary>
    public sealed class FederationViewerReporter : IHostedService
    {
        internal static readonly TimeSpan ProgressSpacing = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan UnsupportedBackoff = TimeSpan.FromMinutes(10);

        private readonly ISessionManager _sessions;
        private readonly FederationLibraryManager _federationManager;
        private readonly RemoteTranscodeHandoffs _handoffs;
        private readonly ILogger<FederationViewerReporter> _logger;
        private readonly ConcurrentDictionary<string, Sent> _sent = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, DateTime> _unsupported = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, string> _playSessions = new(StringComparer.Ordinal);
        private Channel<(RemoteServer Server, PeerViewerReport Report)>? _queue;
        private CancellationTokenSource? _stopping;
        private Task? _worker;

        /// <summary>
        /// Initializes a new instance of the <see cref="FederationViewerReporter"/> class.
        /// </summary>
        public FederationViewerReporter(
            ISessionManager sessions,
            FederationLibraryManager federationManager,
            RemoteTranscodeHandoffs handoffs,
            ILogger<FederationViewerReporter> logger)
        {
            _sessions = sessions;
            _federationManager = federationManager;
            _handoffs = handoffs;
            _logger = logger;
        }

        /// <summary>Clock, replaceable in tests.</summary>
        internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

        /// <inheritdoc />
        public Task StartAsync(CancellationToken cancellationToken)
        {
            _stopping = new CancellationTokenSource();
            _queue = Channel.CreateBounded<(RemoteServer, PeerViewerReport)>(new BoundedChannelOptions(500) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
            _worker = Task.Run(() => SendLoopAsync(_queue.Reader, _stopping.Token));
            _sessions.PlaybackStart += OnStart;
            _sessions.PlaybackProgress += OnProgress;
            _sessions.PlaybackStopped += OnStopped;
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            _sessions.PlaybackStart -= OnStart;
            _sessions.PlaybackProgress -= OnProgress;
            _sessions.PlaybackStopped -= OnStopped;
            _queue?.Writer.TryComplete();
            _stopping?.Cancel();
            if (_worker != null)
            {
                try
                {
                    await _worker.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }

            _stopping?.Dispose();
            _stopping = null;
        }

        private void OnStart(object? sender, PlaybackProgressEventArgs e)
            => Handle(ViewerEvents.Start, e.Session, e.Item, e.MediaSourceId, e.PlaySessionId, e.PlaybackPositionTicks, e.IsPaused);

        private void OnProgress(object? sender, PlaybackProgressEventArgs e)
            => Handle(ViewerEvents.Progress, e.Session, e.Item, e.MediaSourceId, e.PlaySessionId, e.PlaybackPositionTicks, e.IsPaused);

        private void OnStopped(object? sender, PlaybackStopEventArgs e)
            => Handle(ViewerEvents.Stop, e.Session, e.Item, e.MediaSourceId, e.PlaySessionId, e.PlaybackPositionTicks, false);

        private void Handle(string kind, SessionInfo? session, BaseItem? item, string? mediaSourceId, string? playSessionId, long? positionTicks, bool isPaused)
        {
            try
            {
                if (session != null)
                {
                    // Players don't expose their play session id; the Playback
                    // Info panel asks by session, so remember it here.
                    if (kind == ViewerEvents.Stop)
                    {
                        _playSessions.TryRemove(session.Id, out _);
                    }
                    else if (!string.IsNullOrEmpty(playSessionId))
                    {
                        _playSessions[session.Id] = playSessionId;
                    }
                }

                if (session == null || item == null || Plugin.Instance?.Configuration?.ShareViewersWithFriends == false)
                {
                    return;
                }

                var key = ReportKey(session.Id, playSessionId);
                if (kind == ViewerEvents.Stop)
                {
                    // Only playbacks already reported are stopped; anything else never reached the friend.
                    if (_sent.TryRemove(key, out var last))
                    {
                        Enqueue(last.Server, Build(kind, key, last.ItemId, session, playSessionId, positionTicks, false, last.Route, last.Server));
                    }

                    return;
                }

                var source = ResolveSource(item, mediaSourceId);
                if (source == null)
                {
                    return;
                }

                var server = _federationManager.GetServer(source.ServerId);
                if (server == null || !server.Enabled || server.Kind != ServerKind.Jellyfin || IsUnsupported(server.Id))
                {
                    return;
                }

                var route = FederatedRoute.Classify(server, session.PlayState?.PlayMethod, _handoffs.HandedOffTo(playSessionId) != null, FederationPlaybackRoutes.Get(playSessionId));
                var now = UtcNow();
                _sent.TryGetValue(key, out var previous);
                if (kind == ViewerEvents.Progress && previous != null && previous.ItemId == source.RemoteItemId && !IsDue(previous, now, positionTicks, isPaused, route))
                {
                    return;
                }

                var sendKind = previous == null || previous.ItemId != source.RemoteItemId ? ViewerEvents.Start : ViewerEvents.Progress;
                if (previous != null && previous.ItemId != source.RemoteItemId)
                {
                    // Next episode in the same session: close the old one on its owner first.
                    Enqueue(previous.Server, Build(ViewerEvents.Stop, key, previous.ItemId, session, playSessionId, previous.PositionTicks, false, previous.Route, previous.Server));
                }

                _sent[key] = new Sent(server, source.RemoteItemId, now, positionTicks, isPaused, route);
                Enqueue(server, Build(sendKind, key, source.RemoteItemId, session, playSessionId, positionTicks, isPaused, route, server));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[Federation] Could not prepare a viewer report");
            }
        }

        /// <summary>The play session id a session is playing under, as last reported.</summary>
        public string? PlaySessionFor(string sessionId)
            => _playSessions.TryGetValue(sessionId, out var id) ? id : null;

        /// <summary>
        /// Whether a progress report is due: spacing elapsed, pause changed, the
        /// route changed, or the position jumped (a seek) by more than 10 s.
        /// </summary>
        internal static bool IsDue(Sent previous, DateTime now, long? positionTicks, bool isPaused, string route)
        {
            if (previous.IsPaused != isPaused || previous.Route != route || now - previous.SentUtc >= ProgressSpacing)
            {
                return true;
            }

            if (positionTicks is not long position || previous.PositionTicks is not long before)
            {
                return false;
            }

            var expected = before + (previous.IsPaused ? 0 : (now - previous.SentUtc).Ticks);
            return Math.Abs(position - expected) > TimeSpan.FromSeconds(10).Ticks;
        }

        private FederatedSource? ResolveSource(BaseItem item, string? mediaSourceId)
        {
            var key = FederationLibraryManager.GetFederationKey(item);
            var entry = key == null ? null : _federationManager.Cache.GetEntryByKey(key);
            return entry == null ? null : PlayedSource(entry, item.Id, mediaSourceId);
        }

        /// <summary>
        /// The friend's copy a playback uses: the media source id is the item's
        /// own id for the primary copy, or a hash of friend and remote id for the
        /// others (see <see cref="FederationMediaSourceProvider.BuildSourceId"/>).
        /// </summary>
        internal static FederatedSource? PlayedSource(FederatedCacheEntry entry, Guid itemId, string? mediaSourceId)
        {
            var sources = entry.GetSourcesSnapshot();
            if (!string.IsNullOrEmpty(mediaSourceId) && !string.Equals(mediaSourceId, itemId.ToString("N"), StringComparison.OrdinalIgnoreCase))
            {
                var played = sources.FirstOrDefault(s => string.Equals(FederationMediaSourceProvider.BuildSourceId(s), mediaSourceId, StringComparison.OrdinalIgnoreCase));
                if (played != null)
                {
                    return played;
                }
            }

            return entry.GetPrimarySource();
        }

        private PeerViewerReport Build(string kind, string key, Guid itemId, SessionInfo session, string? playSessionId, long? positionTicks, bool isPaused, string route, RemoteServer server)
            => new()
            {
                Event = kind,
                Key = key,
                ItemId = itemId,
                PositionTicks = positionTicks,
                IsPaused = isPaused,
                Route = route,
                PlayMethod = FederatedRoute.OwnerPlayMethod(route, session.PlayState?.PlayMethod),
                ViewerId = session.UserId == Guid.Empty ? null : session.UserId.ToString("N"),
                ViewerName = SharesNamesWith(server) ? session.UserName : null,
                Client = session.Client,
                ClientVersion = session.ApplicationVersion,
                DeviceName = SharesNamesWith(server) ? session.DeviceName : null,
                DeviceId = session.DeviceId,
                PlaySessionId = playSessionId
            };

        /// <summary>
        /// Whether reports to <paramref name="friend"/> carry user and device
        /// names: its own choice, else the default. Without them the friend
        /// shows "Someone (this server)".
        /// </summary>
        internal static bool SharesNamesWith(RemoteServer friend)
            => friend.ShareViewerNames ?? Plugin.Instance?.Configuration?.ShareViewerNamesWithFriends ?? false;

        /// <summary>An opaque, stable id for one playback: friends never see this server's own session ids.</summary>
        internal static string ReportKey(string sessionId, string? playSessionId)
            => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sessionId + "|" + (playSessionId ?? string.Empty)))).Substring(0, 24).ToLowerInvariant();

        private void Enqueue(RemoteServer server, PeerViewerReport report)
            => _queue?.Writer.TryWrite((server, report));

        private bool IsUnsupported(string serverId)
            => _unsupported.TryGetValue(serverId, out var until) && until > UtcNow();

        private async Task SendLoopAsync(ChannelReader<(RemoteServer Server, PeerViewerReport Report)> reader, CancellationToken token)
        {
            await foreach (var (server, report) in reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                var client = _federationManager.GetClient(server.Id);
                if (client == null || IsUnsupported(server.Id))
                {
                    continue;
                }

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                int status;
                try
                {
                    status = await client.PostPeerAsync("Viewers", report, timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    continue;
                }

                if (status == 404 || status == 405)
                {
                    // A friend on a version without Peer/Viewers.
                    _unsupported[server.Id] = UtcNow() + UnsupportedBackoff;
                }
                else if (status is not (>= 200 and < 300) and not 0)
                {
                    _logger.LogDebug("[Federation] {Friend} did not accept a viewer report (HTTP {Status})", server.Name, status);
                }
            }
        }

        /// <summary>The last report sent for one playback.</summary>
        internal sealed record Sent(RemoteServer Server, Guid ItemId, DateTime SentUtc, long? PositionTicks, bool IsPaused, string Route);
    }

    /// <summary>
    /// Owner side: friends' viewers watching this server's titles, shown as
    /// sessions on the dashboard (title, position, paused, play method) under
    /// "bob (friend-a)". The sessions have no local user, so nothing is
    /// recorded against anyone's watch history here. A viewer that goes quiet
    /// for 90 s is stopped.
    /// </summary>
    public sealed class FederationRemoteViewerService : IHostedService
    {
        internal static readonly TimeSpan Silence = TimeSpan.FromSeconds(90);

        private readonly ISessionManager _sessions;
        private readonly ILibraryManager _libraryManager;
        private readonly FederationPeerAccessService _peerAccess;
        private readonly ILogger<FederationRemoteViewerService> _logger;
        private readonly IRemoteServerClientFactory? _clientFactory;
        private readonly Dictionary<string, Viewer> _viewers = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, (DateTime ExpiresUtc, List<UserDto> Users)> _viewerRosters = new(StringComparer.Ordinal);
        private readonly SemaphoreSlim _gate = new(1, 1);
        private Timer? _sweep;

        /// <summary>
        /// Initializes a new instance of the <see cref="FederationRemoteViewerService"/> class.
        /// </summary>
        public FederationRemoteViewerService(
            ISessionManager sessions,
            ILibraryManager libraryManager,
            FederationPeerAccessService peerAccess,
            ILogger<FederationRemoteViewerService> logger,
            IRemoteServerClientFactory? clientFactory = null)
        {
            _sessions = sessions;
            _libraryManager = libraryManager;
            _peerAccess = peerAccess;
            _logger = logger;
            _clientFactory = clientFactory;
        }

        /// <summary>Clock, replaceable in tests.</summary>
        internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

        /// <inheritdoc />
        public Task StartAsync(CancellationToken cancellationToken)
        {
            _sweep = new Timer(_ => _ = SweepAsync(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            _sweep?.Dispose();
            _sweep = null;
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                foreach (var key in _viewers.Keys.ToList())
                {
                    await EndAsync(key).ConfigureAwait(false);
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>Friends' viewers currently shown, for the plugin's own dashboard card.</summary>
        public IReadOnlyList<(string Friend, string Viewer, Guid ItemId, string? Route)> Current()
        {
            _gate.Wait();
            try
            {
                return _viewers.Values.Select(v => (v.Friend, v.ViewerName, v.ItemId, v.Route)).ToList();
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// Applies one report from <paramref name="caller"/>. Returns the HTTP
        /// status for the friend.
        /// </summary>
        public async Task<int> ReceiveAsync(RemoteServer caller, PeerViewerReport report, string? remoteAddress)
        {
            if (Plugin.Instance?.Configuration?.ShowFriendsViewers == false)
            {
                return 204;
            }

            if (string.IsNullOrWhiteSpace(report.Key) || report.Key.Length > 64 || report.ItemId == Guid.Empty)
            {
                return 400;
            }

            if (report.Event != ViewerEvents.Stop && !await IsVerifiedViewer(caller, report).ConfigureAwait(false))
            {
                // A name this server can't check against the friend's roster (the
                // friend keeps its names or its users to itself, or made one up)
                // is never shown: the viewer appears as "Someone (friend)".
                Anonymize(report);
            }

            var key = caller.Id + "|" + report.Key;
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (report.Event == ViewerEvents.Stop)
                {
                    if (_viewers.ContainsKey(key))
                    {
                        await EndAsync(key, report.PositionTicks).ConfigureAwait(false);
                    }

                    return 204;
                }

                var item = _libraryManager.GetItemById(report.ItemId);
                if (item == null)
                {
                    return 404;
                }

                if (!_peerAccess.IsItemVisible(caller, report.ViewerId, item.Id))
                {
                    return 403;
                }

                _viewers.TryGetValue(key, out var viewer);
                if (viewer != null && viewer.ItemId != item.Id)
                {
                    await StopPlaybackAsync(viewer, null).ConfigureAwait(false);
                    viewer.Started = false;
                }

                if (viewer == null)
                {
                    var callerPrefix = caller.Id + "|";
                    if (_viewers.Keys.Count(k => k.StartsWith(callerPrefix, StringComparison.Ordinal)) >= MaxViewersPerFriend)
                    {
                        return 429;
                    }

                    var session = await _sessions.LogSessionActivity(
                        Clean(report.Client, 64) ?? "Jellyfin",
                        Clean(report.ClientVersion, 32) ?? "0",
                        DeviceIdFor(caller, report),
                        DeviceLabel(report.DeviceName, caller.Name),
                        string.IsNullOrEmpty(remoteAddress) ? "0.0.0.0" : remoteAddress,
                        null!).ConfigureAwait(false);
                    viewer = new Viewer(session, caller.Name, Label(report.ViewerName, caller.Name));
                    _viewers[key] = viewer;
                }

                viewer.Session.UserName = viewer.ViewerName;
                viewer.ItemId = item.Id;
                viewer.Route = report.Route;
                viewer.PlaySessionId = Clean(report.PlaySessionId, 64);
                viewer.LastReportUtc = UtcNow();
                if (Enum.TryParse<PlayMethod>(report.PlayMethod, true, out var method))
                {
                    if (!viewer.Started)
                    {
                        await _sessions.OnPlaybackStart(new PlaybackStartInfo
                        {
                            ItemId = item.Id,
                            SessionId = viewer.Session.Id,
                            MediaSourceId = item.Id.ToString("N"),
                            PositionTicks = report.PositionTicks,
                            IsPaused = report.IsPaused,
                            PlayMethod = method,
                            PlaySessionId = viewer.PlaySessionId,
                            CanSeek = true
                        }).ConfigureAwait(false);
                        viewer.Started = true;
                    }
                    else
                    {
                        await _sessions.OnPlaybackProgress(new PlaybackProgressInfo
                        {
                            ItemId = item.Id,
                            SessionId = viewer.Session.Id,
                            MediaSourceId = item.Id.ToString("N"),
                            PositionTicks = report.PositionTicks,
                            IsPaused = report.IsPaused,
                            PlayMethod = method,
                            PlaySessionId = viewer.PlaySessionId,
                            CanSeek = true
                        }).ConfigureAwait(false);
                    }
                }

                return 204;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Federation] Could not show a viewer from {Friend}", caller.Name);
                return 500;
            }
            finally
            {
                _gate.Release();
            }
        }

        internal async Task<bool> IsVerifiedViewer(RemoteServer caller, PeerViewerReport report)
        {
            if (_clientFactory == null || !Guid.TryParse(report.ViewerId, out var viewerId) || string.IsNullOrWhiteSpace(report.ViewerName))
            {
                return false;
            }

            var now = UtcNow();
            if (!_viewerRosters.TryGetValue(caller.Id, out var cached) || cached.ExpiresUtc <= now)
            {
                // A friend that shows us none of its users answers with nothing (or a
                // refusal) every time: remember that too, instead of asking again on
                // every progress report.
                var users = await _clientFactory.GetClient(caller).GetUsersAsync().ConfigureAwait(false);
                cached = (now + TimeSpan.FromMinutes(5), users ?? new List<UserDto>());
                _viewerRosters[caller.Id] = cached;
            }

            return cached.Users.Any(u => Guid.TryParse(u.Id, out var id) && id == viewerId && string.Equals(u.Name, report.ViewerName, StringComparison.Ordinal));
        }

        /// <summary>Drops the names a report carries, so it shows as "Someone" on a generic device.</summary>
        internal static void Anonymize(PeerViewerReport report)
        {
            report.ViewerName = null;
            report.DeviceName = null;
        }

        /// <summary>How a friend's viewer is named on the dashboard: "bob (friend-a)".</summary>
        internal static string Label(string? viewerName, string friendName)
            => (Clean(viewerName, 64) ?? "Someone") + " (" + friendName + ")";

        /// <summary>The device line: "Chrome · via friend-a".</summary>
        internal static string DeviceLabel(string? deviceName, string friendName)
            => (Clean(deviceName, 64) ?? "Device") + " · via " + friendName;

        /// <summary>How many viewer sessions one friend can have open here at once.</summary>
        private const int MaxViewersPerFriend = 20;

        /// <summary>
        /// The viewer's own device id, so a conversion this server runs for them
        /// (the handoff asks with that device id) shows its details on the same
        /// session. The caller namespace prevents one friend from attaching its
        /// report to another friend's or a local device session.
        /// </summary>
        internal static string DeviceIdFor(RemoteServer caller, PeerViewerReport report)
            => "federation-viewer-" + caller.Id + "-" + (Clean(report.DeviceId, 96) ?? report.Key);

        private async Task SweepAsync()
        {
            if (!await _gate.WaitAsync(0).ConfigureAwait(false))
            {
                return;
            }

            try
            {
                var now = UtcNow();
                foreach (var (key, viewer) in _viewers.ToList())
                {
                    if (now - viewer.LastReportUtc > Silence)
                    {
                        await EndAsync(key).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[Federation] Viewer sweep failed");
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task EndAsync(string key, long? positionTicks = null)
        {
            if (!_viewers.Remove(key, out var viewer))
            {
                return;
            }

            await StopPlaybackAsync(viewer, positionTicks).ConfigureAwait(false);
            try
            {
                await _sessions.ReportSessionEnded(viewer.Session.Id).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[Federation] Could not end a friend viewer's session");
            }
        }

        private async Task StopPlaybackAsync(Viewer viewer, long? positionTicks)
        {
            if (!viewer.Started)
            {
                return;
            }

            try
            {
                await _sessions.OnPlaybackStopped(new PlaybackStopInfo
                {
                    ItemId = viewer.ItemId,
                    SessionId = viewer.Session.Id,
                    MediaSourceId = viewer.ItemId.ToString("N"),
                    PositionTicks = positionTicks ?? viewer.Session.PlayState?.PositionTicks,
                    PlaySessionId = viewer.PlaySessionId
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[Federation] Could not stop a friend viewer's playback");
            }

            viewer.Started = false;
        }

        private static string? Clean(string? value, int max)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var cleaned = new string(value.Where(c => !char.IsControl(c)).ToArray()).Trim();
            if (cleaned.Length == 0)
            {
                return null;
            }

            return cleaned.Length <= max ? cleaned : cleaned.Substring(0, max);
        }

        private sealed class Viewer
        {
            public Viewer(SessionInfo session, string friend, string viewerName)
            {
                Session = session;
                Friend = friend;
                ViewerName = viewerName;
            }

            public SessionInfo Session { get; }

            public string Friend { get; }

            public string ViewerName { get; }

            public Guid ItemId { get; set; }

            public string? Route { get; set; }

            public string? PlaySessionId { get; set; }

            public bool Started { get; set; }

            public DateTime LastReportUtc { get; set; }
        }
    }
}
