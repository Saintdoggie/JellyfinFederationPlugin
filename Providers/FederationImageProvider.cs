using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Providers
{
    /// <summary>
    /// Provides images for federated content by building direct URLs to the
    /// primary remote source's image endpoint.
    /// </summary>
    public class FederationImageProvider : IRemoteImageProvider
    {
        private readonly ILogger<FederationImageProvider> _logger;
        private readonly Services.FederationLibraryManager _federationManager;
        private readonly HttpClient _httpClient;

        /// <summary>
        /// Initializes a new instance of the <see cref="FederationImageProvider"/> class.
        /// </summary>
        public FederationImageProvider(
            ILogger<FederationImageProvider> logger,
            Services.FederationLibraryManager federationManager)
        {
            _logger = logger;
            _federationManager = federationManager;
            _httpClient = new HttpClient();
        }

        /// <inheritdoc />
        public string Name => "Federation";

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
                var entry = _federationManager.Cache.GetEntry(item.Path);
                var primary = entry?.PrimarySource;
                if (primary == null)
                {
                    return Enumerable.Empty<RemoteImageInfo>();
                }

                var server = _federationManager.GetServer(primary.ServerId);
                var client = _federationManager.GetClient(primary.ServerId);
                if (server == null || client == null)
                {
                    return Enumerable.Empty<RemoteImageInfo>();
                }

                var remoteItem = await client.GetItemAsync(primary.RemoteItemId.ToString(), cancellationToken: cancellationToken).ConfigureAwait(false);
                if (remoteItem == null)
                {
                    return Enumerable.Empty<RemoteImageInfo>();
                }

                var images = new List<RemoteImageInfo>();
                var baseUrl = server.Url.TrimEnd('/');
                var itemId = primary.RemoteItemId.ToString();
                var apiKeySuffix = server.RequireApiKeyForImages ? $"&api_key={Uri.EscapeDataString(server.ApiKey)}" : string.Empty;

                if (remoteItem.ImageTags?.ContainsKey(ImageType.Primary) == true)
                {
                    images.Add(new RemoteImageInfo
                    {
                        Url = $"{baseUrl}/Items/{itemId}/Images/{ImageType.Primary}?tag={remoteItem.ImageTags[ImageType.Primary]}{apiKeySuffix}",
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
                            Url = $"{baseUrl}/Items/{itemId}/Images/Backdrop/{i}?tag={remoteItem.BackdropImageTags[i]}{apiKeySuffix}",
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
                            Url = $"{baseUrl}/Items/{itemId}/Images/{imageType}?tag={remoteItem.ImageTags[imageType]}{apiKeySuffix}",
                            Type = imageType,
                            ProviderName = Name
                        });
                    }
                }

                return images;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Federation] Error getting images for {Path}", item.Path);
                return Enumerable.Empty<RemoteImageInfo>();
            }
        }

        /// <inheritdoc />
        public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        {
            return _httpClient.GetAsync(url, cancellationToken);
        }
    }
}
