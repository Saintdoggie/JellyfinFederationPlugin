using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Provides multiple media sources for federated content: one per remote source
    /// so the user can pick which server to play from in the Jellyfin UI.
    /// </summary>
    public class FederationMediaSourceProvider : IMediaSourceProvider
    {
        private readonly ILogger<FederationMediaSourceProvider> _logger;
        private readonly FederationLibraryManager _federationManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="FederationMediaSourceProvider"/> class.
        /// </summary>
        public FederationMediaSourceProvider(
            ILogger<FederationMediaSourceProvider> logger,
            FederationLibraryManager federationManager)
        {
            _logger = logger;
            _federationManager = federationManager;
        }

        /// <inheritdoc />
        public Task<IEnumerable<MediaSourceInfo>> GetMediaSources(BaseItem item, CancellationToken cancellationToken)
        {
            if (item == null || !_federationManager.IsFederatedItem(item))
            {
                return Task.FromResult(Enumerable.Empty<MediaSourceInfo>());
            }

            try
            {
                if (!FederationLibraryManager.TryParseFederationPath(
                        item.Path,
                        out var mapping,
                        out _,
                        out _,
                        out _,
                        out _))
                {
                    return Task.FromResult(Enumerable.Empty<MediaSourceInfo>());
                }

                var entry = _federationManager.Cache.GetEntry(item.Path);
                if (entry == null)
                {
                    return Task.FromResult(Enumerable.Empty<MediaSourceInfo>());
                }

                var sources = new List<MediaSourceInfo>();
                for (int i = 0; i < entry.Sources.Count; i++)
                {
                    var src = entry.Sources[i];
                    var server = _federationManager.GetServer(src.ServerId);
                    if (server == null)
                    {
                        continue;
                    }

                    var client = _federationManager.GetClient(src.ServerId);
                    if (client == null)
                    {
                        continue;
                    }

                    var sourceName = entry.Sources.Count > 1
                        ? $"{server.Name}{(i == entry.PrimarySourceIndex ? " (primary)" : string.Empty)}"
                        : server.Name;

                    sources.Add(new MediaSourceInfo
                    {
                        Id = $"{src.ServerId}:{src.RemoteItemId}",
                        Name = sourceName,
                        Path = client.BuildDirectStreamUrl(src.RemoteItemId.ToString()),
                        Protocol = MediaProtocol.Http,
                        IsRemote = true,
                        SupportsDirectPlay = true,
                        SupportsDirectStream = true,
                        SupportsTranscoding = false,
                        RequiresOpening = false,
                        RequiresClosing = false,
                        RunTimeTicks = entry.Metadata.RunTimeTicks ?? item.RunTimeTicks,
                        Type = i == entry.PrimarySourceIndex ? MediaSourceType.Default : MediaSourceType.Grouping
                    });
                }

                if (sources.Count == 0)
                {
                    _logger.LogWarning("[Federation] No live sources for {Path}", item.Path);
                }

                return Task.FromResult<IEnumerable<MediaSourceInfo>>(sources);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Federation] Error getting media sources for {Path}", item.Path);
                return Task.FromResult(Enumerable.Empty<MediaSourceInfo>());
            }
        }

        /// <inheritdoc />
        public Task<ILiveStream> OpenMediaSource(string openToken, List<ILiveStream> currentLiveStreams, CancellationToken cancellationToken)
        {
            throw new NotImplementedException("Live stream opening is not supported for federated content");
        }
    }
}
