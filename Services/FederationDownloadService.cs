using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;
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
        private static readonly HttpClient BrowseDownloadHttpClient = new HttpClient { Timeout = TimeSpan.FromHours(6) };

        private readonly ILibraryManager _libraryManager;
        private readonly FederationLibraryManager _federationManager;
        private readonly IRemoteServerClientFactory _clientFactory;
        private readonly ExternalCatalogRegistry _externalCatalogs;
        private readonly ILogger<FederationDownloadService> _logger;
        private readonly Func<RemoteServer, string, string, IProgress<(long BytesRead, long? TotalBytes)>, CancellationToken, Task>? _qualityDownloadOverride;
        private readonly Func<MediaBrowser.Controller.Entities.BaseItem, RemoteServer, string, bool>? _qualityUpgradeValidatorOverride;
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _cancellationSources = new();

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
            Func<MediaBrowser.Controller.Entities.BaseItem, RemoteServer, string, bool>? qualityUpgradeValidatorOverride)
        {
            _libraryManager = libraryManager;
            _federationManager = federationManager;
            _clientFactory = clientFactory;
            _externalCatalogs = externalCatalogs;
            _logger = logger;
            _qualityDownloadOverride = qualityDownloadOverride;
            _qualityUpgradeValidatorOverride = qualityUpgradeValidatorOverride;
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
        /// Validates the request and starts a background download. Returns immediately
        /// with an operation id to poll via <see cref="DownloadProgressTracker.Get"/> -
        /// a whole movie can take far longer than a single HTTP request should be held
        /// open for.
        /// </summary>
        public (bool Success, string Message, string? OperationId) StartDownload(string localItemId)
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
                return (false, "Already downloading.", null);
            }

            var entry = _federationManager.Cache.GetEntryByKey(key);
            var source = entry?.GetPrimarySource();
            if (entry == null || source == null)
            {
                return (false, "Could not find this item's source server.", null);
            }

            var srcServer = cfg?.RemoteServers?.FirstOrDefault(s => s.Id == source.ServerId);
            if (srcServer != null && !srcServer.AllowDownloads)
            {
                return (false, $"Downloads from {srcServer.Name} are disabled (Catalog → {srcServer.Name} → Download access).", null);
            }

            var operationId = Guid.NewGuid().ToString();
            DownloadProgressTracker.Start(operationId, localItemId, entry.Metadata.Name);

            var cts = new CancellationTokenSource();
            _cancellationSources[operationId] = cts;

            _ = Task.Run(() => RunDownloadAsync(operationId, itemGuid, entry, source, cts.Token));

            return (true, "Download started.", operationId);
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
        public (bool Success, string Message, string? OperationId) StartBrowseDownload(string serverId, string nativeItemId, string itemName)
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

            if (!server.AllowDownloads)
            {
                return (false, $"Downloads from {server.Name} are disabled (Catalog → {server.Name} → Download access).", null);
            }

            // Reuses the same tracker as StartDownload's per-item dedupe, keyed on
            // a browse-specific string rather than a local item Guid since there
            // is no local item yet - LocalItemId is just an opaque dedupe/display
            // key to DownloadProgressTracker either way.
            var dedupeKey = $"browse:{serverId}:{nativeItemId}";
            if (DownloadProgressTracker.IsDownloadingItem(dedupeKey))
            {
                return (false, "Already downloading.", null);
            }

            var operationId = Guid.NewGuid().ToString();
            DownloadProgressTracker.Start(operationId, dedupeKey, itemName);

            var cts = new CancellationTokenSource();
            _cancellationSources[operationId] = cts;

            _ = Task.Run(() => RunBrowseDownloadAsync(operationId, server, nativeItemId, itemName, cts.Token));

            return (true, "Download started.", operationId);
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
                return (false, "Already downloading.", null);
            }

            var operationId = Guid.NewGuid().ToString();
            DownloadProgressTracker.Start(operationId, dedupeKey, item.Name);

            var cts = new CancellationTokenSource();
            _cancellationSources[operationId] = cts;

            _ = Task.Run(() => RunQualityReplaceAsync(operationId, itemGuid, server, nativeItemId, item.Name, cts.Token));

            return (true, "Download started.", operationId);
        }

        /// <summary>
        /// Resolves a browser-downloadable URL for a federated item: the same
        /// proxy stream URL playback already uses (<see cref="FederationLibraryManager.BuildStaticPath"/>),
        /// with <c>download=true</c> and a filesystem-safe filename appended so
        /// <c>FederationController.Stream</c> sends a <c>Content-Disposition</c>
        /// header and the browser saves it to the viewer's own device instead of
        /// playing it inline. Distinct from <see cref="StartDownload"/> above,
        /// which downloads a permanent copy onto *this server's* disk instead -
        /// this never touches server storage at all, it just resolves a URL that
        /// streams straight to whoever asked. Shares this method's item/source
        /// resolution (and its failure messages) with StartDownload rather than
        /// duplicating it.
        /// </summary>
        public (bool Success, string Message, string? Url, string? FileName) GetDownloadUrl(string localItemId)
        {
            if (!Guid.TryParse(localItemId, out var itemGuid))
            {
                return (false, "Invalid item id.", null, null);
            }

            var item = _libraryManager.GetItemById(itemGuid);
            if (item == null)
            {
                return (false, "Item not found.", null, null);
            }

            var key = FederationLibraryManager.GetFederationKey(item);
            if (key == null)
            {
                return (false, "This item isn't streamed from a friend's server.", null, null);
            }

            var entry = _federationManager.Cache.GetEntryByKey(key);
            var source = entry?.GetPrimarySource();
            if (entry == null || source == null)
            {
                return (false, "Could not find this item's source server.", null, null);
            }

            var url = _federationManager.BuildStaticPath(entry.ItemType, source);
            if (url == null)
            {
                return (false, "This source is not currently available for download.", null, null);
            }

            var extension = string.IsNullOrWhiteSpace(entry.Metadata.Container) ? "mp4" : entry.Metadata.Container.Trim().TrimStart('.');
            var fileName = SafeFileName(entry.Metadata.Name) + "." + extension;
            var separator = url.Contains('?', StringComparison.Ordinal) ? "&" : "?";
            var downloadUrl = $"{url}{separator}download=true&fileName={Uri.EscapeDataString(fileName)}";

            return (true, "OK", downloadUrl, fileName);
        }

        /// <summary>
        /// Admin-triggered: cancels an in-progress download. No-ops (successfully)
        /// if the operation already finished or was never known - cancelling
        /// something that's already done isn't an error from the caller's side.
        /// </summary>
        public (bool Success, string Message) CancelDownload(string operationId)
        {
            if (_cancellationSources.TryGetValue(operationId, out var cts))
            {
                cts.Cancel();
                return (true, "Cancelling...");
            }

            var progress = DownloadProgressTracker.Get(operationId);
            if (progress == null)
            {
                return (false, "Download not found.");
            }

            return (true, "Already finished.");
        }

        private async Task RunDownloadAsync(string operationId, Guid itemGuid, FederatedCacheEntry entry, FederatedSource source, CancellationToken cancellationToken)
        {
            string? destinationPath = null;
            try
            {
                var srcServer = _federationManager.GetServer(source.ServerId);
                if (srcServer == null)
                {
                    DownloadProgressTracker.Complete(operationId, false, "Source server is not configured.");
                    return;
                }

                var downloadsRoot = GetDownloadsRoot();
                if (string.IsNullOrEmpty(downloadsRoot))
                {
                    DownloadProgressTracker.Complete(operationId, false, "Plugin data path unavailable.");
                    return;
                }

                Directory.CreateDirectory(downloadsRoot);

                var extension = string.IsNullOrWhiteSpace(entry.Metadata.Container) ? "mkv" : entry.Metadata.Container.Trim('.');
                var fileName = SafeFileName(entry.Metadata.Name) + "." + extension;
                destinationPath = Path.Combine(downloadsRoot, fileName);
                DownloadProgressTracker.SetDestinationPath(operationId, destinationPath);

                DownloadProgressTracker.Update(operationId, "Downloading...");
                var progress = new Progress<(long BytesRead, long? TotalBytes)>(
                    p => DownloadProgressTracker.UpdateBytes(operationId, p.BytesRead, p.TotalBytes, "Downloading..."));

                if (srcServer.Kind == ServerKind.Jellyfin)
                {
                    var client = _federationManager.GetClient(source.ServerId);
                    if (client == null)
                    {
                        DownloadProgressTracker.Complete(operationId, false, "Source server is not configured.");
                        return;
                    }

                    await client.DownloadToFileAsync(source.RemoteItemId.ToString(), destinationPath, progress, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    // Non-Jellyfin sources (Plex today) have no
                    // Plugins/Federation/DirectStream route to hit - source.RemoteItemId
                    // is only the deterministically-derived local Guid, not something
                    // the remote itself understands. The real rating key/native id the
                    // remote needs is what sync recorded on the entry at materialize
                    // time (see FederatedItemMetadata.RemoteNativeId), same lookup
                    // FederationStreamHandler's own external-source path already relies
                    // on for playback - a null here means a stale entry from before that
                    // field was recorded, fixed by a library refresh.
                    if (string.IsNullOrEmpty(entry.Metadata.RemoteNativeId))
                    {
                        DownloadProgressTracker.Complete(operationId, false, "Could not resolve this item's id on the remote server - try refreshing the library.");
                        return;
                    }

                    var provider = _externalCatalogs.For(srcServer);
                    var url = provider == null
                        ? null
                        : await provider.ResolveStreamUrlAsync(srcServer, entry.Metadata.RemoteNativeId, cancellationToken).ConfigureAwait(false);
                    if (url == null)
                    {
                        DownloadProgressTracker.Complete(operationId, false, "Could not resolve a download URL from the remote server.");
                        return;
                    }

                    await DownloadUrlToFileAsync(url, destinationPath, progress, cancellationToken).ConfigureAwait(false);
                }

                await EnsureDownloadsLibraryAsync(downloadsRoot).ConfigureAwait(false);
                _libraryManager.QueueLibraryScan();

                var item = _libraryManager.GetItemById(itemGuid);
                if (item != null)
                {
                    _libraryManager.DeleteItem(item, new DeleteOptions { DeleteFileLocation = false });
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

                _logger.LogInformation("[Federation] Downloaded {Name} to {Path}", entry.Metadata.Name, destinationPath);
                DownloadProgressTracker.Complete(operationId, true, "Downloaded. It will appear as a local item after the next library scan.");
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("[Federation] Download cancelled for {Name}", entry.Metadata.Name);
                DownloadProgressTracker.Complete(operationId, false, "Cancelled.");
                DeletePartialFile(destinationPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Federation] Download failed for {Name}", entry.Metadata.Name);
                DownloadProgressTracker.Complete(operationId, false, "Download failed: " + ex.Message);
                DeletePartialFile(destinationPath);
            }
            finally
            {
                if (_cancellationSources.TryRemove(operationId, out var cts))
                {
                    cts.Dispose();
                }
            }
        }

        private async Task RunBrowseDownloadAsync(string operationId, RemoteServer server, string nativeItemId, string itemName, CancellationToken cancellationToken)
        {
            string? destinationPath = null;
            try
            {
                var downloadsRoot = GetDownloadsRoot();
                if (string.IsNullOrEmpty(downloadsRoot))
                {
                    DownloadProgressTracker.Complete(operationId, false, "Plugin data path unavailable.");
                    return;
                }

                Directory.CreateDirectory(downloadsRoot);

                // The remote's real container isn't known up front the way
                // RunDownloadAsync's federated-entry path knows it from synced
                // metadata - mkv is a safe default container extension for
                // whatever bytes come back; Jellyfin's own library scan probes
                // the actual codecs regardless of the extension.
                var fileName = SafeFileName(itemName) + ".mkv";
                destinationPath = Path.Combine(downloadsRoot, fileName);
                DownloadProgressTracker.SetDestinationPath(operationId, destinationPath);

                DownloadProgressTracker.Update(operationId, "Downloading...");
                var progress = new Progress<(long BytesRead, long? TotalBytes)>(
                    p => DownloadProgressTracker.UpdateBytes(operationId, p.BytesRead, p.TotalBytes, "Downloading..."));

                if (server.Kind == ServerKind.Jellyfin)
                {
                    var client = _clientFactory.GetClient(server);
                    await client.DownloadToFileAsync(nativeItemId, destinationPath, progress, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    var provider = _externalCatalogs.For(server);
                    var url = provider == null
                        ? null
                        : await provider.ResolveStreamUrlAsync(server, nativeItemId, cancellationToken).ConfigureAwait(false);
                    if (url == null)
                    {
                        DownloadProgressTracker.Complete(operationId, false, "Could not resolve a download URL from the remote server.");
                        return;
                    }

                    await DownloadUrlToFileAsync(url, destinationPath, progress, cancellationToken).ConfigureAwait(false);
                }

                await EnsureDownloadsLibraryAsync(downloadsRoot).ConfigureAwait(false);
                _libraryManager.QueueLibraryScan();

                _logger.LogInformation("[Federation] Browse-downloaded {Name} from {Server} to {Path}", itemName, server.Name, destinationPath);
                DownloadProgressTracker.Complete(operationId, true, "Downloaded. It will appear as a local item after the next library scan.");
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("[Federation] Browse download cancelled for {Name}", itemName);
                DownloadProgressTracker.Complete(operationId, false, "Cancelled.");
                DeletePartialFile(destinationPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Federation] Browse download failed for {Name}", itemName);
                DownloadProgressTracker.Complete(operationId, false, "Download failed: " + ex.Message);
                DeletePartialFile(destinationPath);
            }
            finally
            {
                if (_cancellationSources.TryRemove(operationId, out var cts))
                {
                    cts.Dispose();
                }
            }
        }

        private async Task RunQualityReplaceAsync(string operationId, Guid oldItemGuid, RemoteServer server, string nativeItemId, string itemName, CancellationToken cancellationToken)
        {
            string? partialPath = null;
            string? committedPath = null;
            try
            {
                var downloadsRoot = GetDownloadsRoot();
                if (string.IsNullOrEmpty(downloadsRoot))
                {
                    DownloadProgressTracker.Complete(operationId, false, "Plugin data path unavailable.");
                    return;
                }

                Directory.CreateDirectory(downloadsRoot);

                var fileName = SafeFileName(itemName) + ".mkv";
                committedPath = GetUniqueDestinationPath(downloadsRoot, fileName);
                partialPath = Path.Combine(downloadsRoot, "." + Path.GetFileName(committedPath) + "." + operationId + ".partial");
                DownloadProgressTracker.SetDestinationPath(operationId, committedPath);

                DownloadProgressTracker.Update(operationId, "Downloading higher-quality copy...");
                var progress = new Progress<(long BytesRead, long? TotalBytes)>(
                    p => DownloadProgressTracker.UpdateBytes(operationId, p.BytesRead, p.TotalBytes, "Downloading higher-quality copy..."));

                if (_qualityDownloadOverride != null)
                {
                    await _qualityDownloadOverride(server, nativeItemId, partialPath, progress, cancellationToken).ConfigureAwait(false);
                }
                else if (server.Kind == ServerKind.Jellyfin)
                {
                    var client = _clientFactory.GetClient(server);
                    await client.DownloadToFileAsync(nativeItemId, partialPath, progress, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    var provider = _externalCatalogs.For(server);
                    var url = provider == null
                        ? null
                        : await provider.ResolveStreamUrlAsync(server, nativeItemId, cancellationToken).ConfigureAwait(false);
                    if (url == null)
                    {
                        DownloadProgressTracker.Complete(operationId, false, "Could not resolve a download URL from the remote server.");
                        return;
                    }

                    await DownloadUrlToFileAsync(url, partialPath, progress, cancellationToken).ConfigureAwait(false);
                }

                if (!ValidateCompletedDownload(partialPath))
                {
                    throw new InvalidDataException("The downloaded replacement was empty, truncated, or did not look like media.");
                }

                // Same-directory move is atomic on supported filesystems: the
                // managed library never observes a partially-written movie.
                File.Move(partialPath, committedPath);
                partialPath = null;

                // Make the destination library ready before touching the old item.
                // A failure here leaves both the committed new file and old copy.
                await EnsureDownloadsLibraryAsync(downloadsRoot).ConfigureAwait(false);

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
                DownloadProgressTracker.Complete(operationId, true, "Downloaded a higher-quality copy and removed the old one. It will appear as a local item after the next library scan.");
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("[Federation] Quality-replace cancelled for {Name}", itemName);
                DownloadProgressTracker.Complete(operationId, false, "Cancelled. The old copy was not touched.");
                DeletePartialFile(partialPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Federation] Quality-replace failed for {Name}", itemName);
                var preservation = committedPath != null && File.Exists(committedPath)
                    ? " The downloaded copy was kept. Verify the old copy before retrying."
                    : " The old copy was not touched.";
                DownloadProgressTracker.Complete(operationId, false, "Replacement failed: " + ex.Message + preservation);
                DeletePartialFile(partialPath);
            }
            finally
            {
                if (_cancellationSources.TryRemove(operationId, out var cts))
                {
                    cts.Dispose();
                }
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
        private static async Task DownloadUrlToFileAsync(string url, string destinationPath, IProgress<(long BytesRead, long? TotalBytes)> progress, CancellationToken cancellationToken)
        {
            using var response = await BrowseDownloadHttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength;
            await using var remoteStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

            var buffer = new byte[81920];
            long totalRead = 0;
            int read;
            while ((read = await remoteStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                totalRead += read;
                progress.Report((totalRead, totalBytes));
            }

            progress.Report((totalRead, totalBytes));
        }

        private void DeletePartialFile(string? path)
        {
            if (string.IsNullOrEmpty(path))
            {
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

        private async Task EnsureDownloadsLibraryAsync(string downloadsRoot)
        {
            var existing = _libraryManager.GetVirtualFolders()?
                .FirstOrDefault(vf => string.Equals(vf.Name, DownloadsLibraryName, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                return;
            }

            var libraryOptions = new LibraryOptions
            {
                PathInfos = new[] { new MediaPathInfo { Path = downloadsRoot } }
            };

            await _libraryManager.AddVirtualFolder(DownloadsLibraryName, CollectionTypeOptions.mixed, libraryOptions, refreshLibrary: false)
                .ConfigureAwait(false);
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

            return new string(chars);
        }

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

        private static string GetUniqueDestinationPath(string directory, string fileName)
        {
            var candidate = Path.Combine(directory, fileName);
            if (!File.Exists(candidate))
            {
                return candidate;
            }

            var stem = Path.GetFileNameWithoutExtension(fileName);
            var extension = Path.GetExtension(fileName);
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
}
