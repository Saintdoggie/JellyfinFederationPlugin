using System;
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
            if (server == null || !server.Enabled || provider == null || string.IsNullOrWhiteSpace(nativeId))
            {
                return false;
            }

            var tag = source.PrimaryImageTag
                ?? entry.Metadata.PrimaryImageTag
                ?? $"native:{nativeId}";
            var savedTag = item.GetProviderId(PrimaryImageTagProviderId);
            if (item.HasImage(ImageType.Primary, 0)
                && string.Equals(savedTag, tag, StringComparison.Ordinal))
            {
                return false;
            }

            try
            {
                using var response = await provider.GetPrimaryImageResponseAsync(server, nativeId, cancellationToken).ConfigureAwait(false);
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
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "[Federation] Could not refresh source poster for {ItemName} from {ServerName}",
                    item.Name,
                    server.Name);
                return false;
            }
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
                if (server == null || !server.Enabled || _externalCatalogs.For(server) == null)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(entry.GetNativeId(source)))
                {
                    return source;
                }
            }

            return null;
        }
    }
}
