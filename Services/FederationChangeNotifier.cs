using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Tells friends when this server's own catalog changed, so they sync it
    /// within minutes instead of at their next scheduled sync (see
    /// FederationSyncService.OnFriendCatalogChanged on the receiving side).
    /// Changes are collected until the library has been quiet for
    /// <see cref="QuietPeriod"/> (at most <see cref="MaxDelay"/> after the first
    /// one), so a library scan sends one notice, not thousands. A friend is only
    /// told when one of the changed titles is visible to it, or when anything was
    /// removed. Items federated in from friends are ignored, so syncs between two
    /// servers cannot notify each other in a loop.
    /// </summary>
    public sealed class FederationChangeNotifier : IDisposable
    {
        private const int MaxTrackedChanges = 100_000;

        private readonly ILogger<FederationChangeNotifier> _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly IRemoteServerClientFactory _clientFactory;
        private readonly FederationPeerAccessService _peerAccess;
        private readonly object _sync = new();
        private readonly HashSet<Guid> _changed = new();
        private bool _removedOrMany;
        private DateTime? _firstChangeUtc;
        private Timer? _timer;
        private bool _started;

        public FederationChangeNotifier(
            ILogger<FederationChangeNotifier> logger,
            ILibraryManager libraryManager,
            IRemoteServerClientFactory clientFactory,
            FederationPeerAccessService peerAccess)
        {
            _logger = logger;
            _libraryManager = libraryManager;
            _clientFactory = clientFactory;
            _peerAccess = peerAccess;
        }

        internal TimeSpan QuietPeriod { get; set; } = TimeSpan.FromMinutes(2);

        internal TimeSpan MaxDelay { get; set; } = TimeSpan.FromMinutes(10);

        public void Start()
        {
            lock (_sync)
            {
                if (_started)
                {
                    return;
                }

                _started = true;
                _timer = new Timer(_ => _ = FlushSafelyAsync(), null, Timeout.Infinite, Timeout.Infinite);
            }

            _libraryManager.ItemAdded += OnItemAdded;
            _libraryManager.ItemUpdated += OnItemUpdated;
            _libraryManager.ItemRemoved += OnItemRemoved;
        }

        public void Dispose()
        {
            _libraryManager.ItemAdded -= OnItemAdded;
            _libraryManager.ItemUpdated -= OnItemUpdated;
            _libraryManager.ItemRemoved -= OnItemRemoved;
            lock (_sync)
            {
                _timer?.Dispose();
                _timer = null;
                _started = false;
            }
        }

        private void OnItemAdded(object? sender, ItemChangeEventArgs e) => Record(e.Item, removed: false);

        private void OnItemUpdated(object? sender, ItemChangeEventArgs e) => Record(e.Item, removed: false);

        private void OnItemRemoved(object? sender, ItemChangeEventArgs e) => Record(e.Item, removed: true);

        internal void Record(BaseItem? item, bool removed)
        {
            if (!IsShareableCatalogItem(item))
            {
                return;
            }

            lock (_sync)
            {
                if (removed || _changed.Count >= MaxTrackedChanges)
                {
                    _removedOrMany = true;
                }
                else
                {
                    _changed.Add(item!.Id);
                }

                var now = DateTime.UtcNow;
                _firstChangeUtc ??= now;
                var due = _firstChangeUtc.Value + MaxDelay;
                if (now + QuietPeriod < due)
                {
                    due = now + QuietPeriod;
                }

                _timer?.Change(due > now ? due - now : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
            }
        }

        private static bool IsShareableCatalogItem(BaseItem? item)
            => item != null
                && (item is Video || item is Audio || item is Series || item is Season || item is MusicAlbum)
                && !FederationLibraryManager.IsIneligibleForOutgoingShare(item)
                && !FederationDownloadService.IsDownloadedFilePath(item.Path);

        private async Task FlushSafelyAsync()
        {
            try
            {
                await FlushAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Federation] Could not send change notices to friends");
            }
        }

        /// <summary>Sends the notices for everything collected so far. Returns how many friends were told.</summary>
        internal async Task<int> FlushAsync(CancellationToken cancellationToken)
        {
            List<Guid> changed;
            bool removedOrMany;
            lock (_sync)
            {
                changed = _changed.ToList();
                removedOrMany = _removedOrMany;
                _changed.Clear();
                _removedOrMany = false;
                _firstChangeUtc = null;
            }

            if (changed.Count == 0 && !removedOrMany)
            {
                return 0;
            }

            var friends = (Plugin.Instance?.Configuration?.RemoteServers ?? new List<RemoteServer>())
                .Where(s => s.Enabled
                    && s.Kind == ServerKind.Jellyfin
                    && !string.IsNullOrEmpty(s.ApiKey)
                    && (s.ShareAllLibraries || (s.SharedLibraryFolderIds?.Count ?? 0) > 0))
                .ToList();
            var told = 0;
            foreach (var friend in friends)
            {
                if (!removedOrMany && !changed.Any(id => _peerAccess.IsItemVisible(friend, null, id)))
                {
                    continue;
                }

                var client = _clientFactory.GetClient(friend);
                if (client != null && await client.NotifyCatalogChangedAsync(cancellationToken).ConfigureAwait(false))
                {
                    told++;
                }
            }

            if (told > 0)
            {
                _logger.LogInformation("[Federation] Told {Count} friend(s) that this server's catalog changed", told);
            }

            return told;
        }
    }
}
