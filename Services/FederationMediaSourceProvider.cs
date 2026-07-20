using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;
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
    /// Honors each server's <see cref="StreamingMode"/>: Direct sources embed the
    /// remote api_key (documented tradeoff); Proxy sources route through this
    /// server so the remote key never reaches clients.
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
                var entry = _federationManager.Cache.GetEntry(item.Path);
                if (entry == null)
                {
                    return Task.FromResult(Enumerable.Empty<MediaSourceInfo>());
                }

                var entrySources = entry.GetSourcesSnapshot();
                var primaryIndex = Math.Min(entry.PrimarySourceIndex, entrySources.Length - 1);

                var sources = new List<MediaSourceInfo>();
                for (int i = 0; i < entrySources.Length; i++)
                {
                    var src = entrySources[i];
                    var server = _federationManager.GetServer(src.ServerId);
                    if (server == null)
                    {
                        continue;
                    }

                    var path = BuildPlaybackPath(server, src);
                    if (path == null)
                    {
                        continue;
                    }

                    var sourceName = entrySources.Length > 1
                        ? $"{server.Name}{(i == primaryIndex ? " (primary)" : string.Empty)}"
                        : server.Name;

                    sources.Add(new MediaSourceInfo
                    {
                        Id = $"{src.ServerId}:{src.RemoteItemId}",
                        Name = sourceName,
                        Path = path,
                        Protocol = MediaProtocol.Http,
                        IsRemote = true,
                        SupportsDirectPlay = true,
                        SupportsDirectStream = true,
                        SupportsTranscoding = false,
                        RequiresOpening = false,
                        RequiresClosing = false,
                        RunTimeTicks = entry.Metadata.RunTimeTicks ?? item.RunTimeTicks,
                        Type = i == primaryIndex ? MediaSourceType.Default : MediaSourceType.Grouping
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
            return Task.FromException<ILiveStream>(new NotSupportedException("Live stream opening is not supported for federated content"));
        }

        private string? BuildPlaybackPath(RemoteServer server, FederatedSource src)
        {
            if (server.StreamingMode == StreamingMode.Proxy)
            {
                var localUrl = _federationManager.GetLocalServerUrl();
                if (string.IsNullOrEmpty(localUrl))
                {
                    _logger.LogWarning(
                        "[Federation] Server {Server} is in Proxy mode but no local server URL is configured; skipping source",
                        server.Name);
                    return null;
                }

                // The remote api_key stays server-side; clients only see this server.
                return $"{localUrl}/Plugins/Federation/Stream?serverId={Uri.EscapeDataString(src.ServerId)}&itemId={src.RemoteItemId}";
            }

            var client = _federationManager.GetClient(src.ServerId);
            return client?.BuildDirectStreamUrl(src.RemoteItemId.ToString());
        }
    }
}
