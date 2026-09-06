using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Providers
{
    /// <summary>
    /// Source-of-truth metadata for federated items. Always returns the Plex or
    /// Jellyfin friend's own data (including custom posters via RemoteImages) so
    /// TMDb/OMDb never get a chance to "identify" the item.
    /// </summary>
    public class FederationMetadataProvider :
        IRemoteMetadataProvider<Movie, MovieInfo>,
        IRemoteMetadataProvider<Series, SeriesInfo>,
        IRemoteMetadataProvider<Season, SeasonInfo>,
        IRemoteMetadataProvider<Episode, EpisodeInfo>,
        IRemoteMetadataProvider<Audio, SongInfo>,
        IHasOrder
    {
        private static readonly HttpClient SharedHttpClient = new HttpClient();

        private readonly ILogger<FederationMetadataProvider> _logger;
        private readonly Services.FederationLibraryManager _federationManager;
        private readonly FederationImageProvider _images;

        public FederationMetadataProvider(
            ILogger<FederationMetadataProvider> logger,
            Services.FederationLibraryManager federationManager,
            FederationImageProvider images)
        {
            _logger = logger;
            _federationManager = federationManager;
            _images = images;
        }

        public string Name => "Federation";

        /// <inheritdoc />
        public int Order => 0;

        public bool Supports(BaseItem item) => _federationManager.IsFederatedItem(item);

        public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(MovieInfo info, CancellationToken cancellationToken)
            => Task.FromResult(Enumerable.Empty<RemoteSearchResult>());

        public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(SeriesInfo info, CancellationToken cancellationToken)
            => Task.FromResult(Enumerable.Empty<RemoteSearchResult>());

        public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(SeasonInfo info, CancellationToken cancellationToken)
            => Task.FromResult(Enumerable.Empty<RemoteSearchResult>());

        public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(EpisodeInfo info, CancellationToken cancellationToken)
            => Task.FromResult(Enumerable.Empty<RemoteSearchResult>());

        public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(SongInfo info, CancellationToken cancellationToken)
            => Task.FromResult(Enumerable.Empty<RemoteSearchResult>());

        public Task<MetadataResult<Movie>> GetMetadata(MovieInfo info, CancellationToken cancellationToken)
            => GetMetadataCommon<Movie>(info.ProviderIds, cancellationToken);

        public Task<MetadataResult<Series>> GetMetadata(SeriesInfo info, CancellationToken cancellationToken)
            => GetMetadataCommon<Series>(info.ProviderIds, cancellationToken);

        public Task<MetadataResult<Season>> GetMetadata(SeasonInfo info, CancellationToken cancellationToken)
            => GetMetadataCommon<Season>(info.ProviderIds, cancellationToken);

        public Task<MetadataResult<Episode>> GetMetadata(EpisodeInfo info, CancellationToken cancellationToken)
            => GetMetadataCommon<Episode>(info.ProviderIds, cancellationToken);

        public Task<MetadataResult<Audio>> GetMetadata(SongInfo info, CancellationToken cancellationToken)
            => GetMetadataCommon<Audio>(info.ProviderIds, cancellationToken);

        public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
            => SharedHttpClient.GetAsync(url, cancellationToken);

        private async Task<MetadataResult<T>> GetMetadataCommon<T>(Dictionary<string, string>? providerIds, CancellationToken cancellationToken) where T : BaseItem, new()
        {
            var result = new MetadataResult<T> { HasMetadata = false };
            if (providerIds == null || !providerIds.TryGetValue("FederationKey", out var key) || string.IsNullOrEmpty(key))
            {
                return result;
            }

            var entry = _federationManager.Cache.GetEntryByKey(key);
            var primary = entry?.GetPrimarySource();
            if (entry == null || primary == null)
            {
                return result;
            }

            try
            {
                var server = _federationManager.GetServer(primary.ServerId);
                var item = new T
                {
                    Name = entry.Metadata.Name ?? string.Empty,
                    OriginalTitle = entry.Metadata.OriginalTitle,
                    Overview = entry.Metadata.Overview,
                    ProductionYear = entry.Metadata.ProductionYear,
                    PremiereDate = entry.Metadata.PremiereDate,
                    CommunityRating = entry.Metadata.CommunityRating,
                    OfficialRating = entry.Metadata.OfficialRating,
                    RunTimeTicks = entry.Metadata.RunTimeTicks,
                    Genres = entry.Metadata.Genres ?? Array.Empty<string>(),
                    Studios = entry.Metadata.Studios ?? Array.Empty<string>(),
                    ProductionLocations = Array.Empty<string>(),
                    LockedFields = Services.FederationLibraryManager.LockedMetadataFields,
                    Tags = Services.FederationLibraryManager.AppendServerTag(entry.Metadata.Tags, server?.Name),
                    ProviderIds = entry.Metadata.ProviderIds != null
                        ? new Dictionary<string, string>(entry.Metadata.ProviderIds, StringComparer.OrdinalIgnoreCase)
                        : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                };

                item.ProviderIds["FederationKey"] = key;
                item.ProviderIds["FederationSource"] = primary.ServerId;
                item.ProviderIds["FederationRemoteId"] = primary.RemoteItemId.ToString();

                if (item is Episode ep)
                {
                    ep.SeriesName = entry.Metadata.SeriesName;
                    ep.IndexNumber = entry.Metadata.IndexNumber;
                    ep.ParentIndexNumber = entry.Metadata.ParentIndexNumber;
                }

                if (item is Season season)
                {
                    season.IndexNumber = entry.Metadata.IndexNumber;
                    season.SeriesName = entry.Metadata.SeriesName;
                }

                if (item is Audio audio)
                {
                    audio.Album = entry.Metadata.Album;
                    audio.AlbumArtists = entry.Metadata.AlbumArtist != null ? new[] { entry.Metadata.AlbumArtist } : Array.Empty<string>();
                    audio.Artists = entry.Metadata.Artists ?? Array.Empty<string>();
                    audio.IndexNumber = entry.Metadata.IndexNumber;
                }

                result.Item = item;
                result.HasMetadata = true;
                result.Provider = Name;
                result.RemoteImages ??= new List<(string, ImageType)>();

                foreach (var person in Services.FederationLibraryManager.ToPersonInfos(entry))
                {
                    result.AddPerson(person);
                }

                var images = await _images.GetImages(item, cancellationToken).ConfigureAwait(false);
                foreach (var image in images)
                {
                    if (!string.IsNullOrEmpty(image.Url))
                    {
                        result.RemoteImages.Add((image.Url, image.Type));
                    }
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Federation] Failed to apply source metadata for {Key}", key);
                return result;
            }
        }
    }
}
