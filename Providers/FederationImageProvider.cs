using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Providers
{
    /// <summary>
    /// Provides images for federated content by building direct URLs to the
    /// primary remote source's image endpoint.
    /// </summary>
    public class FederationImageProvider : IRemoteImageProvider, IHasOrder
    {
        // Shared for the app lifetime; image responses are disposed by the caller.
        private static readonly HttpClient SharedHttpClient = new HttpClient();

        private readonly ILogger<FederationImageProvider> _logger;
        private readonly Services.FederationLibraryManager _federationManager;
        private readonly Services.ExternalCatalogRegistry _externalCatalogs;
        private readonly IHttpContextAccessor? _httpContextAccessor;
        private readonly IAuthorizationContext? _authorizationContext;

        /// <summary>
        /// Initializes a new instance of the <see cref="FederationImageProvider"/> class.
        /// </summary>
        public FederationImageProvider(
            ILogger<FederationImageProvider> logger,
            Services.FederationLibraryManager federationManager,
            Services.ExternalCatalogRegistry externalCatalogs,
            IHttpContextAccessor? httpContextAccessor = null,
            IAuthorizationContext? authorizationContext = null)
        {
            _logger = logger;
            _federationManager = federationManager;
            _externalCatalogs = externalCatalogs;
            _httpContextAccessor = httpContextAccessor;
            _authorizationContext = authorizationContext;
        }

        /// <summary>
        /// Resolves the local user whose request triggered this image lookup, when
        /// one is available. Same source as
        /// <see cref="Services.FederationMediaSourceProvider"/> so per-remote-user
        /// rules are applied at image-token mint time instead of minting a
        /// userless token that skips those rules.
        /// </summary>
        private async Task<Guid?> ResolveLocalUserId()
        {
            try
            {
                var context = _httpContextAccessor?.HttpContext;
                if (context == null || _authorizationContext == null)
                {
                    return null;
                }

                var info = await _authorizationContext.GetAuthorizationInfo(context).ConfigureAwait(false);
                return info != null && info.UserId != Guid.Empty ? info.UserId : null;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[Federation] Could not resolve the local acting user for an image-token mint");
                return null;
            }
        }

        /// <inheritdoc />
        public string Name => "Federation";

        /// <inheritdoc />
        public int Order => 0;

        /// <inheritdoc />
        public bool Supports(BaseItem item) => _federationManager.IsFederatedItem(item);

        /// <inheritdoc />
        public IEnumerable<ImageType> GetSupportedImages(BaseItem item)
        {
            return new[]
            {
                ImageType.Primary,
                ImageType.Backdrop,
                ImageType.Banner,
                ImageType.Thumb,
                ImageType.Logo,
                ImageType.Art
            };
        }

        /// <inheritdoc />
        public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
        {
            if (!_federationManager.IsFederatedItem(item))
            {
                return Enumerable.Empty<RemoteImageInfo>();
            }

            try
            {
                var key = Services.FederationLibraryManager.GetFederationKey(item);
                var entry = key == null ? null : _federationManager.Cache.GetEntryByKey(key);
                var primary = entry?.GetPrimarySource();
                if (entry == null || primary == null)
                {
                    return Enumerable.Empty<RemoteImageInfo>();
                }

                var server = _federationManager.GetServer(primary.ServerId);
                if (server == null)
                {
                    return Enumerable.Empty<RemoteImageInfo>();
                }

                // A non-Jellyfin source (Plex today) has no "Peer" gateway of its
                // own to hotlink through - it's a different product entirely -
                // so its images are resolved directly against its own native API
                // via the external-provider abstraction instead.
                var externalProvider = _externalCatalogs.For(server);
                if (externalProvider != null)
                {
                    var nativeId = entry.Metadata.RemoteNativeId;
                    if (nativeId == null)
                    {
                        return Enumerable.Empty<RemoteImageInfo>();
                    }

                    var externalImages = await externalProvider.GetImagesAsync(server, nativeId, cancellationToken).ConfigureAwait(false);
                    if (externalImages == null)
                    {
                        return Enumerable.Empty<RemoteImageInfo>();
                    }

                    var externalResult = new List<RemoteImageInfo>();
                    if (externalImages.PrimaryUrl != null)
                    {
                        externalResult.Add(new RemoteImageInfo { Url = externalImages.PrimaryUrl, Type = ImageType.Primary, ProviderName = Name });
                    }

                    if (externalImages.BackdropUrl != null)
                    {
                        externalResult.Add(new RemoteImageInfo { Url = externalImages.BackdropUrl, Type = ImageType.Backdrop, ProviderName = Name });
                    }

                    return externalResult;
                }

                var client = _federationManager.GetClient(primary.ServerId);
                if (client == null)
                {
                    return Enumerable.Empty<RemoteImageInfo>();
                }

                var actingUserId = await ResolveLocalUserId().ConfigureAwait(false);
                var actingUser = actingUserId?.ToString("N");
                var remoteItem = await client.GetItemAsync(
                    primary.RemoteItemId.ToString(),
                    cancellationToken: cancellationToken,
                    localActingUserId: actingUser).ConfigureAwait(false);
                if (remoteItem == null)
                {
                    return Enumerable.Empty<RemoteImageInfo>();
                }

                // Images used to hotlink straight to the remote's native
                // /Items/{id}/Images/{type} endpoint, optionally with server.ApiKey
                // appended as a raw api_key (RemoteServer.RequireApiKeyForImages).
                // Under the federation-token model that key is no longer a real
                // Jellyfin credential - it would just 401 for anyone with that
                // option on, and was an unauthenticated hotlink to the friend's
                // own native API for anyone without it. Every image now goes
                // through the token-gated Peer/Images gateway with an
                // Image-purpose token DirectStream will not honor for video/audio:
                // mint one short-lived, single-item-scoped token and reuse it
                // across every image URL for this item, rather than minting one
                // per image. The acting user is forwarded so per-remote-user
                // visibility rules apply at mint time.
                var itemId = primary.RemoteItemId.ToString();
                var (imageToken, _) = await client.GetImageTokenAsync(itemId, cancellationToken, localActingUserId: actingUser).ConfigureAwait(false);
                if (imageToken == null)
                {
                    _logger.LogWarning("[Federation] Could not obtain an image token from {ServerName} for {Name}; no images will be shown", server.Name, item.Name);
                    return Enumerable.Empty<RemoteImageInfo>();
                }

                var images = new List<RemoteImageInfo>();
                var baseUrl = server.Url.TrimEnd('/');
                var tokenParam = $"token={Uri.EscapeDataString(imageToken)}";

                if (remoteItem.ImageTags?.ContainsKey(ImageType.Primary) == true)
                {
                    images.Add(new RemoteImageInfo
                    {
                        Url = $"{baseUrl}/Plugins/Federation/Peer/Images/{itemId}/{ImageType.Primary}?{tokenParam}&tag={remoteItem.ImageTags[ImageType.Primary]}",
                        Type = ImageType.Primary,
                        ProviderName = Name
                    });
                }

                if (remoteItem.BackdropImageTags != null)
                {
                    for (int i = 0; i < remoteItem.BackdropImageTags.Length; i++)
                    {
                        images.Add(new RemoteImageInfo
                        {
                            Url = $"{baseUrl}/Plugins/Federation/Peer/Images/{itemId}/Backdrop/{i}?{tokenParam}&tag={remoteItem.BackdropImageTags[i]}",
                            Type = ImageType.Backdrop,
                            ProviderName = Name
                        });
                    }
                }

                foreach (var imageType in new[] { ImageType.Banner, ImageType.Thumb, ImageType.Logo, ImageType.Art })
                {
                    if (remoteItem.ImageTags?.ContainsKey(imageType) == true)
                    {
                        images.Add(new RemoteImageInfo
                        {
                            Url = $"{baseUrl}/Plugins/Federation/Peer/Images/{itemId}/{imageType}?{tokenParam}&tag={remoteItem.ImageTags[imageType]}",
                            Type = imageType,
                            ProviderName = Name
                        });
                    }
                }

                return images;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Federation] Error getting images for {Name}", item.Name);
                return Enumerable.Empty<RemoteImageInfo>();
            }
        }

        /// <inheritdoc />
        public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        {
            return SharedHttpClient.GetAsync(url, cancellationToken);
        }
    }
}
