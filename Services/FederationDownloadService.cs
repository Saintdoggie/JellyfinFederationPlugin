using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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

        private readonly ILibraryManager _libraryManager;
        private readonly FederationLibraryManager _federationManager;
        private readonly ILogger<FederationDownloadService> _logger;
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _cancellationSources = new();

        /// <summary>
        /// Initializes a new instance of the <see cref="FederationDownloadService"/> class.
        /// </summary>
        public FederationDownloadService(
            ILibraryManager libraryManager,
            FederationLibraryManager federationManager,
            ILogger<FederationDownloadService> logger)
        {
            _libraryManager = libraryManager;
            _federationManager = federationManager;
            _logger = logger;
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

            var operationId = Guid.NewGuid().ToString();
            DownloadProgressTracker.Start(operationId, localItemId, entry.Metadata.Name);

            var cts = new CancellationTokenSource();
            _cancellationSources[operationId] = cts;

            _ = Task.Run(() => RunDownloadAsync(operationId, itemGuid, entry, source, cts.Token));

            return (true, "Download started.", operationId);
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
                var client = _federationManager.GetClient(source.ServerId);
                if (client == null)
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

                await client.DownloadToFileAsync(source.RemoteItemId.ToString(), destinationPath, progress, cancellationToken).ConfigureAwait(false);

                EnsureDownloadsLibrary(downloadsRoot);
                _libraryManager.QueueLibraryScan();

                var item = _libraryManager.GetItemById(itemGuid);
                if (item != null)
                {
                    _libraryManager.DeleteItem(item, new DeleteOptions { DeleteFileLocation = false });
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

        private void EnsureDownloadsLibrary(string downloadsRoot)
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

            // Fire-and-forget: this runs inside the same background Task as the
            // download itself, so blocking here doesn't hold up any HTTP request.
            _libraryManager.AddVirtualFolder(DownloadsLibraryName, CollectionTypeOptions.mixed, libraryOptions, refreshLibrary: false)
                .GetAwaiter().GetResult();
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
    }
}
