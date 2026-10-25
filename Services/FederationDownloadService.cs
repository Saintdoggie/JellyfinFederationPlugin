using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Federation.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// "Download to server": fetches a federated item's whole media file from the
    /// remote it's currently streamed from and saves it into a dedicated local
    /// library, so it plays back afterward like any other local file with no
    /// dependency on the friend's server being reachable. Separate from, and does
    /// not touch, the Proxy-mode live streaming path in
    /// <see cref="FederationStreamHandler"/>.
    /// </summary>
    public class FederationDownloadService
    {
        private const string DownloadsSubFolder = "federation-downloads";
        private const string DownloadsLibraryName = "Federation Downloads";

        // Mirrors RemoteServerClient's own DownloadHttpClient: a plain metadata
        // HttpClient's default timeout is far too short for a whole movie, and
        // this is a one-shot server-side fetch, not a live client-facing relay.
        private static readonly HttpClient BrowseDownloadHttpClient = new HttpClient(FederationHttp.Handler()) { Timeout = TimeSpan.FromHours(6) };
        private static readonly SemaphoreSlim BrowseDownloadSlots = new(2, 2);

        private readonly ILibraryManager _libraryManager;
        private readonly FederationLibraryManager _federationManager;
        private readonly IRemoteServerClientFactory _clientFactory;
        private readonly ExternalCatalogRegistry _externalCatalogs;
        private readonly ILogger<FederationDownloadService> _logger;
        private readonly Func<RemoteServer, string, string, IProgress<(long BytesRead, long? TotalBytes)>, CancellationToken, Task>? _qualityDownloadOverride;
        private readonly Func<BaseItem, RemoteServer, string, bool>? _qualityUpgradeValidatorOverride;
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _cancellationSources = new();
        private readonly ConcurrentDictionary<string, string> _pendingDownloadOrigins = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, byte> _pauseRequested = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, byte> _resumeWhenPaused = new(StringComparer.OrdinalIgnoreCase);
        private readonly FederationDownloadQueue _queue = new();

        /// <summary>
        /// Initializes a new instance of the <see cref="FederationDownloadService"/> class.
        /// </summary>
        public FederationDownloadService(
            ILibraryManager libraryManager,
            FederationLibraryManager federationManager,
            IRemoteServerClientFactory clientFactory,
            ExternalCatalogRegistry externalCatalogs,
            ILogger<FederationDownloadService> logger)
            : this(libraryManager, federationManager, clientFactory, externalCatalogs, logger, null, null)
        {
        }

        /// <summary>
        /// Test seam for the destructive replacement state machine. Production
        /// dependency injection always uses the public constructor above; tests
        /// can substitute only the long-running transfer and fresh-candidate
        /// check while exercising the real staging, validation, commit,
        /// cancellation, and delete ordering below.
        /// </summary>
        internal FederationDownloadService(
            ILibraryManager libraryManager,
            FederationLibraryManager federationManager,
            IRemoteServerClientFactory clientFactory,
            ExternalCatalogRegistry externalCatalogs,
            ILogger<FederationDownloadService> logger,
            Func<RemoteServer, string, string, IProgress<(long BytesRead, long? TotalBytes)>, CancellationToken, Task>? qualityDownloadOverride,
            Func<BaseItem, RemoteServer, string, bool>? qualityUpgradeValidatorOverride)
        {
            _libraryManager = libraryManager;
            _federationManager = federationManager;
            _clientFactory = clientFactory;
            _externalCatalogs = externalCatalogs;
            _logger = logger;
            _qualityDownloadOverride = qualityDownloadOverride;
            _qualityUpgradeValidatorOverride = qualityUpgradeValidatorOverride;
            _libraryManager.ItemAdded += OnLibraryItemAdded;
        }

        /// <summary>
        /// Resolves the on-disk root used for downloaded files:
        /// <c>&lt;plugin data dir&gt;/federation-downloads/</c>.
        /// </summary>
        internal static string GetDownloadsRoot()
        {
            var dataPath = Plugin.Instance?.DataFolderPath;
            return string.IsNullOrEmpty(dataPath) ? string.Empty : Path.Combine(dataPath, DownloadsSubFolder);
        }

        /// <summary>
        /// True when <paramref name="path"/> is a file inside the plugin's
        /// federation-downloads folder (not the folder itself). Used as a
        /// share-eligibility gate before the post-scan provider-id stamp lands.
        /// </summary>
        internal static bool IsDownloadedFilePath(string? path)
        {
            return IsPathInsideRoot(path, GetDownloadsRoot()) || DownloadedFileRegistry.Contains(path);
        }

        /// <summary>
        /// Stamps any already-scanned files under the downloads folder. Covers
        /// copies downloaded before this provider id existed, and items whose
        /// scan finished after process restart.
        /// </summary>
        internal async Task StampExistingDownloadedItemsAsync(CancellationToken cancellationToken)
        {
            var root = GetDownloadsRoot();
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                return;
            }

            IReadOnlyList<BaseItem> items;
            try
            {
                items = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    Recursive = true,
                    IncludeItemTypes = new[]
                    {
                        BaseItemKind.Movie,
                        BaseItemKind.Episode,
                        BaseItemKind.Video,
                        BaseItemKind.Audio,
                        BaseItemKind.MusicVideo,
                        BaseItemKind.Series,
                        BaseItemKind.Season
                    }
                }) ?? Array.Empty<BaseItem>();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Federation] Could not enumerate downloaded federated files to stamp");
                return;
            }

            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsPathInsideRoot(item.Path, root))
                {
                    continue;
                }

                await PersistDownloadedStampAsync(item, "downloaded", cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Validates the request and starts a background download. Returns immediately
        /// with an operation id to poll via <see cref="DownloadProgressTracker.Get"/> -
        /// a whole movie can take far longer than a single HTTP request should be held
        /// open for.
        /// </summary>
        public (bool Success, string Message, string? OperationId) StartDownload(string localItemId, string? destination = null)
        {
            if (!Guid.TryParse(localItemId, out var itemGuid))
            {
                return (false, "Invalid item id.", null);
            }

            var item = _libraryManager.GetItemById(itemGuid);
            if (item == null)
            {
                return (false, "Item not found.", null);
            }

            var key = FederationLibraryManager.GetFederationKey(item);
            if (key == null)
            {
                return (false, "This item isn't streamed from a friend's server - nothing to download.", null);
            }

            // Admin gates: global incoming filter + per-friend allowDownloads
            var cfg = Plugin.Instance?.Configuration;
            if (cfg?.IncomingFilter != null && !cfg.IncomingFilter.AllowDownloads)
            {
                return (false, "Downloads are disabled in Catalog → Incoming content filters.", null);
            }

            if (DownloadProgressTracker.IsDownloadingItem(localItemId))
            {
                if (TryResumeExisting(localItemId, out var resumedId))
                {
                    return (true, "Resuming download.", resumedId);
                }

                return (false, "Already downloading.", null);
            }

            var entry = _federationManager.Cache.GetEntryByKey(key);
            var source = entry?.GetPrimarySource();
            if (entry == null || source == null)
            {
                return (false, "Could not find this item's source server.", null);
            }

            var srcServer = cfg?.RemoteServers?.FirstOrDefault(s => s.Id == source.ServerId);
            if (srcServer != null && srcServer.Kind != ServerKind.Jellyfin && !srcServer.AllowDownloads)
            {
                return (false, $"Downloads from {srcServer.Name} are disabled (Catalog → {srcServer.Name} → Download access).", null);
            }

            var details = new DownloadDetails(destination, entry.ItemType, entry.Metadata.ProductionYear, entry.Metadata.SeriesName, entry.Metadata.ParentIndexNumber, entry.Metadata.IndexNumber);
            if (CheckDestination(details) is { } destinationError)
            {
                return (false, destinationError, null);
            }

            var job = NewJob(
                DownloadJob.KindFederated,
                localItemId,
                entry.Metadata.Name,
                source.ServerId,
                source.RemoteItemId.ToString(),
                key,
                localItemId,
                bulk: false);
            Apply(job, details);
            EnqueueAndStart(job, ct => RunDownloadAsync(job, itemGuid, entry, source, ct));

            return (true, "Download started.", job.OperationId);
        }

        /// <summary>
        /// Starts a background download of an item browsed straight off a remote
        /// server's catalog (see <c>Browse/{serverId}/Items</c>) - distinct from
        /// <see cref="StartDownload"/>, which requires the item to already be a
        /// materialized federated item in a mapped library. This one only needs
        /// the server and the remote's own native item id, so it works for
        /// anything visible in the browse picker whether or not it has ever been
        /// synced into a local library.
        /// </summary>
        public (bool Success, string Message, string? OperationId) StartBrowseDownload(
            string serverId,
            string nativeItemId,
            string itemName,
            bool bulk = false,
            DownloadDetails? details = null)
        {
            if (string.IsNullOrWhiteSpace(serverId) || string.IsNullOrWhiteSpace(nativeItemId))
            {
                return (false, "Server and item are required.", null);
            }

            var cfg = Plugin.Instance?.Configuration;
            var server = cfg?.RemoteServers?.FirstOrDefault(s => s.Id == serverId);
            if (server == null || !server.Enabled)
            {
                return (false, "Server not found.", null);
            }

            if (cfg?.IncomingFilter != null && !cfg.IncomingFilter.AllowDownloads)
            {
                return (false, "Downloads are disabled in Catalog → Incoming content filters.", null);
            }

            // A Jellyfin content owner is authoritative and grants a purpose-
            // scoped token when the worker starts. For external sources such as
            // Plex there is no Federation peer endpoint, so the local connection
            // setting is the only available consent boundary.
            if (server.Kind != ServerKind.Jellyfin && !server.AllowDownloads)
            {
                return (false, $"Downloads from {server.Name} are disabled (Catalog → {server.Name} → Download access).", null);
            }

            if (bulk && server.Kind != ServerKind.Jellyfin && !server.AllowBulkDownloads)
            {
                return (false, $"Bulk downloads from {server.Name} are not enabled.", null);
            }

            // Reuses the same tracker as StartDownload's per-item dedupe, keyed on
            // a browse-specific string rather than a local item Guid since there
            // is no local item yet - LocalItemId is just an opaque dedupe/display
            // key to DownloadProgressTracker either way.
            var dedupeKey = $"browse:{serverId}:{nativeItemId}";
            if (DownloadProgressTracker.IsDownloadingItem(dedupeKey))
            {
                if (TryResumeExisting(dedupeKey, out var resumedId))
                {
                    return (true, "Resuming download.", resumedId);
                }

                return (false, "Already downloading.", null);
            }

            if (CheckDestination(details) is { } destinationError)
            {
                return (false, destinationError, null);
            }

            var job = NewJob(
                DownloadJob.KindBrowse,
                dedupeKey,
                itemName,
                server.Id,
                nativeItemId,
                federationKey: null,
                localItemId: null,
                bulk);
            Apply(job, details);
            EnqueueAndStart(job, ct => RunBrowseDownloadAsync(job, server, nativeItemId, itemName, bulk, ct));

            return (true, "Download started.", job.OperationId);
        }

        /// <summary>
        /// "Prefer higher quality" review flow's Apply step (see
        /// <see cref="FederationQualityAdvisorService"/>): downloads the
        /// higher-quality remote copy first, and only once that fully succeeds
        /// removes the old, lower-quality local item and its file. Never the
        /// other way around - the old copy is never touched unless the new one
        /// is confirmed safely on disk, so a failed or cancelled download always
        /// leaves the admin with the copy they started with rather than neither.
        /// </summary>
        public (bool Success, string Message, string? OperationId) StartQualityReplace(string localItemId, string serverId, string nativeItemId, string itemName)
        {
            if (!Guid.TryParse(localItemId, out var itemGuid))
            {
                return (false, "Invalid item id.", null);
            }

            var item = _libraryManager.GetItemById(itemGuid);
            if (item == null)
            {
                return (false, "Item not found.", null);
            }

            var cfg = Plugin.Instance?.Configuration;
            if (cfg?.PreferHigherQualityRemotes != true || cfg.EnableQualityReplacementActions != true)
            {
                return (false, "Quality replacement actions are not enabled.", null);
            }

            if (FederationLibraryManager.GetFederationKey(item) != null || string.IsNullOrWhiteSpace(item.Path))
            {
                return (false, "The approved old copy is no longer a local media file.", null);
            }

            var server = cfg?.RemoteServers?.FirstOrDefault(s => s.Id == serverId);
            if (server == null || !server.Enabled)
            {
                return (false, "Server not found.", null);
            }

            if (string.IsNullOrWhiteSpace(nativeItemId))
            {
                return (false, "Remote item id is required.", null);
            }

            if (cfg?.IncomingFilter != null && !cfg.IncomingFilter.AllowDownloads)
            {
                return (false, "Downloads are disabled in Catalog → Incoming content filters.", null);
            }

            if (!server.AllowDownloads)
            {
                return (false, $"Downloads from {server.Name} are disabled (Catalog → {server.Name} → Download access).", null);
            }

            if (!IsExactQualityUpgrade(item, server, nativeItemId))
            {
                return (false, "The approved local/remote match is stale or is no longer a quality upgrade.", null);
            }

            var dedupeKey = $"qreplace:{localItemId}";
            if (DownloadProgressTracker.IsDownloadingItem(dedupeKey))
            {
                if (TryResumeExisting(dedupeKey, out var resumedId))
                {
                    return (true, "Resuming download.", resumedId);
                }

                return (false, "Already downloading.", null);
            }

            var job = NewJob(
                DownloadJob.KindQualityReplace,
                dedupeKey,
                item.Name,
                server.Id,
                nativeItemId,
                federationKey: null,
                localItemId,
                bulk: false);

            // The better copy goes in the same library as the one it replaces.
            Apply(job, new DownloadDetails(
                LibraryHolding(item.Path),
                item.GetBaseItemKind().ToString(),
                item.ProductionYear,
                (item as MediaBrowser.Controller.Entities.TV.Episode)?.SeriesName,
                item.ParentIndexNumber,
                item.IndexNumber));
            EnqueueAndStart(job, ct => RunQualityReplaceAsync(job, itemGuid, server, nativeItemId, item.Name, ct));

            return (true, "Download started.", job.OperationId);
        }

        /// <summary>
        /// Admin-triggered: cancels an in-progress download. No-ops (successfully)
        /// if the operation already finished or was never known - cancelling
        /// something that's already done isn't an error from the caller's side.
        /// </summary>
        public (bool Success, string Message) CancelDownload(string operationId)
        {
            _pauseRequested.TryRemove(operationId, out _);
            if (_cancellationSources.TryGetValue(operationId, out var cts))
            {
                cts.Cancel();
                return (true, "Cancelling...");
            }

            var job = _queue.Get(operationId);
            if (job != null)
            {
                FailPermanently(job, "Cancelled.");
                DeletePartialFile(job, job.PartialPath);
                return (true, "Cancelled.");
            }

            var progress = DownloadProgressTracker.Get(operationId);
            if (progress == null)
            {
                return (false, "Download not found.");
            }

            return (true, "Already finished.");
        }

        /// <summary>
        /// Rebuilds in-memory progress rows from the on-disk queue so the UI
        /// can show unfinished transfers immediately after a restart.
        /// </summary>
        internal void HydrateProgressFromQueue()
        {
            foreach (var job in _queue.Load())
            {
                DownloadProgressTracker.Restore(ToProgress(job));
            }
        }

        /// <summary>
        /// Restarts any unfinished jobs whose source is reachable. Called on
        /// plugin startup after a short delay so configuration is loaded.
        /// </summary>
        internal Task ResumeIncompleteDownloadsAsync(CancellationToken cancellationToken)
        {
            foreach (var job in _queue.Load())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsServerOffline(job.ServerId))
                {
                    PauseJob(job, "Paused — the source server is unreachable");
                    continue;
                }

                ResumeJob(job);
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Holds every in-flight transfer from <paramref name="serverId"/> when
        /// that friend goes offline. Partial files stay on disk.
        /// </summary>
        internal void PauseForServer(string serverId, string reason)
        {
            foreach (var job in _queue.Load())
            {
                if (!string.Equals(job.ServerId, serverId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                _pauseRequested[job.OperationId] = 1;
                if (_cancellationSources.TryGetValue(job.OperationId, out var cts))
                {
                    try
                    {
                        cts.Cancel();
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                }
                else
                {
                    PauseJob(job, reason);
                }
            }
        }

        /// <summary>
        /// Continues paused transfers from a friend that just came back.
        /// </summary>
        internal void ResumePausedForServer(string serverId)
        {
            foreach (var job in _queue.Load())
            {
                if (!string.Equals(job.ServerId, serverId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (_cancellationSources.ContainsKey(job.OperationId))
                {
                    // The pause cancel is still unwinding; continue once PauseJob runs.
                    _resumeWhenPaused[job.OperationId] = 1;
                    continue;
                }

                ResumeJob(job);
            }
        }

        private async Task RunDownloadAsync(DownloadJob job, Guid itemGuid, FederatedCacheEntry entry, FederatedSource source, CancellationToken cancellationToken)
        {
            var operationId = job.OperationId;
            string? destinationPath = job.DestinationPath;
            string? partialPath = job.PartialPath;
            _cancellationSources.TryGetValue(operationId, out var ownedCts);
            try
            {
                var srcServer = _federationManager.GetServer(source.ServerId);
                if (srcServer == null)
                {
                    FailPermanently(job, "Source server is not configured.");
                    return;
                }

                if (ShouldHoldForOfflineServer(srcServer, job))
                {
                    return;
                }

                var targetError = PrepareTarget(job, entry.Metadata.Name, SafeExtension(entry.Metadata.Container, "mkv"), srcServer.Name);
                if (targetError != null)
                {
                    FailPermanently(job, targetError);
                    return;
                }

                destinationPath = job.DestinationPath!;
                partialPath = job.PartialPath!;

                DownloadProgressTracker.SetDestinationPath(operationId, destinationPath);

                var transferId = srcServer.Kind == ServerKind.Jellyfin
                    ? source.RemoteItemId.ToString()
                    : entry.Metadata.RemoteNativeId;
                if (string.IsNullOrEmpty(transferId))
                {
                    FailPermanently(job, "Could not resolve this item's id on the remote server - try refreshing the library.");
                    return;
                }

                await TransferJobAsync(job, srcServer, transferId, bulk: false, "Downloading...", cancellationToken).ConfigureAwait(false);

                if (!ValidateCompletedDownload(partialPath))
                {
                    throw new InvalidDataException("The downloaded file was empty, truncated, or was not valid media.");
                }

                File.Move(partialPath, destinationPath);
                partialPath = null;

                // The file is committed. Nothing after this point may fail the job: a
                // failed/paused job here would re-download a movie that is already on disk.
                await RunPostMoveStepsAsync(destinationPath, source.ServerId, job).ConfigureAwait(false);

                try
                {
                    var item = _libraryManager.GetItemById(itemGuid);
                    if (item != null)
                    {
                        _libraryManager.DeleteItem(item, new DeleteOptions { DeleteFileLocation = false });
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Federation] Downloaded {Name} but could not remove its virtual item", entry.Metadata.Name);
                }

                // Without this, the very next scheduled refresh sees the friend
                // still reporting this item (nothing about a local download tells
                // them to stop), re-upserts it into the federation cache, and
                // recreates the same virtual item right back - now duplicated
                // alongside the real downloaded file. Reuses the existing
                // "admin hid this federated item" suppression list (see
                // PluginConfiguration.HiddenFederatedItemIds and
                // FederationItemPersistenceService's hiddenKeys) rather than
                // inventing separate "downloaded" state - a downloaded item
                // should never be re-materialized as a virtual one, exactly the
                // same outcome an admin hiding it by hand already gets.
                try
                {
                    var config = Plugin.Instance?.Configuration;
                    if (config != null)
                    {
                        config.HiddenFederatedItemIds ??= new List<string>();
                        if (!config.HiddenFederatedItemIds.Contains(entry.Key, StringComparer.OrdinalIgnoreCase))
                        {
                            config.HiddenFederatedItemIds.Add(entry.Key);
                            Plugin.Instance?.SaveConfiguration();
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Federation] Downloaded {Name} but could not record it as hidden", entry.Metadata.Name);
                }

                _logger.LogInformation("[Federation] Downloaded {Name} to {Path}", entry.Metadata.Name, destinationPath);
                SucceedJob(job, "Downloaded. It will appear as a local item after the next library scan.");
            }
            catch (Exception ex)
            {
                HandleTransferFailure(job, entry.Metadata.Name, ex, cancellationToken, partialPath);
            }
            finally
            {
                ReleaseOwnedCts(operationId, ownedCts);
            }
        }

        /// <summary>
        /// Stamp, library-target setup and scan for a file that has already been moved into
        /// place. Best effort: the download itself succeeded, so a failure here is logged
        /// and the next library scan / startup stamp pass picks the file up.
        /// </summary>
        private async Task RunPostMoveStepsAsync(string destinationPath, string origin, DownloadJob job)
        {
            try
            {
                await StampDownloadedCopyAsync(destinationPath, origin).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Federation] Downloaded file is in place but stamping failed for {Path}", destinationPath);
            }

            try
            {
                await FinishTargetAsync(job).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Federation] Downloaded file is in place but the library target setup failed for {Path}", destinationPath);
            }

            try
            {
                _libraryManager.QueueLibraryScan();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Federation] Downloaded file is in place but a library scan could not be queued");
            }
        }

        private async Task RunBrowseDownloadAsync(
            DownloadJob job,
            RemoteServer server,
            string nativeItemId,
            string itemName,
            bool bulk,
            CancellationToken cancellationToken)
        {
            var operationId = job.OperationId;
            string? destinationPath = job.DestinationPath;
            string? partialPath = job.PartialPath;
            var slotHeld = false;
            _cancellationSources.TryGetValue(operationId, out var ownedCts);
            try
            {
                DownloadProgressTracker.Update(operationId, "Queued...");
                await BrowseDownloadSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
                slotHeld = true;

                var currentServer = Plugin.Instance?.Configuration?.RemoteServers?.FirstOrDefault(s => s.Id == server.Id && s.Enabled);
                if (currentServer == null) throw new InvalidOperationException("The selected server is no longer connected or enabled.");
                server = currentServer;

                if (ShouldHoldForOfflineServer(server, job))
                {
                    return;
                }

                var targetError = PrepareTarget(job, itemName, "mkv", server.Name);
                if (targetError != null)
                {
                    FailPermanently(job, targetError);
                    return;
                }

                destinationPath = job.DestinationPath!;
                partialPath = job.PartialPath!;

                DownloadProgressTracker.SetDestinationPath(operationId, destinationPath);

                await TransferJobAsync(job, server, nativeItemId, bulk, "Downloading...", cancellationToken).ConfigureAwait(false);

                if (!ValidateCompletedDownload(partialPath))
                {
                    throw new InvalidDataException("The downloaded file was empty, truncated, or was not valid media.");
                }

                File.Move(partialPath, destinationPath);
                partialPath = null;

                await RunPostMoveStepsAsync(destinationPath, server.Id, job).ConfigureAwait(false);

                _logger.LogInformation("[Federation] Browse-downloaded {Name} from {Server} to {Path}", itemName, server.Name, destinationPath);
                SucceedJob(job, "Downloaded. It will appear as a local item after the next library scan.");
            }
            catch (Exception ex)
            {
                HandleTransferFailure(job, itemName, ex, cancellationToken, partialPath);
            }
            finally
            {
                if (slotHeld)
                {
                    BrowseDownloadSlots.Release();
                }

                ReleaseOwnedCts(operationId, ownedCts);
            }
        }

        private async Task RunQualityReplaceAsync(DownloadJob job, Guid oldItemGuid, RemoteServer server, string nativeItemId, string itemName, CancellationToken cancellationToken)
        {
            var operationId = job.OperationId;
            string? partialPath = job.PartialPath;
            string? committedPath = job.DestinationPath;
            _cancellationSources.TryGetValue(operationId, out var ownedCts);
            try
            {
                if (ShouldHoldForOfflineServer(server, job))
                {
                    return;
                }

                var targetError = PrepareTarget(job, itemName, "mkv", server.Name);
                if (targetError != null)
                {
                    FailPermanently(job, targetError);
                    return;
                }

                committedPath = job.DestinationPath!;
                partialPath = job.PartialPath!;

                DownloadProgressTracker.SetDestinationPath(operationId, committedPath);

                await TransferJobAsync(job, server, nativeItemId, bulk: false, "Downloading higher-quality copy...", cancellationToken).ConfigureAwait(false);

                if (!ValidateCompletedDownload(partialPath))
                {
                    throw new InvalidDataException("The downloaded replacement was empty, truncated, or did not look like media.");
                }

                // Same-directory move is atomic on supported filesystems: the
                // managed library never observes a partially-written movie.
                File.Move(partialPath, committedPath);
                partialPath = null;

                await StampDownloadedCopyAsync(committedPath, server.Id).ConfigureAwait(false);

                // Make the destination library ready before touching the old item.
                // A failure here leaves both the committed new file and old copy.
                await FinishTargetAsync(job).ConfigureAwait(false);

                // Re-resolve the exact approved id immediately before deletion;
                // never retain and act on the object captured before a long download.
                var oldItem = _libraryManager.GetItemById(oldItemGuid);
                if (oldItem == null
                    || FederationLibraryManager.GetFederationKey(oldItem) != null
                    || string.IsNullOrWhiteSpace(oldItem.Path)
                    || !IsExactQualityUpgrade(oldItem, server, nativeItemId))
                {
                    throw new InvalidOperationException("The approved local copy changed before replacement; the downloaded copy was kept and the old item was not removed.");
                }

                DownloadProgressTracker.Update(operationId, "Removing the approved old copy...");
                _libraryManager.DeleteItem(oldItem, new DeleteOptions { DeleteFileLocation = true });

                // Deletion already completed successfully. A scan scheduling
                // failure must not turn that completed replacement into an
                // ambiguous failed operation or invite an unsafe retry.
                try
                {
                    _libraryManager.QueueLibraryScan();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Federation] Replacement succeeded but a follow-up library scan could not be queued");
                }

                _logger.LogInformation(
                    "[Federation] Quality-replaced {Name}: downloaded a higher-quality copy from {Server} and removed the old local copy",
                    itemName,
                    server.Name);
                SucceedJob(job, "Downloaded a higher-quality copy and removed the old one. It will appear as a local item after the next library scan.");
            }
            catch (Exception ex)
            {
                var preservation = committedPath != null && File.Exists(committedPath)
                    ? " The downloaded copy was kept. Verify the old copy before retrying."
                    : " The old copy was not touched.";
                if (ex is OperationCanceledException && !_pauseRequested.ContainsKey(operationId) && cancellationToken.IsCancellationRequested)
                {
                    HandleTransferFailure(job, itemName, ex, cancellationToken, partialPath, "Cancelled. The old copy was not touched.");
                }
                else if (ex is OperationCanceledException || IsTransientDownloadFailure(ex))
                {
                    HandleTransferFailure(job, itemName, ex, cancellationToken, partialPath);
                }
                else
                {
                    _logger.LogError(ex, "[Federation] Quality-replace failed for {Name}", itemName);
                    FailPermanently(job, "Replacement failed: " + ex.Message + preservation);
                    DeletePartialFile(job, partialPath);
                }
            }
            finally
            {
                ReleaseOwnedCts(operationId, ownedCts);
            }
        }

        /// <summary>
        /// Streams an already-credentialed, absolute URL (see
        /// <see cref="IExternalCatalogProvider.ResolveStreamUrlAsync"/>) straight to
        /// disk. The Jellyfin-peer path has its own equivalent
        /// (<see cref="RemoteServerClient.DownloadToFileAsync"/>) that goes through a
        /// scoped playback token instead - this is the generic fallback for any
        /// external provider, which hands back a complete fetchable URL rather than
        /// a token to mint one from.
        /// </summary>
        private static Task DownloadUrlToFileAsync(
            string url,
            string destinationPath,
            IProgress<(long BytesRead, long? TotalBytes)> progress,
            CancellationToken cancellationToken,
            long resumeFrom = 0)
        {
            return DownloadTransfer.CopyUrlToFileAsync(
                BrowseDownloadHttpClient,
                url,
                destinationPath,
                resumeFrom,
                progress,
                cancellationToken);
        }

        /// <summary>Checks a requested destination before anything is queued. Null when fine.</summary>
        private string? CheckDestination(DownloadDetails? details)
        {
            if (string.IsNullOrWhiteSpace(details?.Destination))
            {
                return null;
            }

            var target = DownloadDestinations.Resolve(details.Destination, details.ItemType, _libraryManager.GetVirtualFolders(), Plugin.Instance?.DataFolderPath, GetDownloadsRoot());
            return target.Error;
        }

        private static void Apply(DownloadJob job, DownloadDetails? details)
        {
            if (details == null)
            {
                return;
            }

            job.Destination = string.IsNullOrWhiteSpace(details.Destination) ? null : details.Destination.Trim();
            job.ItemType = details.ItemType;
            job.Year = details.Year;
            job.SeriesName = details.SeriesName;
            job.Season = details.Season;
            job.Episode = details.Episode;
        }

        /// <summary>"library:Movies" for the library whose folder holds <paramref name="path"/>, else null (the default).</summary>
        private string? LibraryHolding(string? path)
        {
            var folder = _libraryManager.GetVirtualFolders()?
                .FirstOrDefault(vf => (vf.Locations ?? Array.Empty<string>()).Any(l => IsPathInsideRoot(path, l)));
            return folder == null ? null : DownloadDestinations.LibraryPrefix + folder.Name;
        }

        private DownloadJob NewJob(
            string kind,
            string dedupeKey,
            string itemName,
            string? serverId,
            string? remoteItemId,
            string? federationKey,
            string? localItemId,
            bool bulk)
        {
            return new DownloadJob
            {
                OperationId = Guid.NewGuid().ToString(),
                Kind = kind,
                State = DownloadJob.StateRunning,
                DedupeKey = dedupeKey,
                ItemName = itemName,
                ServerId = serverId,
                RemoteItemId = remoteItemId,
                FederationKey = federationKey,
                LocalItemId = localItemId,
                Bulk = bulk,
                StartedUtc = DateTime.UtcNow
            };
        }

        private void EnqueueAndStart(DownloadJob job, Func<CancellationToken, Task> worker)
        {
            PersistJob(job);
            DownloadProgressTracker.Start(job.OperationId, job.DedupeKey, job.ItemName);
            var cts = new CancellationTokenSource();
            _cancellationSources[job.OperationId] = cts;
            _ = Task.Run(() => worker(cts.Token));
        }

        private bool TryResumeExisting(string dedupeKey, out string? operationId)
        {
            operationId = null;
            var job = _queue.FindByDedupeKey(dedupeKey);
            if (job == null)
            {
                return false;
            }

            if (_cancellationSources.ContainsKey(job.OperationId)
                && !string.Equals(job.State, DownloadJob.StatePaused, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            ResumeJob(job);
            operationId = job.OperationId;
            return true;
        }

        private void ResumeJob(DownloadJob job)
        {
            if (_cancellationSources.ContainsKey(job.OperationId))
            {
                return;
            }

            _pauseRequested.TryRemove(job.OperationId, out _);
            job.State = DownloadJob.StateRunning;
            job.PauseReason = null;
            PersistJob(job);
            DownloadProgressTracker.Restore(ToProgress(job));
            DownloadProgressTracker.Resume(job.OperationId);

            var cts = new CancellationTokenSource();
            _cancellationSources[job.OperationId] = cts;
            _ = Task.Run(() => RunQueuedJobAsync(job, cts.Token));
        }

        private async Task RunQueuedJobAsync(DownloadJob job, CancellationToken cancellationToken)
        {
            _cancellationSources.TryGetValue(job.OperationId, out var ownedCts);
            try
            {
                switch (job.Kind)
                {
                    case DownloadJob.KindBrowse:
                        {
                            var server = _federationManager.GetServer(job.ServerId ?? string.Empty);
                            if (server == null)
                            {
                                FailPermanently(job, "Source server is not configured.");
                                return;
                            }

                            await RunBrowseDownloadAsync(job, server, job.RemoteItemId ?? string.Empty, job.ItemName, job.Bulk, cancellationToken).ConfigureAwait(false);
                            return;
                        }

                    case DownloadJob.KindQualityReplace:
                        {
                            if (!Guid.TryParse(job.LocalItemId, out var oldItemGuid))
                            {
                                FailPermanently(job, "Invalid item id.");
                                return;
                            }

                            var server = _federationManager.GetServer(job.ServerId ?? string.Empty);
                            if (server == null)
                            {
                                FailPermanently(job, "Source server is not configured.");
                                return;
                            }

                            await RunQualityReplaceAsync(job, oldItemGuid, server, job.RemoteItemId ?? string.Empty, job.ItemName, cancellationToken).ConfigureAwait(false);
                            return;
                        }

                    default:
                        {
                            if (!Guid.TryParse(job.LocalItemId, out var itemGuid))
                            {
                                FailPermanently(job, "Invalid item id.");
                                return;
                            }

                            var entry = string.IsNullOrEmpty(job.FederationKey)
                                ? null
                                : _federationManager.Cache.GetEntryByKey(job.FederationKey);
                            var source = entry?.GetSourcesSnapshot()
                                .FirstOrDefault(s => string.Equals(s.ServerId, job.ServerId, StringComparison.OrdinalIgnoreCase))
                                ?? entry?.GetPrimarySource();
                            if (entry == null || source == null)
                            {
                                PauseJob(job, "Paused — the catalog entry is missing; it will retry after the next sync.");
                                return;
                            }

                            await RunDownloadAsync(job, itemGuid, entry, source, cancellationToken).ConfigureAwait(false);
                            return;
                        }
                }
            }
            finally
            {
                ReleaseOwnedCts(job.OperationId, ownedCts);
            }
        }

        private void ReleaseOwnedCts(string operationId, CancellationTokenSource? ownedCts)
        {
            if (ownedCts == null)
            {
                return;
            }

            if (_cancellationSources.TryGetValue(operationId, out var current)
                && ReferenceEquals(current, ownedCts)
                && _cancellationSources.TryRemove(operationId, out var removed))
            {
                removed.Dispose();
                if (_resumeWhenPaused.TryRemove(operationId, out _))
                {
                    var job = _queue.Get(operationId);
                    if (job != null)
                    {
                        ResumeJob(job);
                    }
                }
            }
        }

        private async Task TransferJobAsync(
            DownloadJob job,
            RemoteServer server,
            string remoteId,
            bool bulk,
            string status,
            CancellationToken cancellationToken)
        {
            var partialPath = job.PartialPath ?? throw new InvalidOperationException("Download path was not allocated.");
            var resumeFrom = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
            if (resumeFrom > 0)
            {
                DownloadProgressTracker.UpdateBytes(job.OperationId, resumeFrom, job.TotalBytes, status);
            }
            else
            {
                DownloadProgressTracker.Update(job.OperationId, status);
            }

            void CheckFreeSpace(long? totalBytes, long alreadyOnDisk)
            {
                if (totalBytes is not > 0)
                {
                    return;
                }

                var remaining = totalBytes.Value - alreadyOnDisk;
                var free = FreeSpaceProvider(partialPath);
                if (remaining > 0 && free.HasValue && free.Value < remaining)
                {
                    throw new InsufficientDiskSpaceException($"Not enough disk space: {remaining / 1048576} MB needed, {free.Value / 1048576} MB free.");
                }
            }

            // Resumed jobs already know their size; a fresh one learns it from the first progress
            // report (response headers arrive before any bytes are written), so check there too.
            CheckFreeSpace(job.TotalBytes, resumeFrom);
            var spaceChecked = job.TotalBytes is > 0;

            var progress = new ImmediateProgress<(long BytesRead, long? TotalBytes)>(p =>
            {
                if (!spaceChecked && p.TotalBytes is > 0)
                {
                    spaceChecked = true;
                    CheckFreeSpace(p.TotalBytes, p.BytesRead);
                }

                DownloadProgressTracker.UpdateBytes(job.OperationId, p.BytesRead, p.TotalBytes, status);
                job.BytesDownloaded = p.BytesRead;
                job.TotalBytes = p.TotalBytes;
                PersistJobProgress(job);
            });

            if (_qualityDownloadOverride != null)
            {
                await _qualityDownloadOverride(server, remoteId, partialPath, progress, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (server.Kind == ServerKind.Jellyfin)
            {
                var client = _clientFactory.GetClient(server) ?? _federationManager.GetClient(server.Id);
                if (client == null)
                {
                    throw new InvalidOperationException("Source server is not configured.");
                }

                await client.DownloadToFileAsync(remoteId, partialPath, progress, cancellationToken, bulk, resumeFrom).ConfigureAwait(false);
                return;
            }

            var provider = _externalCatalogs.For(server);
            var url = provider == null
                ? null
                : await provider.ResolveStreamUrlAsync(server, remoteId, cancellationToken).ConfigureAwait(false);
            if (url == null)
            {
                throw new InvalidOperationException("Could not resolve a download URL from the remote server.");
            }

            await DownloadUrlToFileAsync(MarkExternalDownload(url, bulk), partialPath, progress, cancellationToken, resumeFrom).ConfigureAwait(false);
        }

        private bool ShouldHoldForOfflineServer(RemoteServer server, DownloadJob job)
        {
            if (!IsServerOffline(server.Id))
            {
                return false;
            }

            PauseJob(job, "Paused — waiting for " + server.Name + " to come back");
            return true;
        }

        private static bool IsServerOffline(string? serverId)
        {
            return !string.IsNullOrEmpty(serverId)
                && FederationItemPersistenceService.AvailabilityOverride?.IsOffline(serverId) == true;
        }

        private void PersistJob(DownloadJob job)
        {
            job.LastPersistUtc = DateTime.UtcNow;
            _queue.Upsert(job);
        }

        private void PersistJobProgress(DownloadJob job)
        {
            if (DateTime.UtcNow - job.LastPersistUtc < TimeSpan.FromSeconds(1))
            {
                return;
            }

            PersistJob(job);
        }

        private void SucceedJob(DownloadJob job, string message)
        {
            _retryAttempts.TryRemove(job.OperationId, out _);
            _queue.Remove(job.OperationId);
            DownloadProgressTracker.Complete(job.OperationId, true, message);
        }

        private void FailPermanently(DownloadJob job, string message)
        {
            _retryAttempts.TryRemove(job.OperationId, out _);
            _queue.Remove(job.OperationId);
            DownloadProgressTracker.Complete(job.OperationId, false, message);
        }

        private void PauseJob(DownloadJob job, string reason)
        {
            job.State = DownloadJob.StatePaused;
            job.PauseReason = reason;
            if (job.PartialPath != null && File.Exists(job.PartialPath))
            {
                job.BytesDownloaded = new FileInfo(job.PartialPath).Length;
            }

            PersistJob(job);
            DownloadProgressTracker.Restore(ToProgress(job));
            DownloadProgressTracker.Pause(job.OperationId, reason);
        }

        private void HandleTransferFailure(
            DownloadJob job,
            string itemName,
            Exception ex,
            CancellationToken cancellationToken,
            string? partialPath,
            string? cancelledMessage = null)
        {
            if (ex is OperationCanceledException
                && (_pauseRequested.TryRemove(job.OperationId, out _) || !cancellationToken.IsCancellationRequested))
            {
                var reason = cancellationToken.IsCancellationRequested
                    ? "Paused — the source server is unreachable"
                    : "Paused after a transfer interruption — will retry automatically";
                _logger.LogInformation("[Federation] Download paused for {Name}: {Reason}", itemName, reason);
                PauseJob(job, reason);
                if (!cancellationToken.IsCancellationRequested)
                {
                    // An interruption, not a server-offline pause: no server-back event will resume it.
                    ScheduleRetry(job);
                }

                return;
            }

            if (ex is OperationCanceledException)
            {
                _logger.LogInformation("[Federation] Download cancelled for {Name}", itemName);
                FailPermanently(job, cancelledMessage ?? "Cancelled.");
                DeletePartialFile(job, partialPath);
                return;
            }

            if (IsOutOfDiskSpace(ex))
            {
                // Retrying cannot help until the admin frees space, and the partial file is
                // what is filling the disk, so fail clearly and drop it.
                _logger.LogError(ex, "[Federation] Download failed for {Name}: out of disk space", itemName);
                FailPermanently(job, "Not enough disk space to finish this download. Free some space and start it again.");
                DeletePartialFile(job, partialPath);
                return;
            }

            if (ex is DownloadRefusedException)
            {
                _logger.LogWarning("[Federation] Download of {Name} failed: {Reason}", itemName, ex.Message);
                FailPermanently(job, ex.Message);
                DeletePartialFile(job, partialPath);
                return;
            }

            if (IsTransientDownloadFailure(ex))
            {
                _logger.LogWarning(ex, "[Federation] Download paused for {Name} after a transfer error", itemName);
                PauseJob(job, "Paused after a transfer error — will retry automatically. " + ex.Message);
                ScheduleRetry(job);
                return;
            }

            _logger.LogError(ex, "[Federation] Download failed for {Name}", itemName);
            FailPermanently(job, "Download failed: " + ex.Message);
            DeletePartialFile(job, partialPath);
        }

        /// <summary>Thrown before a transfer starts when the target volume cannot hold the file.</summary>
        private sealed class InsufficientDiskSpaceException : IOException
        {
            public InsufficientDiskSpaceException(string message)
                : base(message)
            {
            }
        }

        /// <summary>
        /// True for a disk-full failure (Windows ERROR_DISK_FULL / ERROR_HANDLE_DISK_FULL,
        /// Linux ENOSPC, or the runtime's "No space left" message), possibly wrapped.
        /// </summary>
        internal static bool IsOutOfDiskSpace(Exception? ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (e is InsufficientDiskSpaceException)
                {
                    return true;
                }

                if (e is IOException)
                {
                    var code = e.HResult & 0xFFFF;
                    if (code == 0x70 || code == 0x27 || e.HResult == 28)
                    {
                        return true;
                    }

                    if (e.Message.Contains("No space left", StringComparison.OrdinalIgnoreCase)
                        || e.Message.Contains("not enough space", StringComparison.OrdinalIgnoreCase)
                        || e.Message.Contains("disk is full", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>Test seam: free bytes on the volume holding a path (null when unknown).</summary>
        internal Func<string, long?> FreeSpaceProvider { get; set; } = path =>
        {
            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath(path));
                return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
            }
            catch (Exception)
            {
                return null;
            }
        };

        /// <summary>Test seam: delay before retry attempt <c>n</c> (1-based).</summary>
        internal Func<int, TimeSpan> RetryDelay { get; set; } = attempt => attempt switch
        {
            1 => TimeSpan.FromMinutes(1),
            2 => TimeSpan.FromMinutes(5),
            3 => TimeSpan.FromMinutes(15),
            _ => TimeSpan.FromMinutes(30)
        };

        private const int MaxAutomaticRetries = 6;

        private readonly ConcurrentDictionary<string, int> _retryAttempts = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// A transient transfer failure pauses the job, and only a server-back event or a
        /// restart resumes paused jobs - neither fires when the friend never went offline.
        /// Retry with a backoff, a bounded number of times, so "will retry automatically" is true.
        /// </summary>
        private void ScheduleRetry(DownloadJob job)
        {
            var attempt = _retryAttempts.AddOrUpdate(job.OperationId, 1, (_, n) => n + 1);
            if (attempt > MaxAutomaticRetries)
            {
                _retryAttempts.TryRemove(job.OperationId, out _);
                _logger.LogWarning("[Federation] Download {Name} stayed interrupted after {Count} automatic retries; resume it manually", job.ItemName, MaxAutomaticRetries);
                return;
            }

            var delay = RetryDelay(attempt);
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delay).ConfigureAwait(false);
                    var current = _queue.Get(job.OperationId);
                    if (current != null
                        && string.Equals(current.State, DownloadJob.StatePaused, StringComparison.OrdinalIgnoreCase)
                        && !_cancellationSources.ContainsKey(current.OperationId)
                        && !IsServerOffline(current.ServerId))
                    {
                        ResumeJob(current);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Federation] Automatic download retry failed to start for {Name}", job.ItemName);
                }
            });
        }

        private static bool IsTransientDownloadFailure(Exception ex)
        {
            return ex is HttpRequestException
                or IOException
                or TaskCanceledException
                or TimeoutException;
        }

        private static DownloadProgress ToProgress(DownloadJob job)
        {
            var paused = string.Equals(job.State, DownloadJob.StatePaused, StringComparison.OrdinalIgnoreCase);
            double percent = 0;
            if (job.TotalBytes is > 0)
            {
                percent = Math.Min(100.0, job.BytesDownloaded * 100.0 / job.TotalBytes.Value);
            }

            return new DownloadProgress
            {
                OperationId = job.OperationId,
                LocalItemId = job.DedupeKey,
                ItemName = job.ItemName,
                DestinationPath = job.DestinationPath,
                PercentComplete = percent,
                Status = paused ? (job.PauseReason ?? "Paused") : "Downloading...",
                IsComplete = false,
                Paused = paused,
                Success = false,
                StartTime = job.StartedUtc,
                LastUpdate = DateTime.UtcNow,
                BytesDownloaded = job.BytesDownloaded,
                TotalBytes = job.TotalBytes,
                BytesPerSecond = paused ? 0 : null
            };
        }

        internal static string MarkExternalDownload(string url, bool bulk)
        {
            var separator = url.Contains('?', StringComparison.Ordinal) ? "&" : "?";
            return url + separator + "federationDownload=true&federationBulk=" + (bulk ? "true" : "false");
        }

        private void DeletePartialFile(DownloadJob job, string? path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            // A persisted job's path is not trusted: only ever delete a
            // ".partial" file inside the downloads folder or the job's own
            // destination folder.
            var insideTarget = !string.IsNullOrEmpty(job.TargetRoot) && IsPathInsideRoot(path, job.TargetRoot);
            if (!path.EndsWith(".partial", StringComparison.Ordinal)
                || !(IsPathInsideRoot(path, GetDownloadsRoot()) || insideTarget))
            {
                _logger.LogWarning("[Federation] Not removing partial file outside the downloads folder: {Path}", path);
                return;
            }

            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Federation] Could not remove partially-downloaded file {Path}", path);
            }
        }

        private void OnLibraryItemAdded(object? sender, ItemChangeEventArgs e)
        {
            var item = e.Item;
            if (item == null || string.IsNullOrWhiteSpace(item.Path))
            {
                return;
            }

            if (!TryGetPendingOrigin(item.Path, out var origin) && !IsDownloadedFilePath(item.Path))
            {
                return;
            }

            PersistDownloadedStampAsync(item, origin ?? "downloaded", CancellationToken.None).GetAwaiter().GetResult();
        }

        private async Task StampDownloadedCopyAsync(string destinationPath, string origin)
        {
            RememberPendingDownload(destinationPath, origin);

            // Outside the downloads folder the path alone no longer says "a
            // friend's copy", and the stamp only lands after the scan: remember
            // the file so it is never offered back to friends in between.
            DownloadedFileRegistry.Add(destinationPath);

            BaseItem? item;
            try
            {
                item = _libraryManager.FindByPath(destinationPath, false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[Federation] FindByPath failed while stamping downloaded copy {Path}", destinationPath);
                return;
            }

            if (item != null)
            {
                await PersistDownloadedStampAsync(item, origin, CancellationToken.None).ConfigureAwait(false);
            }
        }

        private async Task PersistDownloadedStampAsync(BaseItem item, string origin, CancellationToken cancellationToken)
        {
            if (!FederationLibraryManager.TryStampDownloadedCopy(item, origin))
            {
                return;
            }

            try
            {
                var parent = item.ParentId != Guid.Empty
                    ? _libraryManager.GetItemById(item.ParentId)
                    : null;
                await _libraryManager.UpdateItemsAsync(
                    new[] { item },
                    parent ?? item,
                    ItemUpdateType.MetadataEdit,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Federation] Could not persist download origin on {Name}", item.Name);
            }

            ForgetPendingDownload(item.Path);
        }

        private void RememberPendingDownload(string path, string origin)
        {
            _pendingDownloadOrigins[NormalizePathKey(path)] = origin;
        }

        private bool TryGetPendingOrigin(string path, out string? origin)
        {
            return _pendingDownloadOrigins.TryGetValue(NormalizePathKey(path), out origin);
        }

        private void ForgetPendingDownload(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            _pendingDownloadOrigins.TryRemove(NormalizePathKey(path), out _);
        }

        private static string NormalizePathKey(string path)
        {
            try
            {
                return Path.GetFullPath(path);
            }
            catch (Exception)
            {
                return path;
            }
        }

        private static bool IsPathInsideRoot(string? path, string? root)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root))
            {
                return false;
            }

            try
            {
                var fullPath = Path.GetFullPath(path);
                var fullRoot = Path.GetFullPath(root);
                if (fullRoot.Length > 0
                    && fullRoot[^1] != Path.DirectorySeparatorChar
                    && fullRoot[^1] != Path.AltDirectorySeparatorChar)
                {
                    fullRoot += Path.DirectorySeparatorChar;
                }

                return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Fixes where a job writes, on its first run: the admin's choice (or
        /// the configured default) resolved to a library folder, laid out the way
        /// Jellyfin names films and episodes. Resumed jobs keep their paths;
        /// jobs from older versions stay in the downloads folder. Returns an
        /// error for the admin, or null.
        /// </summary>
        private string? PrepareTarget(DownloadJob job, string name, string extension, string? sourceLabel)
        {
            var downloadsRoot = GetDownloadsRoot();
            if (!string.IsNullOrEmpty(job.DestinationPath) && !string.IsNullOrEmpty(job.PartialPath))
            {
                job.TargetRoot ??= downloadsRoot;
            }
            else
            {
                var target = DownloadDestinations.Resolve(
                    job.Destination ?? Plugin.Instance?.Configuration?.DownloadDestination,
                    job.ItemType,
                    _libraryManager.GetVirtualFolders(),
                    Plugin.Instance?.DataFolderPath,
                    downloadsRoot);
                if (!target.Ok)
                {
                    return target.Error;
                }

                if (string.IsNullOrEmpty(target.Root))
                {
                    return "Plugin data path unavailable.";
                }

                var (subfolder, fileName) = DownloadDestinations.Layout(
                    new DownloadNaming(name, job.Year, job.ItemType, job.SeriesName, job.Season, job.Episode, extension),
                    target.IsSeparate);
                var directory = string.IsNullOrEmpty(subfolder) ? target.Root : Path.Combine(target.Root, subfolder);
                try
                {
                    Directory.CreateDirectory(directory);
                    var probe = Path.Combine(directory, ".federation-write-test-" + job.OperationId);
                    File.WriteAllBytes(probe, Array.Empty<byte>());
                    File.Delete(probe);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return $"This server can't write to {directory} ({ex.Message}). Pick another destination.";
                }

                var destination = GetUniqueDestinationPath(directory, fileName, sourceLabel);
                job.TargetRoot = target.Root;
                job.TargetLibrary = target.IsSeparate ? null : target.LibraryName;
                job.CreateLibrary = target.CreateLibrary;
                job.CreateType = target.CreateType?.ToString();
                job.DestinationPath = destination;
                job.PartialPath = Path.Combine(directory, "." + Path.GetFileName(destination) + "." + job.OperationId + ".partial");
                PersistJob(job);
            }

            if (!AreJobPathsInsideRoot(job.TargetRoot!, job.DestinationPath, job.PartialPath))
            {
                return "Refusing to write a download outside its destination folder.";
            }

            Directory.CreateDirectory(Path.GetDirectoryName(job.PartialPath!)!);
            DownloadProgressTracker.SetDestinationPath(job.OperationId, job.DestinationPath!);
            return null;
        }

        /// <summary>
        /// Makes sure a library scans the finished file: the separate downloads
        /// library, or the new library made for a new folder. An existing
        /// library already covers its own folders.
        /// </summary>
        private async Task FinishTargetAsync(DownloadJob job)
        {
            if (string.IsNullOrEmpty(job.TargetLibrary))
            {
                await EnsureDownloadsLibraryAsync(job.TargetRoot ?? GetDownloadsRoot()).ConfigureAwait(false);
                return;
            }

            if (string.IsNullOrEmpty(job.CreateLibrary) || string.IsNullOrEmpty(job.TargetRoot))
            {
                return;
            }

            var existing = _libraryManager.GetVirtualFolders()?
                .FirstOrDefault(vf => string.Equals(vf.Name, job.CreateLibrary, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                if (!HasLocation(existing, job.TargetRoot))
                {
                    _libraryManager.AddMediaPath(existing.Name, new MediaPathInfo { Path = job.TargetRoot });
                }

                return;
            }

            var type = Enum.TryParse<CollectionTypeOptions>(job.CreateType, true, out var parsed) ? parsed : CollectionTypeOptions.movies;
            await _libraryManager.AddVirtualFolder(
                job.CreateLibrary,
                type,
                new LibraryOptions { PathInfos = new[] { new MediaPathInfo { Path = job.TargetRoot } } },
                refreshLibrary: false).ConfigureAwait(false);
            _logger.LogInformation("[Federation] Made library {Name} for downloads in {Path}", job.CreateLibrary, job.TargetRoot);
        }

        private async Task EnsureDownloadsLibraryAsync(string downloadsRoot)
        {
            var existing = _libraryManager.GetVirtualFolders()?
                .FirstOrDefault(vf => string.Equals(vf.Name, DownloadsLibraryName, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                // Name match is not enough: older builds created this folder
                // without the downloads path, so files never appeared.
                if (!HasLocation(existing, downloadsRoot))
                {
                    try
                    {
                        _libraryManager.AddMediaPath(existing.Name, new MediaPathInfo { Path = downloadsRoot });
                        _logger.LogInformation("[Federation] Attached downloads path to existing library {Name}", existing.Name);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[Federation] Could not attach downloads path to {Name}", existing.Name);
                    }
                }

                return;
            }

            var libraryOptions = new LibraryOptions
            {
                PathInfos = new[] { new MediaPathInfo { Path = downloadsRoot } }
            };

            await _libraryManager.AddVirtualFolder(DownloadsLibraryName, CollectionTypeOptions.mixed, libraryOptions, refreshLibrary: false)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Returns true when the virtual folder already has <paramref name="path"/> as one
        /// of its media locations (ordinal, case-insensitive).
        /// </summary>
        private static bool HasLocation(VirtualFolderInfo vf, string path)
        {
            if (vf.Locations == null || string.IsNullOrEmpty(path))
            {
                return false;
            }

            foreach (var location in vf.Locations)
            {
                if (string.Equals(location, path, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static string SafeFileName(string name)
        {
            var trimmed = string.IsNullOrWhiteSpace(name) ? "download" : name.Trim();
            var chars = trimmed.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                if (Array.IndexOf(Path.GetInvalidFileNameChars(), chars[i]) >= 0)
                {
                    chars[i] = '_';
                }
            }

            var safe = new string(chars).Trim().TrimEnd('.');
            if (string.IsNullOrEmpty(safe))
            {
                safe = "download";
            }

            // Remote/browser names are display metadata, not trusted paths.
            // Leave ample room for suffixes, extensions, and the staging id on
            // filesystems with a 255-byte component limit.
            return safe.Length <= 120 ? safe : safe.Substring(0, 120).TrimEnd();
        }

        /// <summary>
        /// File extension derived from a remote container name. The container is
        /// friend-supplied metadata, so anything but a short alphanumeric token
        /// (e.g. "../../plugins/x.dll") falls back to <paramref name="fallback"/>.
        /// Jellyfin may report a comma-separated list ("mov,mp4,m4a"); the first
        /// entry is used.
        /// </summary>
        internal static string SafeExtension(string? container, string fallback)
        {
            var candidate = (container ?? string.Empty).Split(',')[0].Trim().TrimStart('.').ToLowerInvariant();
            if (candidate.Length is 0 or > 10)
            {
                return fallback;
            }

            foreach (var c in candidate)
            {
                if (c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9')))
                {
                    return fallback;
                }
            }

            return candidate;
        }

        /// <summary>
        /// Final guard before staging or committing a download: both paths must
        /// resolve inside the downloads root. Covers jobs persisted by older
        /// builds whose destination was built from unsanitized remote metadata.
        /// </summary>
        internal static bool AreJobPathsInsideRoot(string downloadsRoot, string? destinationPath, string? partialPath)
            => IsPathInsideRoot(destinationPath, downloadsRoot) && IsPathInsideRoot(partialPath, downloadsRoot);

        internal static bool ValidateCompletedDownload(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return false;
            }

            var info = new FileInfo(path);
            if (info.Length < 1024)
            {
                return false;
            }

            using var stream = File.OpenRead(path);
            var prefixBytes = new byte[(int)Math.Min(256, info.Length)];
            var read = stream.Read(prefixBytes, 0, prefixBytes.Length);
            var prefix = System.Text.Encoding.UTF8.GetString(prefixBytes, 0, read).TrimStart();
            return !prefix.StartsWith("<html", StringComparison.OrdinalIgnoreCase)
                && !prefix.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase)
                && !prefix.StartsWith("{", StringComparison.Ordinal)
                && !prefix.StartsWith("[", StringComparison.Ordinal);
        }

        private bool IsExactQualityUpgrade(MediaBrowser.Controller.Entities.BaseItem localItem, RemoteServer server, string nativeItemId)
        {
            if (_qualityUpgradeValidatorOverride != null)
            {
                return _qualityUpgradeValidatorOverride(localItem, server, nativeItemId);
            }

            if (!server.Enabled || localItem.ProviderIds == null)
            {
                return false;
            }

            var dedupKeys = Plugin.Instance?.Configuration?.DedupProviderIds
                ?? new List<string> { "imdb", "tmdb", "tvdb" };
            foreach (var entry in _federationManager.Cache.GetAllEntries())
            {
                var sourceMatches = entry.Sources.Any(source =>
                    string.Equals(source.ServerId, server.Id, StringComparison.Ordinal)
                    && (server.Kind == ServerKind.Jellyfin
                        ? string.Equals(source.RemoteItemId.ToString(), nativeItemId, StringComparison.OrdinalIgnoreCase)
                        : string.Equals(entry.Metadata.RemoteNativeId, nativeItemId, StringComparison.Ordinal)));
                if (!sourceMatches || entry.Metadata.ProviderIds == null)
                {
                    continue;
                }

                var sameTitle = dedupKeys.Any(key =>
                    FederationLibraryManager.TryGetProviderId(localItem.ProviderIds, key, out var localValue)
                    && FederationLibraryManager.TryGetProviderId(entry.Metadata.ProviderIds, key, out var remoteValue)
                    && string.Equals(localValue, remoteValue, StringComparison.OrdinalIgnoreCase));
                if (!sameTitle)
                {
                    continue;
                }

                var (localHeight, localBitrate) = FederationQualityAdvisorService.BestVideoStream(localItem.GetMediaStreams());
                var (remoteHeight, remoteBitrate) = FederationQualityAdvisorService.BestVideoStream(entry.Metadata.MediaStreams);
                return FederationQualityAdvisorService.IsUpgrade(localHeight, localBitrate, remoteHeight, remoteBitrate);
            }

            return false;
        }

        /// <summary>
        /// A free file name in <paramref name="directory"/>. When the name is
        /// taken (another version of the same film), the friend's name is added
        /// the way Jellyfin groups versions: "Name (2021) - friend-a.mkv".
        /// </summary>
        internal static string GetUniqueDestinationPath(string directory, string fileName, string? versionLabel = null)
        {
            var candidate = Path.Combine(directory, fileName);
            if (!File.Exists(candidate))
            {
                return candidate;
            }

            var stem = Path.GetFileNameWithoutExtension(fileName);
            var extension = Path.GetExtension(fileName);
            if (!string.IsNullOrWhiteSpace(versionLabel))
            {
                stem += " - " + DownloadDestinations.SafeComponent(versionLabel, "friend");
                candidate = Path.Combine(directory, stem + extension);
                if (!File.Exists(candidate))
                {
                    return candidate;
                }
            }
            for (var suffix = 2; suffix < 10_000; suffix++)
            {
                candidate = Path.Combine(directory, $"{stem} ({suffix}){extension}");
                if (!File.Exists(candidate))
                {
                    return candidate;
                }
            }

            throw new IOException("Could not allocate a unique destination filename.");
        }
    }

    /// <summary>Where a download goes and what it is, for naming it on disk.</summary>
    public sealed record DownloadDetails(string? Destination, string? ItemType, int? Year, string? SeriesName, int? Season, int? Episode);
}
