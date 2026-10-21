using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Federation.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>One title a friend's users played lately, as the friend reports it.</summary>
    /// <param name="Id">The friend's own item id ("N" form).</param>
    /// <param name="Viewers">How many different users of the friend played it in the window.</param>
    public sealed record PeerPopular(string Id, int Viewers);

    /// <summary>Wire shape of <c>Peer/Popular</c>.</summary>
    public sealed class PeerPopularBody
    {
        /// <summary>Gets or sets a value indicating whether the friend shares what is popular at all.</summary>
        public bool Shared { get; set; }

        /// <summary>Gets or sets the popular titles, most played by different users first.</summary>
        public List<PeerPopular>? Items { get; set; }
    }

    /// <summary>One "Popular on (friend)" section for a viewer.</summary>
    /// <param name="ServerId">The friend's id in this server's configuration.</param>
    /// <param name="ServerName">The friend's name.</param>
    /// <param name="ItemIds">This server's ids for the titles, most popular first, all visible to the viewer.</param>
    public sealed record FriendPopularSection(string ServerId, string ServerName, IReadOnlyList<Guid> ItemIds);

    /// <summary>
    /// What is popular on friends' servers: each server tells its friends which of
    /// its own titles its users played most in the last 30 days, and shows its
    /// viewers a "Popular on (friend)" row on the Home tab.
    /// </summary>
    /// <remarks>
    /// Owner side (<see cref="GetSharedPopular"/>): read from Jellyfin's own watch
    /// history (played state and last played date per user), so nothing extra is
    /// recorded. Counts of distinct users only, never who; episodes count for
    /// their series and tracks for their album; only this server's own media; and
    /// only titles the asking viewer is already allowed to see. Off with
    /// <see cref="PluginConfiguration.SharePopularWithFriends"/>.
    /// <para>
    /// Viewer side (<see cref="GetSectionsAsync"/>): the same shape as the
    /// favorites rows. Off with <see cref="PluginConfiguration.ShowFriendsPopular"/>.
    /// </para>
    /// </remarks>
    public sealed class FederationFriendPopularService
    {
        /// <summary>Most titles a friend is sent, and most shown per section.</summary>
        internal const int MaxShared = 20;

        /// <summary>How far back a play still counts.</summary>
        internal static readonly TimeSpan Window = TimeSpan.FromDays(30);

        private static readonly TimeSpan OwnerCacheLifetime = TimeSpan.FromMinutes(20);
        private static readonly TimeSpan ViewerCacheLifetime = TimeSpan.FromMinutes(15);
        private static readonly TimeSpan UnsupportedBackoff = TimeSpan.FromHours(6);
        private static readonly TimeSpan FailureRetry = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan OwnerFirstWait = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan FriendTimeout = TimeSpan.FromSeconds(8);

        // Playable things; seasons and series are reached by rolling episodes up.
        private static readonly BaseItemKind[] PlayedKinds =
        {
            BaseItemKind.Movie,
            BaseItemKind.Episode,
            BaseItemKind.Video,
            BaseItemKind.MusicVideo,
            BaseItemKind.Audio
        };

        private readonly ILibraryManager _library;
        private readonly IUserManager _users;
        private readonly IUserDataManager _userData;
        private readonly FederationLibraryManager _federation;
        private readonly FederationPeerAccessService _peerAccess;
        private readonly IRemoteServerClientFactory _clients;
        private readonly ILogger<FederationFriendPopularService> _logger;

        private readonly object _ownerGate = new();
        private Task? _ownerRefresh;

        /// <summary>Clock for the owner cache, replaceable in tests.</summary>
        internal Func<DateTime> OwnerClock { get; set; } = () => DateTime.UtcNow;
        private (DateTime At, IReadOnlyList<(Guid Id, int Count)> Items)? _ownerPopular;
        private readonly ConcurrentDictionary<string, (DateTime At, IReadOnlyList<PeerPopular> Items)> _viewerCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, DateTime> _failedAt = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, DateTime> _unsupportedUntil = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Initializes a new instance of the <see cref="FederationFriendPopularService"/> class.
        /// </summary>
        public FederationFriendPopularService(
            ILibraryManager library,
            IUserManager users,
            IUserDataManager userData,
            FederationLibraryManager federation,
            FederationPeerAccessService peerAccess,
            IRemoteServerClientFactory clients,
            ILogger<FederationFriendPopularService> logger)
        {
            _library = library;
            _users = users;
            _userData = userData;
            _federation = federation;
            _peerAccess = peerAccess;
            _clients = clients;
            _logger = logger;
        }

        /// <summary>
        /// Owner side: the titles this server's users played most in the last 30
        /// days that <paramref name="remoteUserId"/> on <paramref name="caller"/>
        /// may see, most played first. Empty when sharing is off.
        /// </summary>
        public PeerPopularBody GetSharedPopular(RemoteServer caller, string? remoteUserId)
        {
            if (Plugin.Instance?.Configuration?.SharePopularWithFriends == false)
            {
                return new PeerPopularBody { Shared = false, Items = new List<PeerPopular>() };
            }

            var visible = OwnerPopular()
                .Where(p => _peerAccess.IsItemVisible(caller, remoteUserId, p.Id))
                .Take(MaxShared)
                .Select(p => new PeerPopular(p.Id.ToString("N"), p.Count))
                .ToList();
            return new PeerPopularBody { Shared = true, Items = visible };
        }

        /// <summary>
        /// Viewer side: one section per friend that shares what is popular, holding
        /// the titles <paramref name="user"/> can see here. Friends that are
        /// offline, slow or too old to share are left out, and so is a section
        /// with nothing visible.
        /// </summary>
        public async Task<IReadOnlyList<FriendPopularSection>> GetSectionsAsync(User user, CancellationToken cancellationToken)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null || !config.ShowFriendsPopular)
            {
                return Array.Empty<FriendPopularSection>();
            }

            var friends = (config.RemoteServers ?? new List<RemoteServer>())
                .Where(s => s.Enabled
                    && s.Kind == ServerKind.Jellyfin
                    && !string.IsNullOrEmpty(s.ApiKey)
                    && FederationItemPersistenceService.AvailabilityOverride?.IsOffline(s.Id) != true
                    && !(_unsupportedUntil.TryGetValue(s.Id, out var until) && until > DateTime.UtcNow))
                .OrderBy(s => s.Priority)
                .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var lists = await Task.WhenAll(friends.Select(server => FriendListAsync(server, user, cancellationToken))).ConfigureAwait(false);
            var sections = new List<FriendPopularSection>();
            for (var i = 0; i < friends.Count; i++)
            {
                var ids = VisibleLocalIds(friends[i], lists[i], user);
                if (ids.Count > 0)
                {
                    sections.Add(new FriendPopularSection(friends[i].Id, friends[i].Name, ids));
                }
            }

            return sections;
        }

        /// <summary>
        /// Ranks plays inside the window: most different users first, then the most
        /// recently played title. A user playing a title many times counts once.
        /// </summary>
        internal static IReadOnlyList<(Guid Id, int Count)> Rank(IEnumerable<(Guid Title, Guid User, DateTime PlayedAt)> plays, DateTime now)
        {
            var cutoff = now - Window;
            return plays
                .Where(p => p.PlayedAt >= cutoff)
                .GroupBy(p => p.Title)
                .Select(g => (Id: g.Key, Count: g.Select(p => p.User).Distinct().Count(), Latest: g.Max(p => p.PlayedAt)))
                .OrderByDescending(p => p.Count)
                .ThenByDescending(p => p.Latest)
                .ThenBy(p => p.Id)
                .Select(p => (p.Id, p.Count))
                .ToList();
        }

        /// <summary>
        /// The title a play counts for: an episode for its series, a track for its
        /// album, anything else for itself. Null when the parent is unknown.
        /// </summary>
        internal static Guid? RollUpTarget(BaseItem item) => item switch
        {
            Episode episode => episode.SeriesId == Guid.Empty ? null : episode.SeriesId,
            Audio audio => audio.ParentId == Guid.Empty ? null : audio.ParentId,
            _ => item.Id
        };

        /// <summary>
        /// Maps a friend's ids to this server's items in the friend's order,
        /// dropping ids this server doesn't have from that friend and duplicates.
        /// </summary>
        internal static List<Guid> MapToLocal(string serverId, IEnumerable<PeerPopular> items, Func<string, Guid, Guid?> localIdFor)
        {
            var result = new List<Guid>();
            var seen = new HashSet<Guid>();
            foreach (var item in items)
            {
                if (Guid.TryParse(item.Id, out var remoteId)
                    && localIdFor(serverId, remoteId) is { } localId
                    && seen.Add(localId))
                {
                    result.Add(localId);
                }
            }

            return result;
        }

        /// <summary>
        /// The ranked titles, from a 20-minute cache. The scan over every user's
        /// played items is expensive, so it runs once at a time on a pool thread
        /// (never once per request): a stale answer is served while it refreshes,
        /// and only the very first call waits for it, for a bounded time.
        /// </summary>
        private IReadOnlyList<(Guid Id, int Count)> OwnerPopular()
        {
            Task refresh;
            lock (_ownerGate)
            {
                if (_ownerPopular is { } cached)
                {
                    if (OwnerClock() - cached.At < OwnerCacheLifetime)
                    {
                        return cached.Items;
                    }

                    StartOwnerRefreshLocked();
                    return cached.Items;
                }

                refresh = StartOwnerRefreshLocked();
            }

            // Nothing cached yet: give the first scan a few seconds so the first
            // friend to ask gets a real answer, but never hold a request thread long.
            try
            {
                refresh.Wait(OwnerFirstWait);
            }
            catch (AggregateException)
            {
                // Logged by the refresh itself.
            }

            lock (_ownerGate)
            {
                return _ownerPopular?.Items ?? Array.Empty<(Guid Id, int Count)>();
            }
        }

        private Task StartOwnerRefreshLocked()
        {
            return _ownerRefresh is { IsCompleted: false } running
                ? running
                : _ownerRefresh = Task.Run(() =>
                {
                    try
                    {
                        ComputeOwnerPopular();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[Federation] Could not work out what is popular on this server for friends");
                    }
                });
        }

        private void ComputeOwnerPopular()
        {
            var now = DateTime.UtcNow;
            var plays = new List<(Guid Title, Guid User, DateTime PlayedAt)>();
            var targets = new Dictionary<Guid, bool>();
            foreach (var user in _users.GetUsers().ToList())
            {
                if (user.HasPermission(PermissionKind.IsDisabled))
                {
                    continue;
                }

                try
                {
                    var items = _library.GetItemList(new InternalItemsQuery(user)
                    {
                        IsPlayed = true,
                        Recursive = true,
                        IncludeItemTypes = PlayedKinds,
                        DtoOptions = new DtoOptions(false)
                    });
                    foreach (var item in items)
                    {
                        if (FederationLibraryManager.IsIneligibleForOutgoingShare(item)
                            || _userData.GetUserData(user, item)?.LastPlayedDate is not { } playedAt
                            || playedAt < now - Window
                            || RollUpTarget(item) is not { } target)
                        {
                            continue;
                        }

                        // The series or album must be this server's own media too.
                        if (!targets.TryGetValue(target, out var eligible))
                        {
                            eligible = target == item.Id || !FederationLibraryManager.IsIneligibleForOutgoingShare(_library.GetItemById(target));
                            targets[target] = eligible;
                        }

                        if (eligible)
                        {
                            plays.Add((target, user.Id, playedAt));
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[Federation] Could not read one user's watch history for sharing what is popular");
                }
            }

            var ranked = Rank(plays, now).Take(MaxShared * 4).ToList();
            lock (_ownerGate)
            {
                _ownerPopular = (OwnerClock(), ranked);
            }
        }

        private async Task<IReadOnlyList<PeerPopular>> FriendListAsync(RemoteServer server, User user, CancellationToken cancellationToken)
        {
            var key = server.Id + "|" + user.Id.ToString("N");
            if (_viewerCache.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.At < ViewerCacheLifetime)
            {
                return cached.Items;
            }

            // A failed ask (per friend and viewer) is not repeated on every Home load.
            if (_failedAt.TryGetValue(key, out var failedAt) && DateTime.UtcNow - failedAt < FailureRetry)
            {
                return cached.Items ?? Array.Empty<PeerPopular>();
            }

            var client = _clients.GetClient(server);
            if (client == null)
            {
                return cached.Items ?? Array.Empty<PeerPopular>();
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(FriendTimeout);
            try
            {
                var body = await client.GetPopularAsync(user.Id.ToString("N"), user.Username, timeout.Token).ConfigureAwait(false);
                if (body == null)
                {
                    // Unreachable or failed: keep showing what we had.
                    _failedAt[key] = DateTime.UtcNow;
                    return cached.Items ?? Array.Empty<PeerPopular>();
                }

                if (!body.Shared && body.Items == null)
                {
                    _unsupportedUntil[server.Id] = DateTime.UtcNow + UnsupportedBackoff;
                    return Array.Empty<PeerPopular>();
                }

                var items = (IReadOnlyList<PeerPopular>)(body.Items ?? new List<PeerPopular>()).Take(MaxShared).ToList();
                _viewerCache[key] = (DateTime.UtcNow, items);
                _failedAt.TryRemove(key, out _);
                return items;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("[Federation] {Server} took too long to list what is popular", server.Name);
                _failedAt[key] = DateTime.UtcNow;
                return cached.Items ?? Array.Empty<PeerPopular>();
            }
        }

        private List<Guid> VisibleLocalIds(RemoteServer server, IReadOnlyList<PeerPopular> items, User user)
        {
            if (items.Count == 0)
            {
                return new List<Guid>();
            }

            var mapped = MapToLocal(server.Id, items, (serverId, remoteId) =>
            {
                var key = _federation.Cache.TryGetLocalKeyForRemoteItem(serverId, remoteId);
                var entry = key == null ? null : _federation.Cache.GetEntryByKey(key);
                return entry == null ? null : _federation.ComputeItemId(entry);
            });
            if (mapped.Count == 0)
            {
                return mapped;
            }

            // The viewer's own query applies their library access, parental
            // limits and the friend's hiding rules (blocked tags) in one go.
            var visible = _library.GetItemIds(new InternalItemsQuery(user)
            {
                ItemIds = mapped.ToArray(),
                Recursive = true
            }).ToHashSet();
            return mapped.Where(visible.Contains).Take(MaxShared).ToList();
        }
    }
}
