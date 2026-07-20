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
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Providers
{
    /// <summary>
    /// Pulls fresh metadata from the primary remote source for federated items.
    /// </summary>
    public class FederationMetadataProvider :
        IRemoteMetadataProvider<Movie, MovieInfo>,
        IRemoteMetadataProvider<Series, SeriesInfo>,
        IRemoteMetadataProvider<Episode, EpisodeInfo>,
        IRemoteMetadataProvider<Audio, SongInfo>
    {
        private static readonly HttpClient SharedHttpClient = new HttpClient();

        private readonly ILogger<FederationMetadataProvider> _logger;
        private readonly Services.FederationLibraryManager _federationManager;

        public FederationMetadataProvider(
            ILogger<FederationMetadataProvider> logger,
            Services.FederationLibraryManager federationManager)
        {
            _logger = logger;
            _federationManager = federationManager;
        }

        public string Name => "Federation";

        public bool Supports(BaseItem item) => _federationManager.IsFederatedItem(item);

        public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(MovieInfo info, CancellationToken cancellationToken)
            => Task.FromResult(Enumerable.Empty<RemoteSearchResult>());

        public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(SeriesInfo info, CancellationToken cancellationToken)
            => Task.FromResult(Enumerable.Empty<RemoteSearchResult>());

        public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(EpisodeInfo info, CancellationToken cancellationToken)
            => Task.FromResult(Enumerable.Empty<RemoteSearchResult>());

        public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(SongInfo info, CancellationToken cancellationToken)
            => Task.FromResult(Enumerable.Empty<RemoteSearchResult>());

        public async Task<MetadataResult<Movie>> GetMetadata(MovieInfo info, CancellationToken cancellationToken)
            => (MetadataResult<Movie>)await GetMetadataCommon<Movie>(info.Path, cancellationToken).ConfigureAwait(false);

        public async Task<MetadataResult<Series>> GetMetadata(SeriesInfo info, CancellationToken cancellationToken)
            => (MetadataResult<Series>)await GetMetadataCommon<Series>(info.Path, cancellationToken).ConfigureAwait(false);

        public async Task<MetadataResult<Episode>> GetMetadata(EpisodeInfo info, CancellationToken cancellationToken)
            => (MetadataResult<Episode>)await GetMetadataCommon<Episode>(info.Path, cancellationToken).ConfigureAwait(false);

        public async Task<MetadataResult<Audio>> GetMetadata(SongInfo info, CancellationToken cancellationToken)
            => (MetadataResult<Audio>)await GetMetadataCommon<Audio>(info.Path, cancellationToken).ConfigureAwait(false);

        public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
            => SharedHttpClient.GetAsync(url, cancellationToken);

        private async Task<object> GetMetadataCommon<T>(string? path, CancellationToken cancellationToken) where T : BaseItem, new()
        {
            var result = new MetadataResult<T> { HasMetadata = false };
            if (string.IsNullOrEmpty(path) || !path.StartsWith("federation://", StringComparison.OrdinalIgnoreCase))
            {
                return result;
            }

            var entry = _federationManager.Cache.GetEntry(path);
            var primary = entry?.GetPrimarySource();
            if (entry == null || primary == null)
            {
                return result;
            }

            var client = _federationManager.GetClient(primary.ServerId);
            if (client == null)
            {
                return result;
            }

            try
            {
                var remoteItem = await client.GetItemAsync(primary.RemoteItemId.ToString(), cancellationToken: cancellationToken).ConfigureAwait(false);
                if (remoteItem == null)
                {
                    return result;
                }

                var item = new T
                {
                    Name = remoteItem.Name ?? string.Empty,
                    Overview = remoteItem.Overview,
                    ProductionYear = remoteItem.ProductionYear,
                    PremiereDate = remoteItem.PremiereDate,
                    CommunityRating = remoteItem.CommunityRating,
                    OfficialRating = remoteItem.OfficialRating,
                    RunTimeTicks = remoteItem.RunTimeTicks,
                    Genres = remoteItem.Genres ?? Array.Empty<string>(),
                    Studios = remoteItem.Studios?.Select(s => s.Name ?? string.Empty).ToArray() ?? Array.Empty<string>(),
                    Tags = remoteItem.Tags ?? Array.Empty<string>(),
                    ProviderIds = remoteItem.ProviderIds != null
                        ? new Dictionary<string, string>(remoteItem.ProviderIds, StringComparer.OrdinalIgnoreCase)
                        : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                };

                if (item is Episode ep)
                {
                    ep.SeriesName = remoteItem.SeriesName;
                    ep.IndexNumber = remoteItem.IndexNumber;
                    ep.ParentIndexNumber = remoteItem.ParentIndexNumber;
                }

                if (item is Audio audio)
                {
                    audio.Album = remoteItem.Album;
                    audio.AlbumArtists = remoteItem.AlbumArtist != null ? new[] { remoteItem.AlbumArtist } : Array.Empty<string>();
                    audio.Artists = (remoteItem.Artists ?? Array.Empty<string>()).ToArray();
                    audio.IndexNumber = remoteItem.IndexNumber;
                }

                result.Item = item;
                result.HasMetadata = true;
                result.Provider = Name;

                if (remoteItem.People != null)
                {
                    foreach (var p in remoteItem.People)
                    {
                        result.AddPerson(new PersonInfo
                        {
                            Name = p.Name,
                            Role = p.Role,
                            Type = p.Type
                        });
                    }
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Federation] Failed to fetch metadata for {Path}", path);
                return result;
            }
        }
    }
}
