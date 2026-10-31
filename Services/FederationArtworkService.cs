using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;
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
            if (server == null || !server.Enabled
                || provider == null && server.Kind != ServerKind.Jellyfin
                || provider != null && string.IsNullOrWhiteSpace(nativeId))
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

                var mimeType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
                if (!mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                if (response.Content.Headers.ContentLength > MaxPosterBytes)
                {
                    return false;
                }

                // Read at most the cap, so a friend can't stream an endless body into memory.
                using var stream = new MemoryStream();
                using (var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
                {
                    var buffer = new byte[81920];
                    int read;
                    while ((read = await body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        if (stream.Length + read > MaxPosterBytes)
                        {
                            return false;
                        }

                        stream.Write(buffer, 0, read);
                    }
                }

                stream.Position = 0;
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
        /// Width an image is fetched at, or null when the item has no useful
        /// artwork size. Jellyfin cannot load a library item's image lazily:
        /// saving the item fetches it into the local image store at once. Keep
        /// posters sharp on high-DPI/TV layouts without downloading multi-thousand
        /// pixel originals for every movie, series and season during a first sync.
        /// </summary>
        internal static int? MaxWidthFor(string? itemType)
        {
            if (string.Equals(itemType, "Episode", StringComparison.OrdinalIgnoreCase))
            {
                return EpisodeImageMaxWidth;
            }

            return itemType is "Movie" or "Series" or "Season" ? PrimaryImageMaxWidth : null;
        }

        internal const int EpisodeImageMaxWidth = 640;
        internal const int PrimaryImageMaxWidth = 1000;

        /// <summary>Largest poster accepted from a friend's server.</summary>
        internal const int MaxPosterBytes = 20 * 1024 * 1024;

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
