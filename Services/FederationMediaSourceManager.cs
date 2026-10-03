// Jellyfin declares IMediaSourceManager without nullable annotations; mirror that here so
// the signatures match it exactly and the build stays warning-free.
#nullable disable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Wraps Jellyfin's <see cref="IMediaSourceManager"/> so every federated media
    /// source carries a bounded ffmpeg analysis window (<see cref="FastStartTuning"/>).
    /// </summary>
    /// <remarks>
    /// An item's own (static) media source is built by Jellyfin from the item and has
    /// no hook of its own, and Jellyfin always makes it the default source, so the
    /// only place to influence it is where the manager hands sources out. Everything
    /// is forwarded untouched; only <c>AnalyzeDurationMs</c> is filled in, and only for
    /// federated items. A failure while tuning never fails the call.
    /// </remarks>
    public sealed class FederationMediaSourceManager : IMediaSourceManager
    {
        private readonly IMediaSourceManager _inner;

        /// <summary>
        /// Initializes a new instance of the <see cref="FederationMediaSourceManager"/> class.
        /// </summary>
        /// <param name="inner">Jellyfin's own manager that every call is forwarded to.</param>
        public FederationMediaSourceManager(IMediaSourceManager inner)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<MediaSourceInfo>> GetPlaybackMediaSources(BaseItem item, User user, bool allowMediaProbe, bool enablePathSubstitution, CancellationToken cancellationToken)
        {
            var sources = await _inner.GetPlaybackMediaSources(item, user, allowMediaProbe, enablePathSubstitution, cancellationToken).ConfigureAwait(false);
            Tune(item, sources);
            return sources;
        }

        /// <inheritdoc />
        public IReadOnlyList<MediaSourceInfo> GetStaticMediaSources(BaseItem item, bool enablePathSubstitution, User user = null)
        {
            var sources = _inner.GetStaticMediaSources(item, enablePathSubstitution, user);
            Tune(item, sources);
            return sources;
        }

        /// <inheritdoc />
        public async Task<MediaSourceInfo> GetMediaSource(BaseItem item, string mediaSourceId, string liveStreamId, bool enablePathSubstitution, CancellationToken cancellationToken)
        {
            var source = await _inner.GetMediaSource(item, mediaSourceId, liveStreamId, enablePathSubstitution, cancellationToken).ConfigureAwait(false);
            if (source != null)
            {
                Tune(item, new[] { source });
            }

            return source;
        }

        private static void Tune(BaseItem item, IEnumerable<MediaSourceInfo> sources)
        {
            try
            {
                FastStartTuning.Apply(item, sources);
            }
            catch (Exception)
            {
                // Tuning is an optimisation. Playback must never depend on it.
            }
        }

        /// <inheritdoc />
        public void AddParts(IEnumerable<IMediaSourceProvider> providers) => _inner.AddParts(providers);

        /// <inheritdoc />
        public IReadOnlyList<MediaStream> GetMediaStreams(Guid itemId) => _inner.GetMediaStreams(itemId);

        /// <inheritdoc />
        public IReadOnlyList<MediaStream> GetMediaStreams(MediaStreamQuery query) => _inner.GetMediaStreams(query);

        /// <inheritdoc />
        public IReadOnlyList<MediaAttachment> GetMediaAttachments(Guid itemId) => _inner.GetMediaAttachments(itemId);

        /// <inheritdoc />
        public IReadOnlyList<MediaAttachment> GetMediaAttachments(MediaAttachmentQuery query) => _inner.GetMediaAttachments(query);

        /// <inheritdoc />
        public Task<LiveStreamResponse> OpenLiveStream(LiveStreamRequest request, CancellationToken cancellationToken) => _inner.OpenLiveStream(request, cancellationToken);

        /// <inheritdoc />
        public Task<Tuple<LiveStreamResponse, IDirectStreamProvider>> OpenLiveStreamInternal(LiveStreamRequest request, CancellationToken cancellationToken) => _inner.OpenLiveStreamInternal(request, cancellationToken);

        /// <inheritdoc />
        public Task<MediaSourceInfo> GetLiveStream(string id, CancellationToken cancellationToken) => _inner.GetLiveStream(id, cancellationToken);

        /// <inheritdoc />
        public Task<Tuple<MediaSourceInfo, IDirectStreamProvider>> GetLiveStreamWithDirectStreamProvider(string id, CancellationToken cancellationToken) => _inner.GetLiveStreamWithDirectStreamProvider(id, cancellationToken);

        /// <inheritdoc />
        public ILiveStream GetLiveStreamInfo(string id) => _inner.GetLiveStreamInfo(id);

        /// <inheritdoc />
        public ILiveStream GetLiveStreamInfoByUniqueId(string uniqueId) => _inner.GetLiveStreamInfoByUniqueId(uniqueId);

        /// <inheritdoc />
        public Task<IReadOnlyList<MediaSourceInfo>> GetRecordingStreamMediaSources(ActiveRecordingInfo info, CancellationToken cancellationToken) => _inner.GetRecordingStreamMediaSources(info, cancellationToken);

        /// <inheritdoc />
        public Task CloseLiveStream(string id) => _inner.CloseLiveStream(id);

        /// <inheritdoc />
        public Task<MediaSourceInfo> GetLiveStreamMediaInfo(string id, CancellationToken cancellationToken) => _inner.GetLiveStreamMediaInfo(id, cancellationToken);

        /// <inheritdoc />
        public bool SupportsDirectStream(string path, MediaProtocol protocol) => _inner.SupportsDirectStream(path, protocol);

        /// <inheritdoc />
        public MediaProtocol GetPathProtocol(string path) => _inner.GetPathProtocol(path);

        /// <inheritdoc />
        public void SetDefaultAudioAndSubtitleStreamIndices(BaseItem item, MediaSourceInfo source, User user) => _inner.SetDefaultAudioAndSubtitleStreamIndices(item, source, user);

        /// <inheritdoc />
        public Task AddMediaInfoWithProbe(MediaSourceInfo mediaSource, bool isAudio, string cacheKey, bool addProbeDelay, bool isLiveStream, CancellationToken cancellationToken) => _inner.AddMediaInfoWithProbe(mediaSource, isAudio, cacheKey, addProbeDelay, isLiveStream, cancellationToken);
    }
}
