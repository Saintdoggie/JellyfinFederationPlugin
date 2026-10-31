using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Providers
{
    /// <summary>
    /// Posters and backdrops for the owner's own libraries, taken from their Plex
    /// server. Image URLs use a private <c>plexmeta://</c> scheme resolved in
    /// <see cref="GetImageResponse"/>, so the Plex token never appears in a URL.
    /// </summary>
    public class PlexLocalImageProvider : IRemoteImageProvider, IHasOrder
    {
        internal const string ProviderName = "Plex";
        private const string Scheme = "plexmeta://";

        private readonly PlexMetadataSourceService _plex;
        private readonly ILogger<PlexLocalImageProvider> _logger;

        /// <summary>Initializes a new instance of the <see cref="PlexLocalImageProvider"/> class.</summary>
        public PlexLocalImageProvider(PlexMetadataSourceService plex, ILogger<PlexLocalImageProvider> logger)
        {
            _plex = plex;
            _logger = logger;
        }

        /// <inheritdoc />
        public string Name => ProviderName;

        /// <inheritdoc />
        public int Order => 0;

        /// <inheritdoc />
        public bool Supports(BaseItem item) => _plex.AppliesTo(item);

        /// <inheritdoc />
        public IEnumerable<ImageType> GetSupportedImages(BaseItem item)
            => item is Episode ? new[] { ImageType.Primary } : new[] { ImageType.Primary, ImageType.Backdrop };

        /// <inheritdoc />
        public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
        {
            var settings = Plugin.Instance?.Configuration.PlexMetadataSource;
            if (settings is not { PullArtwork: true })
            {
                return Enumerable.Empty<RemoteImageInfo>();
            }

            var match = await _plex.FindMatchAsync(item, cancellationToken).ConfigureAwait(false);
            if (match == null)
            {
                return Enumerable.Empty<RemoteImageInfo>();
            }

            // Season posters are the parent poster of one of the season's episodes; an episode's
            // "poster" is its still; only movies, series and seasons have a backdrop worth taking.
            var posterName = match.ImageKind == PlexMetadataSourceService.PlexImageKind.Season ? "seasonposter" : "poster";
            var images = new List<RemoteImageInfo>
            {
                new() { ProviderName = Name, Type = ImageType.Primary, Url = Scheme + match.RatingKey + "/" + posterName },
            };
            if (item is not (Episode or Season))
            {
                images.Add(new RemoteImageInfo { ProviderName = Name, Type = ImageType.Backdrop, Url = Scheme + match.RatingKey + "/backdrop" });
            }

            return images;
        }

        /// <inheritdoc />
        public async Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        {
            if (url.StartsWith(Scheme, StringComparison.Ordinal))
            {
                var parts = url.Substring(Scheme.Length).Split('/');
                if (parts.Length == 2)
                {
                    var response = await _plex.GetImageAsync(parts[0], parts[1], cancellationToken).ConfigureAwait(false);
                    if (response != null)
                    {
                        return response;
                    }
                }
            }

            _logger.LogDebug("[Federation] Plex image unavailable for {Url}", url);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    /// <summary>
    /// Titles, summaries, genres, ratings and studios for the owner's own movies
    /// and series, taken from their Plex server.
    /// </summary>
    public class PlexLocalMetadataProvider :
        IRemoteMetadataProvider<Movie, MovieInfo>,
        IRemoteMetadataProvider<Series, SeriesInfo>,
        IRemoteMetadataProvider<Season, SeasonInfo>,
        IRemoteMetadataProvider<Episode, EpisodeInfo>,
        IHasOrder
    {
        private readonly PlexMetadataSourceService _plex;
        private readonly ILogger<PlexLocalMetadataProvider> _logger;

        /// <summary>Initializes a new instance of the <see cref="PlexLocalMetadataProvider"/> class.</summary>
        public PlexLocalMetadataProvider(PlexMetadataSourceService plex, ILogger<PlexLocalMetadataProvider> logger)
        {
            _plex = plex;
            _logger = logger;
        }

        /// <inheritdoc />
        public string Name => PlexLocalImageProvider.ProviderName;

        /// <inheritdoc />
        public int Order => 0;

        /// <inheritdoc />
        public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(MovieInfo searchInfo, CancellationToken cancellationToken)
            => Task.FromResult(Enumerable.Empty<RemoteSearchResult>());

        /// <inheritdoc />
        public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(SeriesInfo searchInfo, CancellationToken cancellationToken)
            => Task.FromResult(Enumerable.Empty<RemoteSearchResult>());

        /// <inheritdoc />
        public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(SeasonInfo searchInfo, CancellationToken cancellationToken)
            => Task.FromResult(Enumerable.Empty<RemoteSearchResult>());

        /// <inheritdoc />
        public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(EpisodeInfo searchInfo, CancellationToken cancellationToken)
            => Task.FromResult(Enumerable.Empty<RemoteSearchResult>());

        /// <inheritdoc />
        public Task<MetadataResult<Season>> GetMetadata(SeasonInfo info, CancellationToken cancellationToken)
            => GetAsync<Season>(
                info,
                token => _plex.FindSeasonAsync(info.SeriesProviderIds as IReadOnlyDictionary<string, string>, null, null, info.IndexNumber, token),
                cancellationToken);

        /// <inheritdoc />
        public Task<MetadataResult<Episode>> GetMetadata(EpisodeInfo info, CancellationToken cancellationToken)
            => GetAsync<Episode>(
                info,
                token => _plex.FindEpisodeAsync(info.SeriesProviderIds as IReadOnlyDictionary<string, string>, null, null, info.ParentIndexNumber, info.IndexNumber, token),
                cancellationToken);

        /// <inheritdoc />
        public Task<MetadataResult<Movie>> GetMetadata(MovieInfo info, CancellationToken cancellationToken)
            => GetAsync<Movie>(info, token => _plex.FindMatchAsync(info.ProviderIds as IReadOnlyDictionary<string, string>, info.Name, info.Year, token), cancellationToken);

        /// <inheritdoc />
        public Task<MetadataResult<Series>> GetMetadata(SeriesInfo info, CancellationToken cancellationToken)
            => GetAsync<Series>(info, token => _plex.FindMatchAsync(info.ProviderIds as IReadOnlyDictionary<string, string>, info.Name, info.Year, token), cancellationToken);

        /// <inheritdoc />
        public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

        private async Task<MetadataResult<T>> GetAsync<T>(ItemLookupInfo info, Func<CancellationToken, Task<PlexMetadataSourceService.PlexMatch?>> find, CancellationToken cancellationToken)
            where T : BaseItem, new()
        {
            var result = new MetadataResult<T>();
            var settings = Plugin.Instance?.Configuration.PlexMetadataSource;
            if (settings is not { Enabled: true, PullText: true })
            {
                return result;
            }

            try
            {
                var match = await find(cancellationToken).ConfigureAwait(false);
                if (match == null)
                {
                    return result;
                }

                var dto = match.Dto;
                var item = new T
                {
                    Name = dto.Name ?? info.Name ?? string.Empty,
                    OriginalTitle = dto.OriginalTitle,
                    Overview = dto.Overview,
                    ProductionYear = dto.ProductionYear ?? info.Year,
                    PremiereDate = dto.PremiereDate,
                    CommunityRating = dto.CommunityRating,
                    OfficialRating = dto.OfficialRating,
                    Genres = dto.Genres ?? Array.Empty<string>(),
                    Studios = dto.Studios?.Select(s => s.Name ?? string.Empty).Where(s => s.Length > 0).ToArray() ?? Array.Empty<string>(),
                };

                // Seasons are synthesized from their episodes, so only the number is meaningful there.
                if (item is Season season)
                {
                    season.Name = info.Name ?? season.Name;
                    season.Overview = null;
                    season.IndexNumber = info.IndexNumber;
                }
                else if (item is Episode episode)
                {
                    episode.IndexNumber = dto.IndexNumber ?? info.IndexNumber;
                    episode.ParentIndexNumber = dto.ParentIndexNumber;
                }

                result.Item = item;
                result.HasMetadata = true;
                result.Provider = Name;
                return result;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Federation] Plex metadata lookup failed for {Name}", info.Name);
                return result;
            }
        }
    }
}
