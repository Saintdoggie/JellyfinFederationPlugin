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
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>One title a friend's users favorited, as the friend reports it.</summary>
    /// <param name="Id">The friend's own item id ("N" form).</param>
    /// <param name="Favorites">How many of the friend's users favorited it.</param>
    public sealed record PeerFavorite(string Id, int Favorites);

    /// <summary>Wire shape of <c>Peer/Favorites</c>.</summary>
    public sealed class PeerFavoritesBody
    {
        /// <summary>Gets or sets a value indicating whether the friend shares favorites at all.</summary>
        public bool Shared { get; set; }

        /// <summary>Gets or sets the favorited titles, most favorited first.</summary>
        public List<PeerFavorite>? Items { get; set; }
    }

    /// <summary>One "Favorites on (friend)" section for a viewer.</summary>
    /// <param name="ServerId">The friend's id in this server's configuration.</param>
    /// <param name="ServerName">The friend's name.</param>
    /// <param name="ItemIds">This server's ids for the titles, most favorited first, all visible to the viewer.</param>
    public sealed record FriendFavoritesSection(string ServerId, string ServerName, IReadOnlyList<Guid> ItemIds);

    /// <summary>
    /// Friends' favorites: each server tells its friends which of its own titles its
    /// users favorited, and each server shows its viewers a "Favorites on (friend)"
    /// section on the Favorites tab.
    /// </summary>
    /// <remarks>
    /// Owner side (<see cref="GetSharedFavorites"/>): titles only, never who
    /// favorited them; only this server's own media (never a title it gets from
    /// another friend); and only titles the asking viewer is already allowed to see
    /// under the sharing settings and per-viewer rules. Off with
    /// <see cref="PluginConfiguration.ShareFavoritesWithFriends"/>.
    /// <para>
    /// Viewer side (<see cref="GetSectionsAsync"/>): asks each friend for the
    /// viewer's list, keeps only titles this server has from that friend and the
    /// viewer can see here (library access, parental limits, the friend's hiding
    /// rules), and caches the answer for a few minutes. Off with
    /// <see cref="PluginConfiguration.ShowFriendsFavorites"/>.
    /// </para>
    /// </remarks>
    public sealed class FederationFriendFavoritesService
    {
        /// <summary>Most titles a friend is sent, and most shown per section.</summary>
        internal const int MaxShared = 60;

        private static readonly TimeSpan OwnerCacheLifetime = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan ViewerCacheLifetime = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan UnsupportedBackoff = TimeSpan.FromHours(6);
        private static readonly TimeSpan FailureRetry = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan FriendTimeout = TimeSpan.FromSeconds(8);

        private static readonly BaseItemKind[] SharedKinds =
        {
            BaseItemKind.Movie,
            BaseItemKind.Series,
            BaseItemKind.Season,
            BaseItemKind.Episode,
            BaseItemKind.Video,
            BaseItemKind.MusicVideo,
            BaseItemKind.MusicAlbum,
            BaseItemKind.Audio
        };

        private readonly ILibraryManager _library;
        private readonly IUserManager _users;
        private readonly FederationLibraryManager _federation;
        private readonly FederationPeerAccessService _peerAccess;
        private readonly IRemoteServerClientFactory _clients;
        private readonly ILogger<FederationFriendFavoritesService> _logger;

        private readonly object _ownerGate = new();
        private (DateTime At, IReadOnlyList<(Guid Id, int Count)> Items)? _ownerFavorites;
        private readonly ConcurrentDictionary<string, (DateTime At, IReadOnlyList<PeerFavorite> Items)> _viewerCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, DateTime> _failedAt = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, DateTime> _unsupportedUntil = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Initializes a new instance of the <see cref="FederationFriendFavoritesService"/> class.
        /// </summary>
        public FederationFriendFavoritesService(
            ILibraryManager library,
            IUserManager users,
            FederationLibraryManager federation,
            FederationPeerAccessService peerAccess,
            IRemoteServerClientFactory clients,
            ILogger<FederationFriendFavoritesService> logger)
        {
            _library = library;
            _users = users;
            _federation = federation;
            _peerAccess = peerAccess;
            _clients = clients;
            _logger = logger;
        }

        /// <summary>
        /// Owner side: the titles this server's users favorited that
        /// <paramref name="remoteUserId"/> on <paramref name="caller"/> may see, most
        /// favorited first. Empty when sharing favorites is off.
        /// </summary>
        public PeerFavoritesBody GetSharedFavorites(RemoteServer caller, string? remoteUserId)
        {
            if (Plugin.Instance?.Configuration?.ShareFavoritesWithFriends == false)
            {
                return new PeerFavoritesBody { Shared = false, Items = new List<PeerFavorite>() };
            }

            var visible = OwnerFavorites()
                .Where(f => _peerAccess.IsItemVisible(caller, remoteUserId, f.Id))
                .Take(MaxShared)
                .Select(f => new PeerFavorite(f.Id.ToString("N"), f.Count))
                .ToList();
            return new PeerFavoritesBody { Shared = true, Items = visible };
        }

        /// <summary>
        /// Viewer side: one section per friend that shares favorites, holding the
        /// titles <paramref name="user"/> can see here. Friends that are offline,
        /// slow or too old to share are left out; a section with nothing visible is
        /// left out.
        /// </summary>
        public async Task<IReadOnlyList<FriendFavoritesSection>> GetSectionsAsync(User user, CancellationToken cancellationToken)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null || !config.ShowFriendsFavorites)
            {
                return Array.Empty<FriendFavoritesSection>();
            }

            var friends = (config.RemoteServers ?? new List<RemoteServer>())
                .Where(s => s.Enabled
                    && s.Kind == ServerKind.Jellyfin
                    && s.PeerSharesFavorites != false
                    && !string.IsNullOrEmpty(s.ApiKey)
                    && FederationItemPersistenceService.AvailabilityOverride?.IsOffline(s.Id) != true
                    && !(_unsupportedUntil.TryGetValue(s.Id, out var until) && until > DateTime.UtcNow))
                .OrderBy(s => s.Priority)
                .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var lists = await Task.WhenAll(friends.Select(server => FriendListAsync(server, user, cancellationToken))).ConfigureAwait(false);
            var sections = new List<FriendFavoritesSection>();
            for (var i = 0; i < friends.Count; i++)
            {
                var ids = VisibleLocalIds(friends[i], lists[i], user);
                if (ids.Count > 0)
                {
                    sections.Add(new FriendFavoritesSection(friends[i].Id, friends[i].Name, ids));
                }
            }

            return sections;
        }

        /// <summary>
        /// Ranks favorites: most users first, then the most recently added title.
        /// </summary>
        internal static IReadOnlyList<(Guid Id, int Count)> Rank(IEnumerable<(Guid Id, DateTime Added)> favorites)
            => favorites
                .GroupBy(f => f.Id)
                .Select(g => (Id: g.Key, Count: g.Count(), Added: g.Max(f => f.Added)))
                .OrderByDescending(f => f.Count)
                .ThenByDescending(f => f.Added)
                .ThenBy(f => f.Id)
                .Select(f => (f.Id, f.Count))
                .ToList();

        /// <summary>
        /// Maps a friend's ids to this server's items in the friend's order,
        /// dropping ids this server doesn't have from that friend and duplicates.
        /// </summary>
        internal static List<Guid> MapToLocal(string serverId, IEnumerable<PeerFavorite> favorites, Func<string, Guid, Guid?> localIdFor)
        {
            var result = new List<Guid>();
            var seen = new HashSet<Guid>();
            foreach (var favorite in favorites)
            {
                if (Guid.TryParse(favorite.Id, out var remoteId)
                    && localIdFor(serverId, remoteId) is { } localId
                    && seen.Add(localId))
                {
                    result.Add(localId);
                }
            }

            return result;
        }

        private IReadOnlyList<(Guid Id, int Count)> OwnerFavorites()
        {
            lock (_ownerGate)
            {
                if (_ownerFavorites is { } cached && DateTime.UtcNow - cached.At < OwnerCacheLifetime)
                {
                    return cached.Items;
                }
            }

            var favorites = new List<(Guid Id, DateTime Added)>();
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
                        IsFavorite = true,
                        Recursive = true,
                        IncludeItemTypes = SharedKinds,
                        DtoOptions = new DtoOptions(false)
                    });
                    favorites.AddRange(items
                        .Where(i => !FederationLibraryManager.IsIneligibleForOutgoingShare(i))
                        .Select(i => (i.Id, i.DateCreated)));
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[Federation] Could not read one user's favorites for sharing");
                }
            }

            var ranked = Rank(favorites).Take(MaxShared * 8).ToList();
            lock (_ownerGate)
            {
                _ownerFavorites = (DateTime.UtcNow, ranked);
            }

            return ranked;
        }

        private async Task<IReadOnlyList<PeerFavorite>> FriendListAsync(RemoteServer server, User user, CancellationToken cancellationToken)
        {
            var key = server.Id + "|" + user.Id.ToString("N");
            if (_viewerCache.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.At < ViewerCacheLifetime)
            {
                return cached.Items;
            }

            // A failed ask (per friend and viewer) is not repeated on every scan.
            if (_failedAt.TryGetValue(key, out var failedAt) && DateTime.UtcNow - failedAt < FailureRetry)
            {
                return cached.Items ?? Array.Empty<PeerFavorite>();
            }

            var client = _clients.GetClient(server);
            if (client == null)
            {
                return cached.Items ?? Array.Empty<PeerFavorite>();
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(FriendTimeout);
            try
            {
                var body = await client.GetFavoritesAsync(user.Id.ToString("N"), user.Username, timeout.Token).ConfigureAwait(false);
                if (body == null)
                {
                    // Unreachable or failed: keep showing what we had.
                    _failedAt[key] = DateTime.UtcNow;
                    return cached.Items ?? Array.Empty<PeerFavorite>();
                }

                if (!body.Shared && body.Items == null)
                {
                    _unsupportedUntil[server.Id] = DateTime.UtcNow + UnsupportedBackoff;
                    return Array.Empty<PeerFavorite>();
                }

                var items = (IReadOnlyList<PeerFavorite>)(body.Items ?? new List<PeerFavorite>()).Take(MaxShared).ToList();
                _viewerCache[key] = (DateTime.UtcNow, items);
                _failedAt.TryRemove(key, out _);
                return items;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("[Federation] {Server} took too long to list its favorites", server.Name);
                _failedAt[key] = DateTime.UtcNow;
                return cached.Items ?? Array.Empty<PeerFavorite>();
            }
        }

        private List<Guid> VisibleLocalIds(RemoteServer server, IReadOnlyList<PeerFavorite> favorites, User user)
        {
            if (favorites.Count == 0)
            {
                return new List<Guid>();
            }

            var mapped = MapToLocal(server.Id, favorites, (serverId, remoteId) =>
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
