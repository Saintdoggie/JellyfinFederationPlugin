using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Provides multiple media sources for federated content: one per remote source
    /// so the user can pick which server to play from in the Jellyfin UI.
    /// Honors each server's <see cref="StreamingMode"/>: Direct sources point at a
    /// short-lived, single-item-scoped playback token minted from the remote (the
    /// remote's real api_key is never sent to a client - see
    /// <see cref="FederationLibraryManager.BuildPlaybackUrl"/> for the full
    /// rationale); Proxy sources route through this server so the remote key never
    /// leaves it either.
    /// </summary>
    public class FederationMediaSourceProvider : IMediaSourceProvider
    {
        private readonly ILogger<FederationMediaSourceProvider> _logger;
        private readonly FederationLibraryManager _federationManager;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly IAuthorizationContext _authorizationContext;
        private readonly RemoteAccessControlService _accessControl;
        private readonly ExternalCatalogRegistry _externalCatalogs;
        private readonly TranscodePlacementService? _placement;
        private readonly PlaybackRouteMetrics? _routeMetrics;
        private readonly AdaptiveSourceRanking? _ranking;

        /// <summary>
        /// Initializes a new instance of the <see cref="FederationMediaSourceProvider"/> class.
        /// </summary>
        public FederationMediaSourceProvider(
            ILogger<FederationMediaSourceProvider> logger,
            FederationLibraryManager federationManager,
            IHttpContextAccessor httpContextAccessor,
            IAuthorizationContext authorizationContext,
            RemoteAccessControlService accessControl,
            ExternalCatalogRegistry? externalCatalogs = null,
            TranscodePlacementService? placement = null,
            PlaybackRouteMetrics? routeMetrics = null,
            AdaptiveSourceRanking? ranking = null)
        {
            _placement = placement;
            _routeMetrics = routeMetrics;
            _ranking = ranking;
            _logger = logger;
            _federationManager = federationManager;
            _httpContextAccessor = httpContextAccessor;
            _authorizationContext = authorizationContext;
            _accessControl = accessControl;
            _externalCatalogs = externalCatalogs ?? new ExternalCatalogRegistry(Array.Empty<IExternalCatalogProvider>());
        }

        /// <summary>
        /// Resolves which of this server's own local users is asking, via
        /// Jellyfin's own <see cref="IAuthorizationContext"/> against the current
        /// inbound HTTP request - GetMediaSources always runs inside a real,
        /// authenticated PlaybackInfo request (see <see cref="ResolveLocalServerUrl"/>
        /// for the same assumption elsewhere in this class), so this is reliable
        /// without this plugin having to thread a user id through Jellyfin's own
        /// IMediaSourceProvider interface (which does not carry one). Returns null
        /// when it cannot be resolved (no request context, e.g. an internal call) -
        /// <see cref="RemoteAccessControlService.IsAllowed"/> treats that as "allow",
        /// the same as before this feature existed.
        /// </summary>
        private async Task<AuthorizationInfo?> ResolveAuthorizationInfo()
        {
            try
            {
                var context = _httpContextAccessor.HttpContext;
                return context == null ? null : await _authorizationContext.GetAuthorizationInfo(context).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[Federation] Could not resolve the local acting user for an access-control check");
                return null;
            }
        }

        /// <inheritdoc />
        public async Task<IEnumerable<MediaSourceInfo>> GetMediaSources(BaseItem item, CancellationToken cancellationToken)
        {
            var timing = System.Diagnostics.Stopwatch.StartNew();
            if (item == null)
            {
                return Enumerable.Empty<MediaSourceInfo>();
            }

            if (!_federationManager.IsFederatedItem(item))
            {
                // A title this server owns: nothing, unless the device cannot play
                // the own file as stored and a friend holds a copy it can.
                return await FriendCopiesForOwnedTitleAsync(item, cancellationToken).ConfigureAwait(false);
            }

            var preparationTimer = Stopwatch.StartNew();
            try
            {
                var key = FederationLibraryManager.GetFederationKey(item);
                var entry = key == null ? null : _federationManager.Cache.GetEntryByKey(key);
                if (entry == null)
                {
                    _logger.LogWarning(
                        "[Federation] Debug GetMediaSources: {Name} ({Type}) has FederationKey={Key} but no matching cache entry",
                        item.Name,
                        item.GetType().Name,
                        key);
                    return Enumerable.Empty<MediaSourceInfo>();
                }

                var entrySources = entry.GetSourcesSnapshot();
                var selectedStaticSource = _federationManager.GetPlaybackSource(entry);
                var primaryIndex = Array.IndexOf(entrySources, selectedStaticSource);
                var authorization = await ResolveAuthorizationInfo().ConfigureAwait(false);
                var localUserId = authorization != null && authorization.UserId != Guid.Empty ? authorization.UserId : (Guid?)null;
                var nativeIdentity = NativeClientCapabilities.Identify(_httpContextAccessor.HttpContext?.Request, authorization);

                _logger.LogInformation(
                    "[Federation] Debug GetMediaSources: {Name} ({Type}) key={Key}, sources={SourceCount} [{Sources}]",
                    item.Name,
                    item.GetType().Name,
                    key,
                    entrySources.Length,
                    string.Join(" | ", entrySources.Select(s => $"{s.ServerId}:{s.RemoteItemId}")));

                // When the item carries its own stream URL on Path, Jellyfin already
                // builds a full static media source from it (the primary one), so
                // emitting the primary again here would show the same file twice in the
                // client's version picker. Additional servers hosting the same content
                // are still worth offering as alternates.
                //
                // Compared against a freshly built URL rather than just checking that
                // Path is non-empty: item.Path is stamped once at sync time and
                // reconciliation only ever creates or deletes items, never updates them,
                // so a server whose address or api_key has since changed (re-friending
                // mints a new key, for instance) leaves every item pointing at a URL
                // that no longer works. When that happens the freshly built URL differs,
                // and emitting the primary here puts a working source back in front of
                // the client instead of leaving only the stale one.
                var primarySource = primaryIndex >= 0 && primaryIndex < entrySources.Length
                    ? entrySources[primaryIndex]
                    : null;

                // What the primary source's stamped static Path *should* look like
                // right now. Both modes stamp the local proxy-gateway URL (see
                // FederationLibraryManager.BuildStaticPath) - for Proxy mode that
                // is exactly BuildPlaybackUrl's output; for Direct mode
                // BuildPlaybackUrl builds an internal-only token-bearing remote
                // URL instead, so the proxy form is built here directly (against
                // this request's actual port, same as BuildPlaybackPathAsync's
                // Proxy branch). A base/port mismatch between stamp time and
                // request time just resolves to "not covered" - the provider then
                // emits its own fresh source in addition, which is the safe
                // fallback, never a broken one.
                var currentPrimaryUrl = primarySource == null
                    ? null
                    : _federationManager.BuildStaticPath(entry, primarySource);
                var staticSourceCoversPrimary = _federationManager.IsStoredStaticPathCurrent(item.Path, currentPrimaryUrl, TimeSpan.Zero);

                if (!string.IsNullOrEmpty(item.Path) && !staticSourceCoversPrimary)
                {
                    _logger.LogWarning(
                        "[Federation] {Name} has a stored stream path that no longer matches current server credentials, address, or access rules; serving a freshly built source instead (a sync will refresh the stored one)",
                        item.Name);
                }

                // Each candidate source needs one remote HTTP round trip
                // (FetchRemoteSourceAsync) to describe the actual file, and - for a
                // Direct-mode source - a second one to mint its playback token
                // (BuildPlaybackPathAsync). Resolving every non-network field first
                // and firing all the remote calls together (instead of one `await`
                // per source in a sequential loop, and instead of minting tokens
                // before ever starting the metadata fetches) turns what would
                // otherwise be several serial round trips per play into roughly one
                // round trip's worth - the slowest of them, not the sum of all.
                var adaptive = Plugin.Instance?.Configuration.EnableAdaptivePlayback == true;

                // Which copy the title's own id plays can differ per device when the
                // smart choice is on, and always on a server that may not convert:
                // there, a copy the device cannot play as stored cannot play at all.
                var selectionConfig = Plugin.Instance?.Configuration;
                var deviceAwareWanted = !adaptive && entrySources.Length > 1 && localUserId.HasValue
                    && (selectionConfig?.SmartSourceSelection == true || !TranscodePolicy.MayConvertForOwnViewers(selectionConfig));
                var candidates = new List<(int Index, FederatedSource Src, RemoteServer Server, string SourceName)>();
                for (int i = 0; i < entrySources.Length; i++)
                {
                    if (staticSourceCoversPrimary && i == primaryIndex && !adaptive && !deviceAwareWanted && !IsPeerDirect(_federationManager.GetServer(entrySources[i].ServerId)))
                    {
                        // The item's own Path already IS this source (stamped once
                        // at materialization time, outside any request context - see
                        // ResolvePlaybackUrl), and Jellyfin builds a static media
                        // source from it directly without calling this provider. A
                        // path only counts as covering when every configured user
                        // currently remains allowed; a newly restrictive rule makes
                        // an older path stale immediately and emits a fresh,
                        // user-bound provider source below.
                        //
                        // Never skipped for a Direct-mode peer: the stored Path is
                        // this server's loopback relay, so playing it sends every
                        // byte through this server's upload. The provider's own
                        // source points at the peer instead and replaces the
                        // stored one (see the Id assignment below).
                        continue;
                    }

                    var src = entrySources[i];
                    var server = _federationManager.GetServer(src.ServerId);
                    if (server == null || !server.Enabled)
                    {
                        // Matches what FederationSyncService already does when
                        // refreshing: a server that is disabled is as unusable as one
                        // that has been removed. Without the Enabled check a disabled
                        // server still produced a source here, and playback failed
                        // later against a host the user had deliberately turned off.
                        _logger.LogWarning(
                            "[Federation] Debug GetMediaSources: {Name} source #{Index} references disabled/missing server {ServerId} - skipping",
                            item.Name,
                            i,
                            src.ServerId);
                        continue;
                    }

                    if (FederationItemPersistenceService.AvailabilityOverride?.IsOffline(src.ServerId) == true)
                    {
                        // The background pinger marked this server unreachable:
                        // its items are hidden from the library, but a client
                        // holding an older PlaybackInfo URL could still ask.
                        // Refuse here too so a dead server fails fast instead of
                        // hanging playback on timeouts.
                        _logger.LogDebug(
                            "[Federation] {Name} source #{Index} on {ServerName} skipped: server currently offline",
                            item.Name,
                            i,
                            server.Name);
                        continue;
                    }

                    if (!_accessControl.IsAllowed(server, localUserId, entry.MappingName, src.RemoteItemId))
                    {
                        _logger.LogInformation(
                            "[Federation] {Name} source #{Index} on {ServerName} skipped for the current user by a per-remote-user access override",
                            item.Name,
                            i,
                            server.Name);
                        continue;
                    }

                    var sourceName = i == primaryIndex
                        ? PrimarySourceName(server.Name, entrySources.Length)
                        : server.Name;

                    candidates.Add((i, src, server, sourceName));
                }

                // The remote's own view of each file. Without Container and
                // MediaStreams, MediaInfoHelper.SetDeviceSpecificData has nothing
                // to run StreamBuilder against, so it cannot certify direct play
                // or direct stream for any device profile and every source comes
                // back unplayable - which surfaces in clients as
                // PlaybackError.NO_MEDIA_ERROR ("Unable to find a valid media
                // source to play") even though a source was returned.
                // Bound the whole remote batch phase. The shared per-server
                // HttpClient allows 5 minutes for a legitimately slow library
                // sync, which is far too long for a user sitting on "press
                // play": one hung remote would otherwise stall PlaybackInfo -
                // and therefore playback start - for up to that long. Twenty
                // seconds is generous for the small JSON metadata GET and
                // token-mint POST made here; every callee swallows its own
                // exceptions (returning null = source skipped), so a timeout
                // degrades to "this source isn't playable right now" instead of
                // throwing out of the provider.
                using var playPathCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                playPathCts.CancelAfter(TimeSpan.FromSeconds(20));

                var fetchTasks = candidates.Select(c => FetchRemoteSourceAsync(c.Server, c.Src, localUserId, playPathCts.Token)).ToArray();

                // Path-building (a token mint for a Direct-mode candidate, an
                // effectively-synchronous local URL build for a Proxy-mode one) runs
                // concurrently with the metadata fetches above, not serially before
                // them - see the comment on `candidates` above. Both Task.WhenAll
                // calls are issued back-to-back with no `await` between them so both
                // sets of remote calls are already in flight before either is
                // awaited.
                var pathTasks = candidates.Select(c => BuildPlaybackPathAsync(c.Server, c.Src, entry.ItemType, localUserId, playPathCts.Token)).ToArray();
                var fetchWhenAll = Task.WhenAll(fetchTasks);
                var pathWhenAll = Task.WhenAll(pathTasks);
                await Task.WhenAll(fetchWhenAll, pathWhenAll).ConfigureAwait(false);

                var remoteResults = fetchWhenAll.Result;
                var paths = pathWhenAll.Result;

                // Proxy mode otherwise pays for this sequentially before the first
                // upstream byte (FederationStreamHandler.BuildDirectStreamUrlAsync).
                // Register the per-user session now, in the background, while the
                // player is still negotiating; the cached token is then already
                // there. Failures are irrelevant - the stream path registers again.
                if (localUserId.HasValue)
                {
                    var userId = localUserId.Value.ToString("N");
                    foreach (var server in candidates.Select(c => c.Server))
                    {
                        if (server.Kind != ServerKind.Jellyfin || server.StreamingMode != StreamingMode.Proxy)
                        {
                            continue;
                        }

                        var client = _federationManager.GetClient(server.Id);
                        if (client == null)
                        {
                            continue;
                        }

                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await client.GetOrRegisterUserSessionTokenAsync(userId, null, CancellationToken.None).ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogDebug(ex, "[Federation] Early Proxy session registration with {Server} failed; the stream request registers again", server.Name);
                            }
                        });
                    }
                }

                var playable = new List<(int Index, FederatedSource Src, RemoteServer Server, string SourceName, MediaSourceInfo Remote, string Path, string? RemoteMediaSourceId, bool Distinguish)>();
                for (int c = 0; c < candidates.Count; c++)
                {
                    var (i, src, server, sourceName) = candidates[c];
                    var path = paths[c];
                    if (path == null)
                    {
                        _logger.LogWarning(
                            "[Federation] Debug GetMediaSources: {Name} source #{Index} on server {ServerName} ({StreamingMode}) produced a null playback path - skipping",
                            item.Name,
                            i,
                            server.Name,
                            server.StreamingMode);
                        continue;
                    }

                    var remotes = remoteResults[c];
                    if (remotes.Count == 0)
                    {
                        _logger.LogWarning(
                            "[Federation] GetMediaSources: {Name} source #{Index} on {ServerName} failed live preflight - skipping",
                            item.Name,
                            i,
                            server.Name);
                        continue;
                    }

                    var direct = server.StreamingMode == StreamingMode.Direct && server.Kind == ServerKind.Jellyfin;
                    foreach (var remote in direct ? remotes : remotes.Take(1))
                    {
                        _logger.LogInformation(
                            "[Federation] GetMediaSources: {Name} source #{Index} on {ServerName} -> id={MediaSourceId}, container={Container}, streams={StreamCount}, bitrate={Bitrate}",
                            item.Name,
                            i,
                            server.Name,
                            remote.Id ?? "(none)",
                            remote.Container ?? "(none)",
                            remote.MediaStreams?.Count ?? 0,
                            remote.Bitrate);

                        playable.Add((i, src, server, sourceName, remote, direct ? WithRemoteMediaSourceId(path, remote.Id) : path, remote.Id, direct && remotes.Count > 1));
                    }
                }

                if (playable.Count == 0)
                {
                    // Live preflight failed for every candidate. Skipping them all
                    // leaves PlaybackInfo empty and the Play button dead; the
                    // previous behavior still emitted a cached description so the
                    // static Path could be tried. Only used when nothing live
                    // remains - a healthy sibling still wins Auto ranking above.
                    for (int c = 0; c < candidates.Count; c++)
                    {
                        var path = paths[c];
                        if (path == null)
                        {
                            continue;
                        }

                        var (i, src, server, sourceName) = candidates[c];
                        var cachedStreams = src.MediaStreams?.ToList()
                            ?? entry.Metadata.MediaStreams?.ToList()
                            ?? new List<MediaStream>();
                        if (string.IsNullOrEmpty(src.Container)
                            && string.IsNullOrEmpty(entry.Metadata.Container)
                            && cachedStreams.Count == 0)
                        {
                            continue;
                        }

                        playable.Add((
                            i,
                            src,
                            server,
                            sourceName,
                            new MediaSourceInfo
                            {
                                Container = src.Container ?? entry.Metadata.Container,
                                Size = src.Size,
                                Bitrate = src.Bitrate,
                                RunTimeTicks = FirstPositiveRuntime(src.RunTimeTicks, entry.Metadata.RunTimeTicks, item.RunTimeTicks),
                                MediaStreams = cachedStreams
                            },
                            path,
                            null,
                            false));
                    }
                }

                // Sources that would take longer than the start budget to begin go after
                // those that would not (and, when every source is slow, the quickest
                // goes first). A source whose speed is not measured yet is never demoted.
                var deviceAware = deviceAwareWanted && playable.Count > 1;
                var deviceProfile = deviceAware ? FederationPlaybackProfileFilter.Get(_httpContextAccessor.HttpContext) : null;
                var fits = deviceProfile == null
                    ? null
                    : playable.Select(p => DevicePlayFit.Evaluate(deviceProfile, p.Remote.Container, p.Remote.MediaStreams, authorization?.User?.AudioLanguagePreference)).ToList();
                var smart = (selectionConfig?.SmartSourceSelection == true || fits != null) && playable.Count > 1
                    ? RankForViewer(
                        playable.Select(p => (p.Server, p.Src.Priority, p.Remote)).ToList(),
                        localUserId,
                        authorization?.DeviceId,
                        SourceCandidateFactory.Device(nativeIdentity.Client, nativeIdentity.Device, IsBrowserRequest()),
                        fits)
                    : null;
                if (smart != null)
                {
                    _logger.LogInformation("[Federation] Source order for {Name}: {Reason}", item.Name, smart.Reason);
                }

                var ordered = smart != null
                    ? smart.Ranked.Select(r => playable[int.Parse(r.Candidate.Key, System.Globalization.CultureInfo.InvariantCulture)]).ToList()
                    : playable
                    .OrderBy(p => ProjectedStart(p.Server, p.Remote) is > StartEstimator.StartBudgetSeconds ? 1 : 0)
                    .ThenBy(p => ProjectedStart(p.Server, p.Remote) is > StartEstimator.StartBudgetSeconds and var slow ? slow : 0)
                    .ThenBy(p => SourceFitGroup(p.Server, p.Remote))
                    .ThenBy(p => SourceOverageBitrate(p.Server, p.Remote))
                    .ThenByDescending(p => SourceHeight(p.Remote))
                    .ThenByDescending(p => SourceBitrate(p.Remote))
                    .ThenBy(p => p.Src.Priority)
                    .ToList();

                // The copy that answers to the title's own id for this viewer's device.
                // Chosen when the player sends its capabilities; every later request
                // of the play (which arrives without them) gets the same copy back.
                // A copy the viewer picked by name keeps its own id.
                var itemIdPosition = -1;
                if (deviceAware && localUserId.HasValue)
                {
                    var requestedId = RequestedMediaSourceId();
                    var explicitPick = !string.IsNullOrEmpty(requestedId) && !(Guid.TryParse(requestedId, out var requestedGuid) && requestedGuid == item.Id);
                    if (fits == null && ViewerCopyBinding.Shared.Find(localUserId.Value, authorization?.DeviceId, item.Id) is { } bound)
                    {
                        var at = ordered.FindIndex(p => string.Equals(SourceCandidateFactory.Key(p.Src), bound.CopyKey, StringComparison.Ordinal)
                            && (bound.RemoteMediaSourceId == null || string.Equals(p.RemoteMediaSourceId, bound.RemoteMediaSourceId, StringComparison.OrdinalIgnoreCase)));
                        if (at > 0)
                        {
                            var kept = ordered[at];
                            ordered.RemoveAt(at);
                            ordered.Insert(0, kept);
                        }
                    }

                    itemIdPosition = explicitPick
                        ? ordered.FindIndex(p => !string.Equals(BuildSourceId(p.Src, p.Distinguish ? p.RemoteMediaSourceId : null), requestedId, StringComparison.OrdinalIgnoreCase))
                        : 0;
                    if (fits != null && !explicitPick && itemIdPosition == 0)
                    {
                        ViewerCopyBinding.Shared.Bind(localUserId.Value, authorization?.DeviceId, item.Id, SourceCandidateFactory.Key(ordered[0].Src), ordered[0].Distinguish ? ordered[0].RemoteMediaSourceId : null);
                    }
                }

                var sources = new List<MediaSourceInfo>(ordered.Count);
                var replacedStatic = false;
                for (int position = 0; position < ordered.Count; position++)
                {
                    var candidate = ordered[position];
                    var remote = candidate.Remote;
                    var advertised = DescribeDeliveredSource(candidate.Server, candidate.Path, remote, entry.ItemType);

                    // Only one representation can take the item's own id; additional
                    // encodings on that same friend need distinct deterministic ids.
                    var replacesStatic = !adaptive && !replacedStatic
                        && (itemIdPosition >= 0 ? position == itemIdPosition : candidate.Index == primaryIndex);
                    replacedStatic |= replacesStatic;
                    var sourceId = replacesStatic
                        ? item.Id.ToString("N")
                        : BuildSourceId(candidate.Src, candidate.Distinguish ? candidate.RemoteMediaSourceId : null);

                    // Remote-transcode handoff: remember the friend token behind this
                    // source for this viewer, so FederationRemoteTranscodeFilter can
                    // send their transcode/subtitle requests to the friend. A source
                    // the WAN link can't carry as-is must be converted, and only the
                    // friend converts it (to the bitrate the player asked for).
                    var handoff = UsesRemoteTranscode(candidate.Server);

                    // Hardware-based choice (off by default): when the friend's
                    // hardware falls short for this source and this server does
                    // better, convert here. Only when the link can carry the
                    // original; otherwise the friend must shrink it first.
                    if (handoff && _placement != null
                        && !_placement.Decide(candidate.Server, item.Id, remote.MediaStreams, SourceOverageBitrate(candidate.Server, remote) == 0).FriendConverts)
                    {
                        handoff = false;
                        if (localUserId.HasValue)
                        {
                            // A grant from an earlier play would still send conversions to the friend.
                            PeerPlaybackGrants.Forget(localUserId.Value, item.Id, sourceId);
                        }
                    }

                    if (handoff && localUserId.HasValue && PeerPlaybackGrants.TokenFromDirectPath(candidate.Path) is { } grantToken)
                    {
                        PeerPlaybackGrants.Record(localUserId.Value, item.Id, sourceId, candidate.Server.Id, candidate.Src.RemoteItemId, grantToken, candidate.RemoteMediaSourceId);
                    }

                    // The title's subtitles and fonts come from the friend that has
                    // the file even when it converts nothing for us: a separate
                    // subtitle file exists only there.
                    if (localUserId.HasValue
                        && IsPeerDirect(candidate.Server)
                        && PeerTranscodeCapability.ServesSubtitles(candidate.Server.Id)
                        && PeerPlaybackGrants.TokenFromDirectPath(candidate.Path) is { } subtitleToken)
                    {
                        PeerPlaybackGrants.RecordSubtitles(localUserId.Value, item.Id, sourceId, candidate.Server.Id, candidate.Src.RemoteItemId, subtitleToken, candidate.RemoteMediaSourceId);
                    }

                    var mustConvert = SourceOverageBitrate(candidate.Server, remote) > 0
                        && (handoff || candidate.Server.StreamingMode == StreamingMode.Proxy || candidate.Server.Kind != ServerKind.Jellyfin);
                    var sourceName = candidate.Distinguish
                        ? $"{candidate.SourceName} ({SourceVariantName(remote)})"
                        : candidate.SourceName;

                    sources.Add(new MediaSourceInfo
                    {
                        // Jellyfin round-trips MediaSourceId through playback URLs and
                        // expects a plain 32-char hex string; the old
                        // "{serverId}:{remoteItemId}" composite did not survive that.
                        Id = sourceId,
                        Name = position == 0 ? $"{sourceName} (Auto)" : sourceName,
                        Path = candidate.Path,
                        Protocol = MediaProtocol.Http,

                        // Only true in Direct mode, where Path is a URL on a genuinely
                        // different host. In Proxy mode Path points back at this same
                        // server's own /Plugins/Federation/Stream endpoint, so from the
                        // client's perspective it is local. Getting this wrong matters:
                        // jellyfin-web's supportsDirectPlay() refuses IsRemote sources
                        // outright on clients that lack the RemoteVideo app feature
                        // (several smart-TV webviews), which would needlessly block
                        // Proxy-mode playback that this server is perfectly able to serve.
                        IsRemote = candidate.Server.StreamingMode == StreamingMode.Direct,
                        Container = advertised.Container,
                        Size = advertised.Size,
                        Bitrate = advertised.Bitrate,

                        // A copy carrying several sound tracks in the viewer's language
                        // is offered with one this device plays as the default, so a
                        // browser gets the AC-3 or AAC track instead of asking for the
                        // TrueHD one to be converted. The viewer's own pick still wins.
                        MediaStreams = DevicePlayFit.WithPlayableAudioFirst(
                            FederationPlaybackProfileFilter.Get(_httpContextAccessor.HttpContext),
                            advertised.MediaStreams ?? new List<MediaStream>(),
                            authorization?.User?.AudioLanguagePreference).ToList(),
                        // Native apps that can fetch the original directly avoid a
                        // conversion; browser and known Android raw-HTTP paths keep
                        // the container-aware Direct Stream/transcode route.
                        SupportsDirectPlay = !mustConvert && NativeClientCapabilities.SupportsRawHttpDirectPlay(_httpContextAccessor.HttpContext?.Request, nativeIdentity.Client, nativeIdentity.Device),
                        SupportsDirectStream = !mustConvert,

                        // Enabled so a client that cannot direct-play the remote's
                        // container still has a path: this server pulls the remote HTTP
                        // stream and transcodes it. That costs local bandwidth and CPU
                        // per federated stream, but with it false any incompatible
                        // container is simply unplayable. Under BlockAll this server
                        // only offers conversion when the friend is the one converting;
                        // otherwise the client direct-plays or picks another copy.
                        SupportsTranscoding = handoff || TranscodePolicy.MayConvertForOwnViewers(Plugin.Instance?.Configuration),

                        // Lets the transcoder/StreamBuilder verify details omitted by an
                        // otherwise-live remote source. Sources whose live lookup failed
                        // were removed above and are never exposed as probe candidates.
                        SupportsProbing = true,
                        RequiresOpening = false,
                        RequiresClosing = false,

                        RunTimeTicks = FirstPositiveRuntime(remote.RunTimeTicks, candidate.Src.RunTimeTicks, entry.Metadata.RunTimeTicks, item.RunTimeTicks),
                        Type = position == 0 || replacesStatic ? MediaSourceType.Default : MediaSourceType.Grouping
                    });
                }

                if (sources.Count == 0 && !staticSourceCoversPrimary)
                {
                    // Only a problem when the item's own path isn't already serving the
                    // primary source. When it is, returning nothing here is the normal
                    // single-server case, not a failure.
                    _logger.LogWarning("[Federation] No live sources for {Name}", item.Name);
                }

                _logger.LogInformation(
                    "[Federation][timing] Media sources for {Name} resolved in {Ms} ms ({Count} source(s))",
                    item.Name,
                    timing.ElapsedMilliseconds,
                    sources.Count);
                return sources;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Federation] Error getting media sources for {Name}", item.Name);
                return Enumerable.Empty<MediaSourceInfo>();
            }
            finally
            {
                _logger.LogDebug("[Federation] Playback source preparation finished in {ElapsedMs} ms", preparationTimer.Elapsed.TotalMilliseconds);
            }
        }

        /// <summary>Test seam: the own file's tracks (Jellyfin reads them from its database).</summary>
        internal Func<BaseItem, IReadOnlyList<MediaStream>> OwnStreams { get; set; } = item => item.GetMediaStreams();

        /// <summary>
        /// Friends' copies of a title this server owns, offered beside the own file
        /// only when the device cannot play the own file as stored and can play the
        /// friend's copy as stored (see <see cref="OwnedTitleFriendCopies"/>). Never
        /// offered for conversion: converting stays with the own file. Costs nothing
        /// for a play whose device plays the own file, and nothing at all when the
        /// automatic choice is off.
        /// </summary>
        private async Task<IEnumerable<MediaSourceInfo>> FriendCopiesForOwnedTitleAsync(BaseItem item, CancellationToken cancellationToken)
        {
            var config = Plugin.Instance?.Configuration;
            var context = _httpContextAccessor.HttpContext;
            if (!OwnedTitleFriendCopies.Wanted(config)
                || context == null
                || item is not Video
                || item.ProviderIds == null
                || item.ProviderIds.Count == 0)
            {
                return Enumerable.Empty<MediaSourceInfo>();
            }

            var profile = FederationPlaybackProfileFilter.Get(context);
            if (profile == null && OwnedTitleFriendCopies.Shared.IsEmpty)
            {
                return Enumerable.Empty<MediaSourceInfo>();
            }

            try
            {
                var authorization = await ResolveAuthorizationInfo().ConfigureAwait(false);
                if (authorization == null || authorization.UserId == Guid.Empty)
                {
                    return Enumerable.Empty<MediaSourceInfo>();
                }

                var userId = authorization.UserId;
                var deviceId = authorization.DeviceId;
                var language = authorization.User?.AudioLanguagePreference;
                IReadOnlySet<string>? offered = null;
                if (profile != null)
                {
                    var own = DevicePlayFit.Evaluate(profile, item.Container, OwnStreams(item), language);
                    if (!OwnedTitleFriendCopies.OwnFileNeedsHelp(own))
                    {
                        OwnedTitleFriendCopies.Shared.Forget(userId, deviceId, item.Id);
                        return Enumerable.Empty<MediaSourceInfo>();
                    }
                }
                else
                {
                    offered = OwnedTitleFriendCopies.Shared.Offered(userId, deviceId, item.Id);
                    if (offered == null || offered.Count == 0)
                    {
                        return Enumerable.Empty<MediaSourceInfo>();
                    }
                }

                var entry = FindFriendEntryForOwnedTitle(item, config!);
                if (entry == null)
                {
                    return Enumerable.Empty<MediaSourceInfo>();
                }

                var candidates = new List<(FederatedSource Src, RemoteServer Server)>();
                foreach (var src in entry.GetSourcesSnapshot())
                {
                    var server = _federationManager.GetServer(src.ServerId);
                    if (server is { Enabled: true, Kind: ServerKind.Jellyfin }
                        && FederationItemPersistenceService.AvailabilityOverride?.IsOffline(src.ServerId) != true
                        && _accessControl.IsAllowed(server, userId, entry.MappingName, src.RemoteItemId))
                    {
                        candidates.Add((src, server));
                    }
                }

                if (candidates.Count == 0)
                {
                    return Enumerable.Empty<MediaSourceInfo>();
                }

                // The own file can always still be converted, so a slow friend is
                // not worth holding up the play for.
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(6));
                var fetches = candidates.Select(c => FetchRemoteSourceAsync(c.Server, c.Src, userId, timeout.Token)).ToArray();
                var paths = candidates.Select(c => BuildPlaybackPathAsync(c.Server, c.Src, entry.ItemType, userId, timeout.Token)).ToArray();
                await Task.WhenAll(Task.WhenAll(fetches), Task.WhenAll(paths)).ConfigureAwait(false);

                var nativeIdentity = NativeClientCapabilities.Identify(context.Request, authorization);
                var sources = new List<MediaSourceInfo>();
                for (var c = 0; c < candidates.Count; c++)
                {
                    var (src, server) = candidates[c];
                    var path = paths[c].Result;
                    var remotes = fetches[c].Result;
                    if (path == null || remotes.Count == 0)
                    {
                        continue;
                    }

                    var direct = server.StreamingMode == StreamingMode.Direct;
                    foreach (var remote in direct ? remotes : remotes.Take(1))
                    {
                        var sourceId = BuildSourceId(src, direct && remotes.Count > 1 ? remote.Id : null);
                        var wanted = profile != null
                            ? DevicePlayFit.Evaluate(profile, remote.Container, remote.MediaStreams, language).Fit == PlayFit.Direct
                            : offered!.Contains(sourceId);
                        if (!wanted || SourceOverageBitrate(server, remote) > 0)
                        {
                            // Would itself need converting, or the friend's link cannot carry it.
                            continue;
                        }

                        var sourcePath = direct ? WithRemoteMediaSourceId(path, remote.Id) : path;
                        var advertised = DescribeDeliveredSource(server, sourcePath, remote, entry.ItemType);
                        sources.Add(new MediaSourceInfo
                        {
                            Id = sourceId,
                            Name = server.Name,
                            Path = sourcePath,
                            Protocol = MediaProtocol.Http,
                            IsRemote = direct,
                            Container = advertised.Container,
                            Size = advertised.Size,
                            Bitrate = advertised.Bitrate,
                            MediaStreams = DevicePlayFit.WithPlayableAudioFirst(
                                FederationPlaybackProfileFilter.Get(context),
                                advertised.MediaStreams ?? new List<MediaStream>(),
                                authorization?.User?.AudioLanguagePreference).ToList(),
                            SupportsDirectPlay = NativeClientCapabilities.SupportsRawHttpDirectPlay(context.Request, nativeIdentity.Client, nativeIdentity.Device),
                            SupportsDirectStream = true,
                            SupportsTranscoding = false,
                            SupportsProbing = false,
                            RequiresOpening = false,
                            RequiresClosing = false,
                            RunTimeTicks = FirstPositiveRuntime(remote.RunTimeTicks, src.RunTimeTicks, entry.Metadata.RunTimeTicks, item.RunTimeTicks),
                            Type = MediaSourceType.Grouping
                        });
                    }
                }

                if (profile != null)
                {
                    if (sources.Count == 0)
                    {
                        OwnedTitleFriendCopies.Shared.Forget(userId, deviceId, item.Id);
                    }
                    else
                    {
                        OwnedTitleFriendCopies.Shared.Remember(userId, deviceId, item.Id, sources.Select(s => s.Id));
                        context.Items[OwnedTitleFriendCopies.OfferKey] = sources.Select(s => s.Id).ToList();
                        _logger.LogInformation(
                            "[Federation] {Name} is owned here but this device cannot play the own file as stored; offering {Count} friend copy(ies) it can ({Servers})",
                            item.Name,
                            sources.Count,
                            string.Join(", ", sources.Select(s => s.Name).Distinct()));
                    }
                }

                return sources;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // The own file plays regardless.
                _logger.LogDebug(ex, "[Federation] Could not look for a friend's copy of {Name}", item.Name);
                return Enumerable.Empty<MediaSourceInfo>();
            }
        }

        /// <summary>
        /// The cache entry for friends' copies of a title this server owns: the
        /// same provider-id key the sync merges copies on, in any enabled library.
        /// </summary>
        private FederatedCacheEntry? FindFriendEntryForOwnedTitle(BaseItem item, PluginConfiguration config)
        {
            var kind = item.GetBaseItemKind().ToString();
            foreach (var providerName in config.DedupProviderIds ?? new List<string>())
            {
                if (!FederationLibraryManager.TryGetProviderId(item.ProviderIds, providerName, out var value) || string.IsNullOrEmpty(value))
                {
                    continue;
                }

                foreach (var mapping in config.LibraryMappings ?? new List<LibraryMapping>())
                {
                    if (!mapping.Enabled || string.IsNullOrEmpty(mapping.LocalLibraryName))
                    {
                        continue;
                    }

                    var entry = _federationManager.Cache.GetEntryByKey(FederationItemCache.BuildProviderKey(mapping.LocalLibraryName, providerName, value));
                    if (entry != null && string.Equals(entry.ItemType, kind, StringComparison.OrdinalIgnoreCase))
                    {
                        return entry;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Asks the remote server what the file actually is, via its own PlaybackInfo
        /// endpoint. External sources are resolved through their adapter and use
        /// the exact source-bound media snapshot captured during sync. Returns null
        /// when the source is not currently viable, which removes it from Auto.
        /// </summary>
        private async Task<IReadOnlyList<MediaSourceInfo>> FetchRemoteSourceAsync(RemoteServer server, FederatedSource src, Guid? localUserId, CancellationToken cancellationToken)
        {
            var external = _externalCatalogs.For(server);
            if (external != null)
            {
                var entryKey = _federationManager.Cache.TryGetLocalKeyForRemoteItem(src.ServerId, src.RemoteItemId);
                var entry = entryKey == null
                    ? _federationManager.Cache.TryFindEntryByRemoteItemId(src.RemoteItemId)
                    : _federationManager.Cache.GetEntryByKey(entryKey)
                        ?? _federationManager.Cache.TryFindEntryByRemoteItemId(src.RemoteItemId);
                var nativeId = entry?.GetNativeId(src);
                if (string.IsNullOrWhiteSpace(nativeId)
                    || await external.ResolveStreamUrlAsync(server, nativeId, cancellationToken).ConfigureAwait(false) == null)
                {
                    return Array.Empty<MediaSourceInfo>();
                }

                var currentSource = await external.GetMediaSourceAsync(server, nativeId, cancellationToken).ConfigureAwait(false);
                if (currentSource != null) return new[] { currentSource };

                return new[]
                {
                    new MediaSourceInfo
                    {
                        Container = src.Container,
                        Size = src.Size,
                        Bitrate = src.Bitrate,
                        RunTimeTicks = src.RunTimeTicks,
                        MediaStreams = src.MediaStreams?.ToList() ?? new List<MediaStream>()
                    }
                };
            }

            if (server.Kind != ServerKind.Jellyfin)
            {
                return Array.Empty<MediaSourceInfo>();
            }

            var client = _federationManager.GetClient(src.ServerId);
            if (client == null)
            {
                return Array.Empty<MediaSourceInfo>();
            }

            if (string.IsNullOrEmpty(server.UserId))
            {
                _logger.LogInformation(
                    "[Federation] Server {ServerName} has no UserId configured; relying on automatic playback-user resolution in GetPlaybackInfoAsync",
                    server.Name);
            }

            var info = await client.GetPlaybackInfoAsync(
                src.RemoteItemId.ToString("N"),
                cancellationToken: cancellationToken,
                localActingUserId: localUserId?.ToString("N")).ConfigureAwait(false);

            var remotes = info?.MediaSources?.Where(source => source != null).ToArray() ?? Array.Empty<MediaSourceInfo>();
            if (remotes.Length == 0)
            {
                _logger.LogWarning(
                    "[Federation] No remote media source for item {RemoteItemId} on server {ServerId}; falling back to cached metadata (playback will likely fail)",
                    src.RemoteItemId,
                    src.ServerId);
                return Array.Empty<MediaSourceInfo>();
            }

            foreach (var remote in remotes)
            {
                if (remote.MediaStreams == null)
                {
                    continue;
                }

                foreach (var stream in remote.MediaStreams)
                {
                    FederationLibraryManager.RedactStreamPath(stream);
                }
            }

            return remotes;
        }

        /// <summary>
        /// True when conversion of this peer's titles is handed to the peer
        /// (remote-transcode handoff): Direct-mode Jellyfin friend, enabled here,
        /// and the friend's last token said its gateway is open to us.
        /// </summary>
        internal static bool UsesRemoteTranscode(RemoteServer? server)
            => IsPeerDirect(server)
                && Plugin.Instance?.Configuration?.UseFriendsTranscoders != false
                && PeerTranscodeCapability.IsAvailable(server!.Id);

        /// <summary>True for a Jellyfin peer whose bytes go straight from it to the viewer.</summary>
        internal static bool IsPeerDirect(RemoteServer? server)
            => server is { StreamingMode: StreamingMode.Direct, Kind: ServerKind.Jellyfin };

        private bool IsBrowserRequest()
            => _httpContextAccessor.HttpContext?.Request is { } request && FederationRemoteTranscodeFilter.IsBrowser(request);

        /// <summary>
        /// Ranks live sources for the viewer who asked: the same scorer as the
        /// default-copy choice, plus what is known about this device (phone or TV,
        /// its own measured route to each friend, recent stalls). Candidate keys are
        /// the positions in <paramref name="sources"/>.
        /// </summary>
        internal SourceSelection RankForViewer(
            IReadOnlyList<(RemoteServer Server, int Priority, MediaSourceInfo Remote)> sources,
            Guid? userId,
            string? deviceId,
            ViewerDevice device,
            IReadOnlyList<DevicePlayFitResult>? fits = null)
        {
            var config = Plugin.Instance?.Configuration;
            var monitor = FederationItemPersistenceService.BandwidthOverride;
            var candidates = new List<SourceCandidate>(sources.Count);
            for (var n = 0; n < sources.Count; n++)
            {
                var (server, priority, remote) = sources[n];
                var candidate = SourceCandidateFactory.FromLive(
                    n.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    server,
                    priority,
                    SourceBitrate(remote),
                    remote.MediaStreams,
                    monitor,
                    config);

                PlaybackRouteSnapshot? route = null;
                if (_routeMetrics != null && userId.HasValue)
                {
                    foreach (var name in new[] { FederatedRoute.Direct, FederatedRoute.FriendConverts, "direct-link", FederatedRoute.Relay })
                    {
                        var snapshot = _routeMetrics.Snapshot(userId.Value, deviceId, server.Id, name);
                        if (snapshot != null && (route == null || (route.MeasuredMbps == null && snapshot.MeasuredMbps != null)))
                        {
                            route = snapshot;
                        }
                    }
                }

                candidates.Add(candidate with
                {
                    RouteMbps = route?.MeasuredMbps,
                    RecentlyStalled = route?.RecentlyStalled == true,
                    Reliability = _ranking?.Reliability(server.Id),
                    Fit = fits?[n].Fit ?? PlayFit.Unknown,
                    HasPreferredLanguage = fits?[n].HasPreferredLanguage,

                    // With the device's answer in hand, "somebody may convert it"
                    // has to be exact: this friend really converts, or this server may.
                    ConversionAvailable = fits == null
                        ? candidate.ConversionAvailable
                        : UsesRemoteTranscode(server) || TranscodePolicy.MayConvertForOwnViewers(config)
                });
            }

            return SourceSelectionScorer.Rank(candidates, device);
        }

        // The media source a request names: in its query, its route, or (a play
        // request) its body, which the profile filter kept.
        private string? RequestedMediaSourceId()
        {
            var context = _httpContextAccessor.HttpContext;
            if (context == null)
            {
                return null;
            }

            var fromQuery = RemoteMediaPath.QueryValue(context.Request.Query, "MediaSourceId");
            if (!string.IsNullOrEmpty(fromQuery))
            {
                return fromQuery;
            }

            if (context.Request.RouteValues.TryGetValue("mediaSourceId", out var routed) && routed is string fromRoute && fromRoute.Length > 0)
            {
                return fromRoute;
            }

            return FederationPlaybackProfileFilter.GetRequestedMediaSourceId(context);
        }

        private double? ProjectedStart(RemoteServer server, MediaSourceInfo source)
            => _federationManager.ProjectedStartSeconds(server, SourceBitrate(source) is > 0 and var bitrate ? bitrate : null);

        private int SourceFitGroup(RemoteServer server, MediaSourceInfo source)
        {
            var bitrate = SourceBitrate(source);
            var cap = _federationManager.GetEffectiveWanCapMbps(server);
            if (!cap.HasValue)
            {
                return bitrate > 0 ? 0 : 2;
            }

            if (bitrate <= 0)
            {
                return 2;
            }

            return bitrate <= cap.Value * 1_000_000L ? 0 : 1;
        }

        private long SourceOverageBitrate(RemoteServer server, MediaSourceInfo source)
        {
            var bitrate = SourceBitrate(source);
            var cap = _federationManager.GetEffectiveWanCapMbps(server);
            return cap.HasValue && bitrate > cap.Value * 1_000_000L ? bitrate : 0;
        }

        private static long SourceBitrate(MediaSourceInfo source)
            => source.Bitrate.GetValueOrDefault() > 0
                ? source.Bitrate.GetValueOrDefault()
                : source.MediaStreams?.Where(s => s.BitRate.HasValue).Sum(s => (long)s.BitRate!.Value) ?? 0;

        private static long? FirstPositiveRuntime(params long?[] values)
            => values.FirstOrDefault(value => value is > 0);

        private static int SourceHeight(MediaSourceInfo source)
            => source.MediaStreams?
                .Where(s => s.Type == MediaStreamType.Video)
                .Select(s => s.Height.GetValueOrDefault())
                .DefaultIfEmpty(0)
                .Max() ?? 0;

        /// <summary>
        /// Deterministic 32-char hex id for a federated source, stable across syncs so
        /// a resumed stream keeps pointing at the same remote.
        /// </summary>
        /// <summary>The label of the source that plays from the item's own path: just the server name, plus "(primary)" when other servers also offer the title.</summary>
        internal static string PrimarySourceName(string serverName, int sourceCount)
            => sourceCount > 1 ? $"{serverName} (primary)" : serverName;

        internal static string BuildSourceId(FederatedSource src, string? remoteMediaSourceId = null)
        {
            var suffix = string.IsNullOrWhiteSpace(remoteMediaSourceId) ? string.Empty : ":" + remoteMediaSourceId;
            var bytes = MD5.HashData(Encoding.UTF8.GetBytes($"{src.ServerId}:{src.RemoteItemId:N}{suffix}"));
            return new Guid(bytes).ToString("N");
        }

        private static string WithRemoteMediaSourceId(string path, string? remoteMediaSourceId)
            => string.IsNullOrWhiteSpace(remoteMediaSourceId)
                ? path
                : path + (path.Contains('?') ? '&' : '?') + "mediaSourceId=" + Uri.EscapeDataString(remoteMediaSourceId);

        private static string SourceVariantName(MediaSourceInfo source)
        {
            var video = source.MediaStreams?.FirstOrDefault(stream => stream.Type == MediaStreamType.Video);
            var details = new[] { video?.Codec, video?.Height is > 0 ? video.Height + "p" : null, source.Container }
                .Where(value => !string.IsNullOrWhiteSpace(value));
            return string.Join(" · ", details);
        }

        /// <summary>
        /// Describes what the built Path will actually deliver. Proxy mode and audio
        /// serve the original file, so the remote's live metadata stands. A capped
        /// Direct-mode path requests a fixed H.264/AAC MP4 transcode from the peer
        /// (see <see cref="FederationLibraryManager.BuildDirectGatewayLoopbackUrl"/>);
        /// advertising the original HEVC/AV1 MKV there made local StreamBuilder
        /// negotiate against bytes the client would never receive. Resolution, HDR
        /// and profile details of the original stream are unknowable after that
        /// transcode, so they are dropped rather than guessed; bitrate is the
        /// encoded budget, and Size is unknown because the transcode remuxes.
        /// </summary>
        private static MediaSourceInfo DescribeDeliveredSource(RemoteServer server, string path, MediaSourceInfo remote, string itemType)
        {
            var isAudio = string.Equals(itemType, "Audio", StringComparison.OrdinalIgnoreCase);
            if (isAudio || server.StreamingMode != StreamingMode.Direct)
            {
                return new MediaSourceInfo
                {
                    Container = remote.Container,
                    Size = remote.Size,
                    Bitrate = remote.Bitrate,
                    MediaStreams = remote.MediaStreams ?? new List<MediaStream>()
                };
            }

            int? capMbps = null;
            int? maxHeight = null;
            if (Uri.TryCreate(path, UriKind.Absolute, out var uri))
            {
                var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query);
                if (query.TryGetValue("capMbps", out var capValue) && int.TryParse(capValue.ToString(), out var parsedCap) && parsedCap > 0)
                {
                    capMbps = parsedCap;
                }

                if (query.TryGetValue("maxHeight", out var heightValue) && int.TryParse(heightValue.ToString(), out var parsedHeight) && parsedHeight > 0)
                {
                    maxHeight = parsedHeight;
                }
            }

            if (capMbps is not > 0)
            {
                return new MediaSourceInfo
                {
                    Container = remote.Container,
                    Size = remote.Size,
                    Bitrate = remote.Bitrate,
                    MediaStreams = remote.MediaStreams ?? new List<MediaStream>()
                };
            }

            var audioBitrate = 256_000;
            var videoBitrate = (int)Math.Min(int.MaxValue, Math.Max(1, (long)capMbps.Value * 950_000L - audioBitrate));
            var originalVideo = remote.MediaStreams?.FirstOrDefault(s => s.Type == MediaStreamType.Video);
            var originalAudio = remote.MediaStreams?.Where(s => s.Type == MediaStreamType.Audio).ToList() ?? new List<MediaStream>();
            var streams = new List<MediaStream>();
            if (originalVideo != null)
            {
                var downscale = maxHeight is > 0 && originalVideo.Height.GetValueOrDefault() > maxHeight.Value;
                streams.Add(new MediaStream
                {
                    Type = MediaStreamType.Video,
                    Index = originalVideo.Index,
                    Codec = "h264",
                    Language = originalVideo.Language,
                    IsDefault = originalVideo.IsDefault,
                    BitRate = videoBitrate,
                    AverageFrameRate = originalVideo.AverageFrameRate,
                    RealFrameRate = originalVideo.RealFrameRate,
                    Height = downscale ? maxHeight : originalVideo.Height,
                    Width = downscale && originalVideo.Width.HasValue
                        ? (int)Math.Round(originalVideo.Width.Value * (double)maxHeight!.Value / originalVideo.Height!.Value, MidpointRounding.AwayFromZero)
                        : originalVideo.Width
                });
            }

            if (originalAudio.Count == 0)
            {
                streams.Add(new MediaStream { Type = MediaStreamType.Audio, Codec = "aac", Channels = 2, BitRate = audioBitrate, IsDefault = true });
            }
            else
            {
                streams.AddRange(originalAudio.Select(audio => new MediaStream
                {
                    Type = audio.Type,
                    Index = audio.Index,
                    Codec = "aac",
                    Language = audio.Language,
                    IsDefault = audio.IsDefault,
                    BitRate = audioBitrate,
                    Channels = Math.Max(1, Math.Min(audio.Channels.GetValueOrDefault(2), 2)),
                    SampleRate = audio.SampleRate
                }));
            }

            streams.AddRange((remote.MediaStreams ?? new List<MediaStream>())
                .Where(stream => stream.Type == MediaStreamType.Subtitle && stream.IsTextSubtitleStream == true));

            return new MediaSourceInfo
            {
                Container = "mp4",
                Size = null,
                Bitrate = videoBitrate + audioBitrate,
                MediaStreams = streams
            };
        }

        /// <inheritdoc />
        public Task<ILiveStream> OpenMediaSource(string openToken, List<ILiveStream> currentLiveStreams, CancellationToken cancellationToken)
        {
            return Task.FromException<ILiveStream>(new NotSupportedException("Live stream opening is not supported for federated content"));
        }

        /// <summary>
        /// Builds the playback URL for one candidate source. The Proxy branch is
        /// unchanged and fully synchronous internally (just now awaited trivially).
        /// The Direct branch no longer embeds the remote server's real, long-lived
        /// api_key: instead it mints a short-lived, single-item-scoped playback
        /// token from the remote (one extra remote round trip, run concurrently
        /// with the remote metadata fetch by the caller - see the comment on
        /// `candidates` in <see cref="GetMediaSources"/>) and points at this
        /// server's own token-gated <c>DirectStream</c> gateway on the remote. See
        /// <see cref="FederationLibraryManager.BuildPlaybackUrl"/>'s doc comment for
        /// the full security rationale. Returns null (same "skip this source"
        /// degrade already used elsewhere in this method) when the server is
        /// unreachable or the remote refuses to mint a token - never falls back to
        /// embedding the raw key.
        /// </summary>
        /// <summary>
        /// Builds this server's proxy-gateway URL for a source, against the port
        /// this very request arrived on (see <see cref="ResolveLocalServerUrl"/>).
        /// Same shape <see cref="FederationLibraryManager.BuildStaticPath"/> stamps
        /// on item.Path at sync time; used to detect a stale/covering static source
        /// and as the actual Path of Proxy-mode provider sources.
        /// </summary>
        private string BuildProxyUrl(FederatedSource src, bool isAudio, Guid? localUserId = null)
        {
            var localUrl = ResolveLocalServerUrl();
            var audioFlag = isAudio ? "&audio=true" : string.Empty;
            var user = localUserId?.ToString("N");
            var requestingUserFlag = user == null ? string.Empty : $"&requestingUserId={user}";
            var signature = _federationManager.CreateProxySignature(src.ServerId, src.RemoteItemId, isAudio, user);
            return $"{localUrl}/Plugins/Federation/Stream?serverId={Uri.EscapeDataString(src.ServerId)}&itemId={src.RemoteItemId:N}{audioFlag}{requestingUserFlag}&sig={signature}";
        }

        private async Task<string?> BuildPlaybackPathAsync(RemoteServer server, FederatedSource src, string itemType, Guid? localUserId, CancellationToken cancellationToken)
        {
            // Non-Jellyfin servers are always proxied - see the matching guard in
            // FederationLibraryManager.BuildPlaybackUrl for why (the Direct branch
            // below mints a *federation* playback token, which only another
            // Jellyfin running this plugin can issue or honor).
            if (server.StreamingMode == StreamingMode.Proxy || server.Kind != ServerKind.Jellyfin)
            {
                // The remote api_key stays server-side; clients only see this server.
                // Redundant with the access check already applied in GetMediaSources
                // above (which decides whether to emit this source at all) - carried
                // along anyway so FederationController.Stream can re-check it itself
                // at the moment the bytes are actually requested, in case this URL
                // outlives the session it was minted for (bookmarked, cached by a
                // client, replayed later after the admin tightens the rule).
                return BuildProxyUrl(src, itemType == "Audio", localUserId);
            }

            var client = _federationManager.GetClient(src.ServerId);
            if (client == null)
            {
                return null;
            }

            // Client-visible Direct-mode Path is handed to browsers/players, so
            // it must be an item-scoped playback token. A 6-hour session token
            // authorizes any currently visible item: putting one on this URL
            // lets anyone who can read Path swap itemId and fetch a different
            // title. Session tokens remain available for server-side relays
            // (see FederationStreamHandler.BuildDirectStreamUrlAsync) that never
            // reach a client.
            var (token, _) = await client.GetPlaybackTokenAsync(
                src.RemoteItemId.ToString("N"),
                cancellationToken: cancellationToken,
                localActingUserId: localUserId?.ToString("N")).ConfigureAwait(false);

            if (token == null)
            {
                _logger.LogWarning(
                    "[Federation] Could not obtain a playback token from server {ServerName} for item {RemoteItemId}; this source will be skipped",
                    server.Name,
                    src.RemoteItemId);
                return null;
            }

            var directAudioFlag = itemType == "Audio" ? "&audio=true" : string.Empty;

            // With the remote-transcode handoff the friend converts whatever the
            // device needs, so direct play gets the original file and a source
            // over the WAN cap is marked transcode-only in GetMediaSources instead
            // of being pre-shrunk to one fixed progressive MP4 here.
            var capQuery = UsesRemoteTranscode(server)
                ? string.Empty
                : _federationManager.BuildDirectStreamCapQuery(server, itemType == "Audio", src);
            return $"{RemoteServerClientFactory.EffectiveUrl(server)}/Plugins/Federation/DirectStream/{src.RemoteItemId:N}?token={Uri.EscapeDataString(token)}{directAudioFlag}{capQuery}";
        }

        /// <summary>
        /// Resolves the base URL that Jellyfin's own transcoder will fetch the
        /// proxied stream from. Always loopback (or the admin's InternalServerUrl
        /// override), never <see cref="FederationLibraryManager.GetLocalServerUrl"/>
        /// (the public URL configured for peer handshakes): virtually every
        /// production setup runs Jellyfin behind a public reverse proxy or VPS
        /// tunnel, so building the transcoder's ffmpeg input URL from the public
        /// host - or from the incoming request host, the even older behaviour -
        /// meant every byte of a federated Proxy-mode stream hairpinned out
        /// through DNS, the CDN, and back through the tunnel just to reach the
        /// same Jellyfin process that spawned ffmpeg. That's what a 4K stream
        /// taking minutes to start actually was, and it hit anyone with a
        /// ServerUrl configured - which peer handshakes require, so effectively
        /// everyone. Since the URL returned here goes into MediaSourceInfo.Path
        /// and is only consumed by the server-side transcoder (federated Proxy
        /// streams are never client-direct-played - clients always get HLS from
        /// Jellyfin), loopback is both correct and dramatically faster than any
        /// public route.
        ///
        /// When Configuration.InternalServerUrl isn't set (the default), uses the
        /// port Kestrel accepted this very request on (so a non-default port stays
        /// right) when available, otherwise Jellyfin's default 8096 - the same
        /// fallback FederationLibraryManager.GetInternalPlaybackBaseUrl() uses when
        /// it can't detect a live port (background sync, no request in flight).
        /// </summary>
        private string ResolveLocalServerUrl()
        {
            var configured = Plugin.Instance?.Configuration?.InternalServerUrl;
            if (!string.IsNullOrEmpty(configured))
            {
                return configured.TrimEnd('/');
            }

            var context = _httpContextAccessor.HttpContext;
            var localPort = context?.Connection?.LocalPort;
            var port = localPort.HasValue && localPort.Value > 0 ? localPort.Value : 8096;
            return $"http://127.0.0.1:{port}";
        }
    }
}
