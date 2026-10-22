using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Copies source-controlled artwork into Jellyfin's local image store.
    /// Remote-image providers alone are not enough for programmatically-created
    /// items: Jellyfin does not automatically run an image refresh after
    /// <c>CreateItems</c>.
    /// </summary>
    public class FederationArtworkService
    {
        private static readonly HttpClient ImageHttpClient = new(FederationHttp.Handler()) { Timeout = TimeSpan.FromSeconds(20) };

        internal const string PrimaryImageTagProviderId = "FederationPrimaryImageTag";

        private readonly ILogger<FederationArtworkService> _logger;
        private readonly FederationLibraryManager _federationManager;
        private readonly ExternalCatalogRegistry _externalCatalogs;
        private readonly IProviderManager _providerManager;

        public FederationArtworkService(
            ILogger<FederationArtworkService> logger,
            FederationLibraryManager federationManager,
            ExternalCatalogRegistry externalCatalogs,
            IProviderManager providerManager)
        {
            _logger = logger;
            _federationManager = federationManager;
            _externalCatalogs = externalCatalogs;
            _providerManager = providerManager;
        }

        /// <summary>
        /// Saves the exact current external-source poster when it is missing or
        /// its source tag changed. Returns true when the item's private marker
        /// provider id changed and therefore needs persisting.
        /// </summary>
        public async Task<bool> SyncPrimaryImageAsync(
            BaseItem item,
            FederatedCacheEntry entry,
            CancellationToken cancellationToken)
        {
            // Deduped titles often have a Jellyfin primary and a Plex sibling.
            // The Plex thumb is the one this plugin can copy locally; using only
            // GetPrimarySource() skipped every mixed item whose primary was not
            // Plex, and skipped Plex-only items whose catalog snapshot never
            // stored a thumb tag.
            var source = FindExternalArtworkSource(entry);
            if (source == null)
            {
                return false;
            }

            var server = _federationManager.GetServer(source.ServerId);
            var provider = server == null ? null : _externalCatalogs.For(server);
            var nativeId = entry.GetNativeId(source);
            if (server == null || !server.Enabled || provider != null && string.IsNullOrWhiteSpace(nativeId))
            {
                return false;
            }

            var sourceTag = source.PrimaryImageTag
                ?? (ReferenceEquals(source, entry.GetPrimarySource()) ? entry.Metadata.PrimaryImageTag : null);
            var tag = $"{source.ServerId}:{source.RemoteItemId:N}:{sourceTag ?? $"native:{nativeId}"}";
            var savedTag = item.GetProviderId(PrimaryImageTagProviderId);
            if (item.HasImage(ImageType.Primary, 0)
                && !string.IsNullOrWhiteSpace(sourceTag)
                && string.Equals(savedTag, tag, StringComparison.Ordinal))
            {
                return false;
            }

            try
            {
                using var response = provider != null
                    ? await provider.GetPrimaryImageResponseAsync(server, nativeId!, cancellationToken).ConfigureAwait(false)
                    : await GetJellyfinPosterAsync(source, server.Url, MaxWidthFor(entry.ItemType), cancellationToken).ConfigureAwait(false);
                if (response == null)
                {
                    return false;
                }

                var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                var mimeType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
                // The Plex credential was sent as a server-side request header;
                // only image bytes cross into Jellyfin's local image cache.
                await _providerManager.SaveImage(
                    item,
                    stream,
                    mimeType,
                    ImageType.Primary,
                    null,
                    cancellationToken).ConfigureAwait(false);

                item.SetProviderId(PrimaryImageTagProviderId, tag);
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                _logger.LogWarning(
                    "[Federation] Could not refresh source poster for {ItemName} from {ServerName}",
                    item.Name,
                    server.Name);
                return false;
            }
        }

        /// <summary>
        /// Width an image is fetched at, or null for the friend's original. Episode
        /// images are only ever shown small; a 640 px copy is a fraction of the
        /// original, and a large first sync fetches one per episode. Jellyfin
        /// cannot load a library item's image lazily: saving the item converts
        /// any remote image address into a local file at once.
        /// </summary>
        internal static int? MaxWidthFor(string? itemType)
            => string.Equals(itemType, "Episode", StringComparison.OrdinalIgnoreCase) ? EpisodeImageMaxWidth : null;

        internal const int EpisodeImageMaxWidth = 640;

        internal static string BuildPosterUrl(string serverUrl, Guid remoteItemId, string token, int? maxWidth)
            => $"{serverUrl.TrimEnd('/')}/Plugins/Federation/Peer/Images/{remoteItemId:N}/Primary?token={Uri.EscapeDataString(token)}"
                + (maxWidth.HasValue ? $"&maxWidth={maxWidth.Value}" : string.Empty);

        private async Task<HttpResponseMessage?> GetJellyfinPosterAsync(
            FederatedSource source, string serverUrl, int? maxWidth, CancellationToken cancellationToken)
        {
            var client = _federationManager.GetClient(source.ServerId);
            if (client == null) return null;
            var (token, _) = await client.GetImageTokenAsync(source.RemoteItemId.ToString("N"), cancellationToken).ConfigureAwait(false);
            if (token == null) return null;
            var url = BuildPosterUrl(serverUrl, source.RemoteItemId, token, maxWidth);
            var response = await ImageHttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode) return response;
            response.Dispose();
            return null;
        }

        /// <summary>
        /// First enabled non-Jellyfin source that can identify a native item,
        /// regardless of which source is currently Primary.
        /// </summary>
        internal FederatedSource? FindExternalArtworkSource(FederatedCacheEntry entry)
        {
            foreach (var source in entry.GetSourcesSnapshot())
            {
                var server = _federationManager.GetServer(source.ServerId);
                if (server == null || !server.Enabled
                    || FederationItemPersistenceService.AvailabilityOverride?.IsOffline(source.ServerId) == true)
                {
                    continue;
                }

                if (_externalCatalogs.For(server) != null
                    ? !string.IsNullOrWhiteSpace(entry.GetNativeId(source))
                    : !string.IsNullOrWhiteSpace(source.PrimaryImageTag))
                {
                    return source;
                }
            }

            return null;
        }
    }
}
