using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.FederationPlex
{
    // Jellyfin collects image and metadata providers once at start-up, and only from
    // assemblies it loaded itself. The add-in is loaded by this plugin, so its two
    // providers would never be seen. These stand in for them and pass every call on.
    // Without a running add-in they offer nothing and change nothing.

    /// <summary>Stand-in for the add-in's Plex artwork provider.</summary>
    public sealed class PlexImageProviderProxy : IRemoteImageProvider, IHasOrder
    {
        internal const string TargetType = "Jellyfin.Plugin.Federation.Providers.PlexLocalImageProvider";

        private readonly Lazy<IRemoteImageProvider?> _target;

        /// <summary>Initializes a new instance of the <see cref="PlexImageProviderProxy"/> class.</summary>
        public PlexImageProviderProxy(IServiceProvider services)
        {
            _target = new Lazy<IRemoteImageProvider?>(() => PlexAddInHost.Create<IRemoteImageProvider>(services, TargetType));
        }

        /// <inheritdoc />
        public string Name => _target.Value?.Name ?? "Plex (not running)";

        /// <inheritdoc />
        public int Order => (_target.Value as IHasOrder)?.Order ?? 100;

        /// <inheritdoc />
        public bool Supports(BaseItem item) => _target.Value?.Supports(item) == true;

        /// <inheritdoc />
        public IEnumerable<ImageType> GetSupportedImages(BaseItem item)
            => _target.Value?.GetSupportedImages(item) ?? Enumerable.Empty<ImageType>();

        /// <inheritdoc />
        public Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
            => _target.Value?.GetImages(item, cancellationToken) ?? Task.FromResult(Enumerable.Empty<RemoteImageInfo>());

        /// <inheritdoc />
        public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
            => _target.Value?.GetImageResponse(url, cancellationToken) ?? Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    /// <summary>Stand-in for the add-in's Plex metadata provider.</summary>
    public sealed class PlexMetadataProviderProxy :
        IRemoteMetadataProvider<Movie, MovieInfo>,
        IRemoteMetadataProvider<Series, SeriesInfo>,
        IRemoteMetadataProvider<Season, SeasonInfo>,
        IRemoteMetadataProvider<Episode, EpisodeInfo>,
        IHasOrder
    {
        internal const string TargetType = "Jellyfin.Plugin.Federation.Providers.PlexLocalMetadataProvider";

        private readonly Lazy<object?> _target;

        /// <summary>Initializes a new instance of the <see cref="PlexMetadataProviderProxy"/> class.</summary>
        public PlexMetadataProviderProxy(IServiceProvider services)
        {
            _target = new Lazy<object?>(() => PlexAddInHost.Create<object>(services, TargetType));
        }

        /// <inheritdoc />
        public string Name => (_target.Value as IRemoteMetadataProvider<Movie, MovieInfo>)?.Name ?? "Plex (not running)";

        /// <inheritdoc />
        public int Order => (_target.Value as IHasOrder)?.Order ?? 100;

        /// <inheritdoc />
        public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(MovieInfo searchInfo, CancellationToken cancellationToken)
            => Search<Movie, MovieInfo>(searchInfo, cancellationToken);

        /// <inheritdoc />
        public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(SeriesInfo searchInfo, CancellationToken cancellationToken)
            => Search<Series, SeriesInfo>(searchInfo, cancellationToken);

        /// <inheritdoc />
        public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(SeasonInfo searchInfo, CancellationToken cancellationToken)
            => Search<Season, SeasonInfo>(searchInfo, cancellationToken);

        /// <inheritdoc />
        public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(EpisodeInfo searchInfo, CancellationToken cancellationToken)
            => Search<Episode, EpisodeInfo>(searchInfo, cancellationToken);

        /// <inheritdoc />
        public Task<MetadataResult<Movie>> GetMetadata(MovieInfo info, CancellationToken cancellationToken)
            => Metadata<Movie, MovieInfo>(info, cancellationToken);

        /// <inheritdoc />
        public Task<MetadataResult<Series>> GetMetadata(SeriesInfo info, CancellationToken cancellationToken)
            => Metadata<Series, SeriesInfo>(info, cancellationToken);

        /// <inheritdoc />
        public Task<MetadataResult<Season>> GetMetadata(SeasonInfo info, CancellationToken cancellationToken)
            => Metadata<Season, SeasonInfo>(info, cancellationToken);

        /// <inheritdoc />
        public Task<MetadataResult<Episode>> GetMetadata(EpisodeInfo info, CancellationToken cancellationToken)
            => Metadata<Episode, EpisodeInfo>(info, cancellationToken);

        /// <inheritdoc />
        public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
            => (_target.Value as IRemoteSearchProvider)?.GetImageResponse(url, cancellationToken)
                ?? Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

        private Task<IEnumerable<RemoteSearchResult>> Search<TItem, TLookup>(TLookup info, CancellationToken cancellationToken)
            where TItem : BaseItem, IHasLookupInfo<TLookup>
            where TLookup : ItemLookupInfo, new()
            => (_target.Value as IRemoteMetadataProvider<TItem, TLookup>)?.GetSearchResults(info, cancellationToken)
                ?? Task.FromResult(Enumerable.Empty<RemoteSearchResult>());

        private Task<MetadataResult<TItem>> Metadata<TItem, TLookup>(TLookup info, CancellationToken cancellationToken)
            where TItem : BaseItem, IHasLookupInfo<TLookup>
            where TLookup : ItemLookupInfo, new()
            => (_target.Value as IRemoteMetadataProvider<TItem, TLookup>)?.GetMetadata(info, cancellationToken)
                ?? Task.FromResult(new MetadataResult<TItem>());
    }
}
