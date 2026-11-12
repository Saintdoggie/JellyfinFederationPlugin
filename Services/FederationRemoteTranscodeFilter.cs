using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Receiver side of the remote-transcode handoff: sends a viewer's transcode,
    /// subtitle and font requests for a friend's title to that friend, so the
    /// friend's server converts its own file and the bytes go straight from it to
    /// the player. The viewer stays signed in here; this server still negotiated
    /// the playback (device profile, bitrate, tracks) and records progress.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>HLS master/variant playlists, subtitles and fonts are fetched from the
    /// friend by this server (small, and a refusal can still fall back): the
    /// playlist the player gets points at the friend for every segment.</item>
    /// <item>Progressive streams and stray segment requests are 302-redirected.</item>
    /// <item>Anything that can't be handed off - not a friend's title, friend on an
    /// older version or switched off (including its 403 saying so), viewer not
    /// allowed, an https page that would be handed an http address - falls through
    /// to Jellyfin, which converts here exactly as before.</item>
    /// <item>A friend that offers to convert but fails (at its limit, an error)
    /// gives the player its answer: a friend's title plays only from friends,
    /// and this server never steps in to convert a play a friend started.</item>
    /// <item>With automatic copy choice on (or when this server may not convert),
    /// another friend holding the same title can take a play: at start when the
    /// first friend does not answer, and mid-play as a redundant variant in the
    /// master playlist (see <see cref="HandoffStandby"/>).</item>
    /// <item>This server's own credentials (<c>ApiKey</c> on the URL) are never
    /// forwarded; the friend's item-scoped token carries the authorization and the
    /// friend re-checks sharing on every request.</item>
    /// </list>
    /// </remarks>
    public sealed class FederationRemoteTranscodeFilter : IAsyncActionFilter
    {
        private const int MaxPlaylistBytes = 8 * 1024 * 1024;

        private static readonly HashSet<string> MediaControllers = new(StringComparer.OrdinalIgnoreCase)
        {
            "DynamicHls", "Videos", "Audio", "UniversalAudio", "Subtitle", "VideoAttachments"
        };

        private static readonly HttpClient Http = new(FederationHttp.Stamp(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = System.Net.DecompressionMethods.None,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            UseCookies = false,
            ConnectCallback = FederationHttp.ConnectAsync
        }))
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        private readonly IAuthorizationContext _authorization;
        private readonly IUserManager _users;
        private readonly ILibraryManager _library;
        private readonly IMediaSourceManager _mediaSources;
        private readonly FederationLibraryManager _federationManager;
        private readonly RemoteAccessControlService _access;
        private readonly RemoteTranscodeHandoffs _handoffs;
        private readonly ILogger<FederationRemoteTranscodeFilter> _logger;
        private readonly DirectLink.DirectLinkManager? _directLinks;
        private readonly MediaBrowser.Common.Net.INetworkManager? _network;
        private readonly PlaybackRouteMetrics? _routeMetrics;
        private readonly FederationBandwidthAllocator? _bandwidth;

        /// <summary>
        /// Initializes a new instance of the <see cref="FederationRemoteTranscodeFilter"/> class.
        /// </summary>
        public FederationRemoteTranscodeFilter(
            IAuthorizationContext authorization,
            IUserManager users,
            ILibraryManager library,
            IMediaSourceManager mediaSources,
            FederationLibraryManager federationManager,
            RemoteAccessControlService access,
            RemoteTranscodeHandoffs handoffs,
            ILogger<FederationRemoteTranscodeFilter> logger,
            DirectLink.DirectLinkManager? directLinks = null,
            MediaBrowser.Common.Net.INetworkManager? network = null,
            PlaybackRouteMetrics? routeMetrics = null,
            FederationBandwidthAllocator? bandwidth = null)
        {
            _bandwidth = bandwidth;
            _routeMetrics = routeMetrics;
            _directLinks = directLinks;
            _network = network;
            _authorization = authorization;
            _users = users;
            _library = library;
            _mediaSources = mediaSources;
            _federationManager = federationManager;
            _access = access;
            _handoffs = handoffs;
            _logger = logger;
        }

        /// <summary>Test seam: the HTTP send used to fetch from the friend.</summary>
        internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Send { get; set; } = (request, ct) => Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        internal Func<string, HttpClient?>? LinkClientOverride { get; set; }

        private HttpClient? LinkClient(string serverId)
        {
            if (LinkClientOverride != null)
            {
                return LinkClientOverride(serverId);
            }

            return _directLinks?.TryGetClient(serverId, out var client) == true ? client : null;
        }

        /// <inheritdoc />
        public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            RelayPingIfHandedOff(context);
            if (!IsCandidate(context, out var path))
            {
                await next().ConfigureAwait(false);
                return;
            }

            var playSessionId = RemoteMediaPath.QueryValue(context.HttpContext.Request.Query, "PlaySessionId");
            if (path!.Kind == RemoteMediaKind.HlsSegment && _handoffs.HandedOffTo(playSessionId) == null)
            {
                // A segment of a play this server's own transcoder started (its
                // playlist came from here): one play never mixes two transcoders.
                // Decided before ResolveAsync, which can hit the friend over the network.
                HlsQualityLadder.ApplyRungUserAgent(context.HttpContext.Request);
                await next().ConfigureAwait(false);
                return;
            }

            Handoff? handoff = null;
            try
            {
                handoff = await ResolveAsync(context, path!).ConfigureAwait(false);
            }
            catch (FederationBandwidthExceededException)
            {
                context.HttpContext.Response.Headers.CacheControl = "private, no-store";
                context.Result = new ObjectResult(new { error = "bandwidth-capacity", message = "The friend has no unallocated bandwidth for this playback." })
                {
                    StatusCode = StatusCodes.Status429TooManyRequests
                };
                return;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[Federation] Remote transcode handoff check failed; converting here");
            }

            if (handoff == null)
            {
                if (LocalConversionBlocked(path))
                {
                    context.HttpContext.Response.Headers.CacheControl = "private, no-store";
                    context.Result = LocalConversionRefused();
                    return;
                }

                await ConvertHereAsync(context, next, path).ConfigureAwait(false);
                return;
            }

            FederationPlaybackRoutes.Record(playSessionId, FederatedRoute.FriendConverts);
            if (path.Kind == RemoteMediaKind.HlsMaster)
            {
                FirstSegmentTiming.RecordPlaylist(playSessionId);
            }
            else if (path.Kind == RemoteMediaKind.HlsSegment && FirstSegmentTiming.CompleteSegment(playSessionId) is { } firstSegmentMs)
            {
                _logger.LogInformation("[Federation][timing] First handed-off segment for {ItemId} answered {Ms} ms after the master playlist", path.ItemId, firstSegmentMs);
            }
            if (!path.FetchedByReceiver)
            {
                if (path.StartsWork)
                {
                    _handoffs.Record(playSessionId, handoff.ServerId);
                }

                // The URL carries an item-scoped token; never log it.
                context.HttpContext.Response.Headers.CacheControl = "private, no-store";
                context.Result = new RedirectResult(handoff.PlayerUrl, permanent: false);
                return;
            }

            var playlist = path.Kind is RemoteMediaKind.HlsMaster or RemoteMediaKind.HlsVariant or RemoteMediaKind.SubtitlePlaylist;
            string? relayPrefix = HandoffPlaylistLinks.RelayPrefix(context.HttpContext.Request.PathBase.Value);
            // A new play with another friend able to take it does not wait the
            // full half minute on a friend that is not answering.
            TimeSpan? startDeadline = path.Kind == RemoteMediaKind.HlsMaster
                && HandoffStandby.Wanted(Plugin.Instance?.Configuration)
                && HandoffStandby.AllowsAnotherCopy(context.HttpContext.Request.Query)
                && OtherServers(handoff).Count > 0
                    ? HandoffStandby.StartCooldown
                    : null;
            var (fetched, status) = await FetchAsync(handoff, playlist, relayPrefix, context.HttpContext.RequestAborted, startDeadline).ConfigureAwait(false);
            if (status == StatusCodes.Status403Forbidden)
            {
                // Most often a token the friend no longer knows (it restarted).
                // Drop every token from it, get a fresh one (the provider mints it
                // while recording the grant again) and try once more; a friend
                // that really switched converting off says so in that new token.
                RemoteServerClient.InvalidatePlaybackTokens(handoff.ServerId);
                PeerPlaybackGrants.Forget(handoff.UserId, path.ItemId, handoff.SourceId);
                var fresh = await ResolveAsync(context, path).ConfigureAwait(false);
                if (fresh != null)
                {
                    handoff = fresh;
                    (fetched, status) = await FetchAsync(handoff, playlist, relayPrefix, context.HttpContext.RequestAborted).ConfigureAwait(false);
                    if (fetched != null)
                    {
                        _logger.LogInformation("[Federation] {Server} refused an old token; a fresh one worked", handoff.ServerName);
                    }
                }
                else
                {
                    // The fresh token says the friend no longer converts for us.
                    if (LocalConversionBlocked(path))
                    {
                        context.HttpContext.Response.Headers.CacheControl = "private, no-store";
                        context.Result = LocalConversionRefused();
                        return;
                    }

                    _logger.LogInformation("[Federation] {Server} no longer converts for us; converting {ItemId} here", handoff.ServerName, path.ItemId);
                    await ConvertHereAsync(context, next, path).ConfigureAwait(false);
                    return;
                }
            }

            if (fetched == null && status != StatusCodes.Status403Forbidden
                && await StartOnAnotherServerAsync(context, path, handoff with { FailedStatus = status }, relayPrefix, playSessionId).ConfigureAwait(false) is { } moved)
            {
                (fetched, handoff) = (moved.Result, moved.Handoff);
            }

            if (fetched != null)
            {
                if (path.StartsWork)
                {
                    _handoffs.Record(playSessionId, handoff.ServerId);
                }

                fetched = await WithStandbyAsync(context, path, handoff, fetched, relayPrefix, playSessionId).ConfigureAwait(false);
                if (handoff.ViaLink)
                {
                    _logger.LogInformation("[Federation] {Kind} for {ItemId} handed to {Server}: it converts, and the video comes over the direct link to this home viewer", path.Kind, path.ItemId, handoff.ServerName);
                }
                else
                {
                    _logger.LogInformation("[Federation] {Kind} for {ItemId} handed to {Server}: it converts and streams straight to the player", path.Kind, path.ItemId, handoff.ServerName);
                }

                context.HttpContext.Response.Headers.CacheControl = "private, no-store";
                context.Result = fetched;
                return;
            }

            if (status == StatusCodes.Status403Forbidden)
            {
                // The friend no longer converts for us (switched off). Until a
                // new token says otherwise, its titles convert here, whole plays
                // at a time - this is the playlist every play starts with.
                PeerTranscodeCapability.Record(handoff.ServerId, false);
                PeerPlaybackGrants.Forget(handoff.UserId, path.ItemId, handoff.SourceId);
                if (LocalConversionBlocked(path))
                {
                    context.HttpContext.Response.Headers.CacheControl = "private, no-store";
                    context.Result = LocalConversionRefused();
                    return;
                }

                _logger.LogInformation("[Federation] {Server} no longer converts for us; converting {ItemId} here", handoff.ServerName, path.ItemId);
                await ConvertHereAsync(context, next, path).ConfigureAwait(false);
                return;
            }

            // A friend's title plays from friends. When the friend offers to
            // convert but fails and no other friend could start it, the player
            // gets the friend's answer; this server does not step in.
            _logger.LogWarning("[Federation] {Server} could not play {ItemId} ({Kind}, HTTP {Status}); the title plays only from its own server", handoff.ServerName, path.ItemId, path.Kind, status);
            context.HttpContext.Response.Headers.CacheControl = "private, no-store";
            context.Result = new StatusCodeResult(status);
        }

        /// <summary>
        /// Lets this server's Jellyfin handle a federated title's request, with the
        /// quality ladder applied when it is on: a lower level's request is stamped
        /// so Jellyfin keeps its files apart, and the master playlist Jellyfin
        /// returns gets the lower levels added. Ordinary local titles never reach
        /// this (only federated items), and with the switch off nothing changes.
        /// </summary>
        private async Task ConvertHereAsync(ActionExecutingContext context, ActionExecutionDelegate next, RemoteMediaPath path)
        {
            HlsQualityLadder.ApplyRungUserAgent(context.HttpContext.Request);
            var executed = await next().ConfigureAwait(false);
            if (path.Kind != RemoteMediaKind.HlsMaster
                || executed.Exception != null
                || executed.Result is not FileContentResult { FileContents.Length: > 0 and <= MaxPlaylistBytes } playlist
                || Plugin.Instance?.Configuration?.AdaptiveQualityLadder != true)
            {
                return;
            }

            try
            {
                var item = _library.GetItemById(path.ItemId);
                var key = item == null ? null : FederationLibraryManager.GetFederationKey(item);
                var entry = key == null ? null : _federationManager.Cache.GetEntryByKey(key);
                if (entry == null)
                {
                    return;
                }

                var sourceId = path.MediaSourceId?.ToString("N") ?? RemoteMediaPath.QueryValue(context.HttpContext.Request.Query, "MediaSourceId");
                var source = SelectSource(entry, path.ItemId, sourceId);
                var text = HandoffPlaylistLinks.Decode(playlist.FileContents);
                var withLevels = HlsQualityLadder.Expand(text, source?.Bitrate is > 0 ? source.Bitrate : null);
                if (!ReferenceEquals(withLevels, text))
                {
                    executed.Result = new FileContentResult(System.Text.Encoding.UTF8.GetBytes(withLevels), playlist.ContentType);
                    _logger.LogInformation("[Federation] Offering lower quality levels for {ItemId}, converted here, so the player can step down instead of buffering", path.ItemId);
                }
            }
            catch (Exception ex)
            {
                // The playlist Jellyfin wrote is still valid on its own.
                _logger.LogDebug(ex, "[Federation] Could not add quality levels to a playlist; leaving it as Jellyfin wrote it");
            }
        }

        /// <summary>
        /// True when this request would start conversion on this server for a
        /// federated item while <see cref="LocalTranscodeRestriction.BlockAll"/>
        /// is set. Only requests that start work are refused: an already-running
        /// local conversion is left to finish, and ordinary local items are never
        /// affected. A handoff to the friend is decided before this is consulted.
        /// </summary>
        private bool LocalConversionBlocked(RemoteMediaPath path)
        {
            if (TranscodePolicy.MayConvertForOwnViewers(Plugin.Instance?.Configuration) || !path.StartsWork)
            {
                return false;
            }

            var item = _library.GetItemById(path.ItemId);
            return item != null && FederationLibraryManager.GetFederationKey(item) != null;
        }

        private static ObjectResult LocalConversionRefused()
            => new(new { error = "local-transcoding-disabled", message = "This server is set never to convert video, and the server owning this title cannot convert it either. Choose a copy this device can play directly." })
            {
                StatusCode = StatusCodes.Status409Conflict
            };

        /// <summary>
        /// Some players keep a paused transcode alive with <c>Sessions/Playing/Ping</c>
        /// rather than progress reports (which <see cref="RemoteTranscodeSessionBridge"/>
        /// relays). For a handed-off session, pass the ping on to the friend running
        /// it; Jellyfin still handles the request here as usual.
        /// </summary>
        private void RelayPingIfHandedOff(ActionExecutingContext context)
        {
            if (!context.ActionDescriptor.RouteValues.TryGetValue("action", out var action)
                || !string.Equals(action, "PingPlaybackSession", StringComparison.Ordinal)
                || !context.ActionArguments.TryGetValue("playSessionId", out var value)
                || value is not string playSessionId)
            {
                return;
            }

            var serverId = _handoffs.TakePing(playSessionId, isPaused: true);
            var client = serverId == null ? null : _federationManager.GetClient(serverId);
            if (client != null)
            {
                _ = Task.Run(() => client.SendPlayControlAsync(playSessionId, "ping", null, CancellationToken.None));
            }
        }

        /// <summary>
        /// Whether this request is one the handoff may carry: a GET to a native media
        /// endpoint in an allowed shape. Static video direct play is left to
        /// <see cref="FederationDirectStreamRedirectFilter"/>.
        /// </summary>
        internal static bool IsCandidate(ActionExecutingContext context, out RemoteMediaPath? path)
        {
            path = null;
            var request = context.HttpContext.Request;
            if (!HttpMethods.IsGet(request.Method)
                || Plugin.Instance?.Configuration?.UseFriendsTranscoders == false
                || !context.ActionDescriptor.RouteValues.TryGetValue("controller", out var controller)
                || controller == null
                || !MediaControllers.Contains(controller)
                || !RemoteMediaPath.TryParse(request.Path.Value, out path)
                || path == null)
            {
                return false;
            }

            var isStatic = string.Equals(RemoteMediaPath.QueryValue(request.Query, "static"), "true", StringComparison.OrdinalIgnoreCase);
            if (path.Kind == RemoteMediaKind.Stream && isStatic && !path.Audio)
            {
                return false;
            }

            return true;
        }

        /// <param name="context">The player's request.</param>
        /// <param name="path">What is asked for (for a standby: the variant playlist).</param>
        /// <param name="forced">Another copy to resolve instead of the one the request addresses.</param>
        /// <param name="reserve">False for a standby, which must not take bandwidth until a player uses it.</param>
        /// <param name="audioIndex">The sound track number on <paramref name="forced"/>, replacing the request's.</param>
        private async Task<Handoff?> ResolveAsync(ActionExecutingContext context, RemoteMediaPath path, FederatedSource? forced = null, bool reserve = true, int? audioIndex = null)
        {
            var item = _library.GetItemById(path.ItemId);
            var key = FederationLibraryManager.GetFederationKey(item);
            if (item == null || key == null)
            {
                return null;
            }

            var info = await _authorization.GetAuthorizationInfo(context.HttpContext).ConfigureAwait(false);
            if (info == null || !info.IsAuthenticated)
            {
                return null;
            }

            var user = info.User ?? (info.UserId != Guid.Empty ? _users.GetUserById(info.UserId) : null);
            if (user == null || _library.GetItemById<BaseItem>(path.ItemId, user) == null)
            {
                return null;
            }

            var entry = _federationManager.Cache.GetEntryByKey(key);
            if (entry == null)
            {
                return null;
            }

            var request = context.HttpContext.Request;
            var sourceId = path.MediaSourceId?.ToString("N")
                ?? RemoteMediaPath.QueryValue(request.Query, "MediaSourceId");
            var source = forced ?? SelectSource(entry, item.Id, sourceId, user.Id, info.DeviceId);
            var server = source == null ? null : _federationManager.GetServer(source.ServerId);
            if (source == null
                || server == null
                || !server.Enabled
                || FederationItemPersistenceService.AvailabilityOverride?.IsOffline(server.Id) == true
                || !_access.IsAllowed(server, user.Id, entry.MappingName, source.RemoteItemId))
            {
                return null;
            }

            if (!FederationMediaSourceProvider.UsesRemoteTranscode(server))
            {
                if (forced == null)
                {
                    ApplyLocalConversionCap(context, path, server, source, user.Id, info);
                }

                return null;
            }

            var normalizedSourceId = string.IsNullOrEmpty(sourceId) ? item.Id.ToString("N") : sourceId;
            PeerPlaybackGrants.Grant? grant;
            if (forced != null)
            {
                // Another copy than the request names: its grant is under that
                // copy's own id, or under the item's id when it is the default copy.
                (grant, normalizedSourceId) = GrantFor(user.Id, item.Id, forced);
                if (grant == null)
                {
                    await _mediaSources.GetPlaybackMediaSources(item, user, false, false, context.HttpContext.RequestAborted).ConfigureAwait(false);
                    (grant, normalizedSourceId) = GrantFor(user.Id, item.Id, forced);
                }
            }
            else
            {
                grant = PeerPlaybackGrants.Find(user.Id, item.Id, normalizedSourceId);
                if (grant == null)
                {
                    // The provider records a grant for every source it offers this
                    // viewer; asking it again (warm caches, cached token) fills it.
                    await _mediaSources.GetPlaybackMediaSources(item, user, false, false, context.HttpContext.RequestAborted).ConfigureAwait(false);
                    grant = PeerPlaybackGrants.Find(user.Id, item.Id, normalizedSourceId);
                }
            }

            if (grant == null
                || !string.Equals(grant.ServerId, server.Id, StringComparison.OrdinalIgnoreCase)
                || grant.RemoteItemId != source.RemoteItemId)
            {
                return null;
            }

            var peerBase = RemoteServerClientFactory.EffectiveUrl(server).TrimEnd('/');
            var clientScheme = FederationDirectStreamRedirectFilter.RequestScheme(request);
            var playerSeesPeer = !path.FetchedByReceiver || path.Kind is RemoteMediaKind.HlsMaster or RemoteMediaKind.HlsVariant;

            // A viewer on this home's network, with the direct link to the friend
            // up: the player fetches from this server, which gets the video over
            // the link (home download speed; never the home upload). Others go
            // straight to the friend as before.
            var viaLink = _directLinks?.IsConnected(server.Id) == true
                && DirectLink.DirectLinkRelay.IsHomeViewer(context.HttpContext, _network);
            var playerBase = viaLink ? DirectLink.DirectLinkRelay.PlayerBase(request.PathBase.Value, server.Id) : peerBase;
            if (playerSeesPeer
                && !viaLink
                && string.Equals(clientScheme, "https", StringComparison.OrdinalIgnoreCase)
                && !peerBase.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                // A browser on https refuses http media (mixed content), and some
                // players refuse a redirect that changes scheme.
                return null;
            }

            if (playerSeesPeer
                && !viaLink
                && IsBrowser(request)
                && BrowserPlaybackHeaders.Origin(BrowserPlaybackHeaders.OwnOrigin() ?? $"{clientScheme}://{request.Host.Value}") is { } playerOrigin
                && await BrowserPlaybackHeaders.ConvertedProblemAsync(server, peerBase, playerOrigin, _logger).ConfigureAwait(false) != null)
            {
                // A web-server header (the friend's or ours) would make the browser
                // refuse every segment; converted here, the video stays on this site.
                return null;
            }

            // Over the link, the measured link is what carries the video, not the
            // friend's tunnel the Auto cap was measured on; manual caps still apply.
            var fallbackCap = _federationManager.GetEffectiveWanCapMbps(server);
            var cap = viaLink && server.WanCapMode == WanCapMode.Auto
                ? LinkCap(_directLinks!.LinkMbps(server.Id), fallbackCap)
                : fallbackCap;
            var sharedCapacity = cap;

            var identity = NativeClientCapabilities.Identify(request, info);
            var handheld = HandheldClientLimits.IsHandheld(identity.Client, identity.Device);
            cap = HandheldClientLimits.Cap(cap, handheld);
            cap = _routeMetrics?.GetCapMbps(user.Id, info.DeviceId, server.Id, viaLink ? "direct-link" : FederatedRoute.FriendConverts, cap) ?? cap;
            if (reserve && path.StartsWork && _bandwidth != null)
            {
                var allocation = _bandwidth.Allocate(server.Id, RemoteMediaPath.QueryValue(request.Query, "PlaySessionId"), cap, sharedCapacity, user.Id.ToString("N") + "|" + info.DeviceId);
                if (!allocation.Accepted)
                {
                    throw new FederationBandwidthExceededException();
                }

                cap = allocation.CapMbps;
            }

            var remoteMediaSourceId = Guid.TryParse(grant.RemoteMediaSourceId, out var grantedSourceId) ? grantedSourceId : source.RemoteItemId;
            var query = RemoteMediaPath.FilterQuery(request.Query, (name, value) =>
                audioIndex.HasValue && string.Equals(name, "AudioStreamIndex", StringComparison.OrdinalIgnoreCase)
                    ? audioIndex.Value.ToString(CultureInfo.InvariantCulture)
                    : RewriteQueryValue(name, value, remoteMediaSourceId, cap, request.Query));
            if (path.Kind is RemoteMediaKind.HlsMaster or RemoteMediaKind.HlsVariant or RemoteMediaKind.Stream)
            {
                query = HandheldClientLimits.WithResolution(query, request.Query, handheld);
            }

            var nativePath = path.ToNativePath(source.RemoteItemId, path.MediaSourceId.HasValue ? remoteMediaSourceId : null);
            var url = $"{peerBase}/Plugins/Federation/Peer/Play/{Uri.EscapeDataString(grant.Token)}/{nativePath}{query}";
            return new Handoff(url, server.Id, server.Name, user.Id, normalizedSourceId, peerBase, path.Kind, playerBase, viaLink, source.Bitrate is > 0 ? source.Bitrate : null)
            {
                Source = source,
                Entry = entry
            };
        }

        /// <summary>
        /// The grant the provider recorded for <paramref name="source"/> and the
        /// local source id it is under: the copy's own id, or the item's id when
        /// this copy is the one the item id stands for.
        /// </summary>
        internal static (PeerPlaybackGrants.Grant? Grant, string SourceId) GrantFor(Guid userId, Guid itemId, FederatedSource source)
        {
            foreach (var id in new[] { FederationMediaSourceProvider.BuildSourceId(source), itemId.ToString("N") })
            {
                var grant = PeerPlaybackGrants.Find(userId, itemId, id);
                if (grant != null
                    && string.Equals(grant.ServerId, source.ServerId, StringComparison.OrdinalIgnoreCase)
                    && grant.RemoteItemId == source.RemoteItemId)
                {
                    return (grant, id);
                }
            }

            return (null, FederationMediaSourceProvider.BuildSourceId(source));
        }

        /// <summary>
        /// The other friends' copies that could take over this play, in the cached
        /// order: enabled, reachable, converting for us, and allowed for the viewer
        /// (re-checked in full by <see cref="ResolveAsync"/>).
        /// </summary>
        private List<FederatedSource> OtherServers(Handoff handoff)
        {
            if (handoff.Entry == null || handoff.Source == null)
            {
                return new List<FederatedSource>();
            }

            return handoff.Entry.GetSourcesSnapshot()
                .Where(s => !string.Equals(s.ServerId, handoff.ServerId, StringComparison.OrdinalIgnoreCase)
                    && FederationMediaSourceProvider.UsesRemoteTranscode(_federationManager.GetServer(s.ServerId)))
                .GroupBy(s => s.ServerId, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
        }

        /// <summary>
        /// Lists a second friend's copy in the master playlist as a redundant
        /// variant, so the player can move there when the first server stops
        /// delivering (see <see cref="HandoffStandby"/>). Returns the result
        /// unchanged whenever no like-for-like standby exists. Never throws.
        /// </summary>
        private async Task<IActionResult> WithStandbyAsync(ActionExecutingContext context, RemoteMediaPath path, Handoff handoff, IActionResult fetched, string? relayPrefix, string? playSessionId)
        {
            try
            {
                var request = context.HttpContext.Request;
                if (path.Kind != RemoteMediaKind.HlsMaster
                    || fetched is not FileContentResult { FileContents.Length: > 0 } file
                    || string.IsNullOrWhiteSpace(playSessionId)
                    || handoff.Source == null
                    || !HandoffStandby.Wanted(Plugin.Instance?.Configuration)
                    || !HandoffStandby.AllowsMidPlaySwitch(request.Query, handoff.SourceBitrate))
                {
                    return fetched;
                }

                var variantPath = path with { Kind = RemoteMediaKind.HlsVariant, Suffix = "main.m3u8" };
                foreach (var other in OtherServers(handoff))
                {
                    if (!HandoffStandby.SameCut(handoff.Source, other))
                    {
                        continue;
                    }

                    var (audioOk, audioIndex) = HandoffStandby.MapAudio(handoff.Source, other, RemoteMediaPath.QueryLong(request.Query, "AudioStreamIndex"));
                    if (!audioOk)
                    {
                        continue;
                    }

                    var standby = await ResolveAsync(context, variantPath, other, reserve: false, audioIndex).ConfigureAwait(false);
                    if (standby == null || HandoffStandby.IsReduced(handoff.Url, standby.Url))
                    {
                        // Not converting for this viewer, or its link cannot carry
                        // this play at full quality.
                        continue;
                    }

                    var friendUrl = HandoffPlaylistLinks.ToPeer(standby.Url, standby.PlayerBase);
                    if (friendUrl == null)
                    {
                        continue;
                    }

                    var uri = standby.ViaLink || relayPrefix == null ? friendUrl : relayPrefix + Uri.EscapeDataString(friendUrl);
                    var text = HandoffPlaylistLinks.Decode(file.FileContents);
                    var withStandby = HandoffStandby.AddVariant(text, uri);
                    if (ReferenceEquals(withStandby, text))
                    {
                        return fetched;
                    }

                    _handoffs.AddStandby(playSessionId, standby.ServerId);
                    _logger.LogInformation("[Federation] {Standby} stands by for {ItemId}: the player can move there if {Server} stops delivering", standby.ServerName, path.ItemId, handoff.ServerName);
                    return new FileContentResult(System.Text.Encoding.UTF8.GetBytes(withStandby), file.ContentType);
                }
            }
            catch (OperationCanceledException) when (context.HttpContext.RequestAborted.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[Federation] Could not add a standby server to a playlist; leaving it as it was");
            }

            return fetched;
        }

        /// <summary>
        /// The friend picked for a new play did not answer its master playlist:
        /// starts the play on the next friend holding the title instead. Null when
        /// nobody else can, and the first friend's answer goes to the player.
        /// </summary>
        private async Task<(IActionResult Result, Handoff Handoff)?> StartOnAnotherServerAsync(ActionExecutingContext context, RemoteMediaPath path, Handoff failed, string? relayPrefix, string? playSessionId)
        {
            var request = context.HttpContext.Request;
            if (path.Kind != RemoteMediaKind.HlsMaster
                || failed.Source == null
                || !HandoffStandby.Wanted(Plugin.Instance?.Configuration)
                || !HandoffStandby.AllowsAnotherCopy(request.Query))
            {
                return null;
            }

            FederationItemPersistenceService.SmartSelectionMemory.NoteFailure(failed.ServerId);
            foreach (var other in OtherServers(failed))
            {
                try
                {
                    var (audioOk, audioIndex) = HandoffStandby.MapAudio(failed.Source, other, RemoteMediaPath.QueryLong(request.Query, "AudioStreamIndex"));
                    if (!audioOk)
                    {
                        continue;
                    }

                    // The failed friend carries nothing: its share goes back first.
                    _bandwidth?.Release(failed.ServerId, playSessionId);
                    var next = await ResolveAsync(context, path, other, reserve: true, audioIndex).ConfigureAwait(false);
                    if (next == null)
                    {
                        continue;
                    }

                    var (fetched, status) = await FetchAsync(next, true, relayPrefix, context.HttpContext.RequestAborted, HandoffStandby.StartCooldown).ConfigureAwait(false);
                    if (fetched != null)
                    {
                        _logger.LogInformation("[Federation] {Failed} did not start {ItemId} (HTTP {Status}); starting it on {Server} instead", failed.ServerName, path.ItemId, failed.FailedStatus, next.ServerName);
                        return (fetched, next);
                    }

                    FederationItemPersistenceService.SmartSelectionMemory.NoteFailure(next.ServerId);
                    _bandwidth?.Release(next.ServerId, playSessionId);
                    _logger.LogInformation("[Federation] {Server} could not start {ItemId} either (HTTP {Status})", next.ServerName, path.ItemId, status);
                }
                catch (FederationBandwidthExceededException)
                {
                    // That friend's link is full; try the next one.
                }
            }

            return null;
        }

        private void ApplyLocalConversionCap(ActionExecutingContext context, RemoteMediaPath path, RemoteServer server, FederatedSource source, Guid userId, AuthorizationInfo info)
        {
            var request = context.HttpContext.Request;
            var sharedCapacity = _federationManager.GetEffectiveWanCapMbps(server);
            var identity = NativeClientCapabilities.Identify(request, info);
            var handheld = HandheldClientLimits.IsHandheld(identity.Client, identity.Device);
            var cap = HandheldClientLimits.Cap(sharedCapacity, handheld);
            cap = _routeMetrics?.GetCapMbps(userId, info.DeviceId, server.Id, FederatedRoute.Relay, cap) ?? cap;
            if (path.StartsWork && _bandwidth != null)
            {
                var allocation = _bandwidth.Allocate(server.Id, RemoteMediaPath.QueryValue(request.Query, "PlaySessionId"), cap, sharedCapacity, userId.ToString("N") + "|" + info.DeviceId);
                if (!allocation.Accepted)
                {
                    throw new FederationBandwidthExceededException();
                }

                cap = allocation.CapMbps;
            }

            var query = LocalConversionQuery(request.Query, cap,
                handheld && (path.Kind is RemoteMediaKind.HlsMaster or RemoteMediaKind.HlsVariant or RemoteMediaKind.Stream));

            request.QueryString = new QueryString(query);
            var capped = RemoteMediaPath.ParseQuery(query);
            UpdateBoundArgument(context, "maxStreamingBitrate", capped, "MaxStreamingBitrate");
            UpdateBoundArgument(context, "videoBitrate", capped, "VideoBitrate");
            UpdateBoundArgument(context, "maxWidth", capped, "MaxWidth");
            UpdateBoundArgument(context, "maxHeight", capped, "MaxHeight");
        }

        /// <summary>Applies conversion limits to a request handled by this server's own Jellyfin.</summary>
        internal static string LocalConversionQuery(IEnumerable<KeyValuePair<string, StringValues>> requested, int? cap, bool handheld)
        {
            // This request stays on this server. The peer allow-list deliberately
            // strips ApiKey and native session/profile fields; using it here makes
            // Jellyfin emit child playlists without the native player's URL token.
            // Preserve the existing local query, including repeated values, and
            // change only the limits. Peer forwarding still uses FilterQuery.
            var query = QueryString.Create(requested.SelectMany(pair => pair.Value.Select(value =>
                new KeyValuePair<string, string?>(pair.Key,
                    string.Equals(pair.Key, "MediaSourceId", StringComparison.OrdinalIgnoreCase)
                        ? value
                        : RewriteQueryValue(pair.Key, value ?? string.Empty, Guid.Empty, cap, requested))))).Value ?? string.Empty;
            return HandheldClientLimits.WithResolution(query, requested, handheld);
        }

        private static void UpdateBoundArgument(ActionExecutingContext context, string argument, IEnumerable<KeyValuePair<string, StringValues>> query, string name)
        {
            if (context.ActionArguments.ContainsKey(argument)
                && RemoteMediaPath.QueryLong(query, name) is >= 0 and <= int.MaxValue and var value)
            {
                context.ActionArguments[argument] = (int)value;
            }
        }

        /// <summary>
        /// A web player (jellyfin-web, also inside the mobile apps), which enforces
        /// CORS and the page's security headers; native players do not.
        /// </summary>
        internal static bool IsBrowser(HttpRequest request)
            => request.Headers.Origin.Count > 0
                || request.Headers.UserAgent.ToString().StartsWith("Mozilla/", StringComparison.Ordinal);

        /// <summary>
        /// The source a request addresses: the item's own id is the primary source
        /// (what the details page plays); any other id is a hashed alternate.
        /// </summary>
        internal FederatedSource? SelectSource(FederatedCacheEntry entry, Guid itemId, string? sourceId, Guid? userId = null, string? deviceId = null)
        {
            if (string.IsNullOrEmpty(sourceId)
                || (Guid.TryParse(sourceId, out var asGuid) && asGuid == itemId))
            {
                return FederationViewerReporter.BoundSource(entry, itemId, userId, deviceId)
                    ?? _federationManager.GetPlaybackSource(entry);
            }

            return entry.GetSourcesSnapshot().FirstOrDefault(s =>
                string.Equals(FederationMediaSourceProvider.BuildSourceId(s), sourceId, StringComparison.OrdinalIgnoreCase)
                || (Guid.TryParse(sourceId, out var id) && Guid.TryParse(FederationMediaSourceProvider.BuildSourceId(s), out var built) && id == built));
        }

        /// <summary>
        /// One forwarded query value: the media source becomes the friend's item, and
        /// bitrates are held to this friend link's WAN cap (the friend's upload is
        /// what carries the stream). Everything else passes as negotiated here.
        /// </summary>
        internal static string? RewriteQueryValue(string name, string value, Guid remoteItemId, int? capMbps, IEnumerable<KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues>> query)
        {
            if (string.Equals(name, "MediaSourceId", StringComparison.OrdinalIgnoreCase))
            {
                return remoteItemId.ToString("N");
            }

            if (capMbps is not > 0)
            {
                return value;
            }

            var total = (long)capMbps.Value * 950_000L;
            if (string.Equals(name, "MaxStreamingBitrate", StringComparison.OrdinalIgnoreCase)
                && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxStreaming))
            {
                return Math.Min(maxStreaming, total).ToString(CultureInfo.InvariantCulture);
            }

            if (string.Equals(name, "VideoBitrate", StringComparison.OrdinalIgnoreCase)
                && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var video))
            {
                var audio = RemoteMediaPath.QueryLong(query, "AudioBitrate") ?? 192_000;
                var budget = Math.Max(250_000, total - audio);
                return Math.Min(video, budget).ToString(CultureInfo.InvariantCulture);
            }

            return value;
        }

        /// <summary>The cap for a play over the direct link: 80% of the measured link, none when unmeasured or fast.</summary>
        internal static int? LinkCap(double linkMbps)
            => linkMbps is > 0 and < 500 ? Math.Max(1, (int)(linkMbps * 0.8)) : null;

        /// <summary>A failed/pending measurement must not remove the existing Auto cap.</summary>
        internal static int? LinkCap(double linkMbps, int? fallbackMbps)
            => linkMbps > 0 ? LinkCap(linkMbps) : fallbackMbps;

        /// <summary>
        /// Fetches a playlist/subtitle from the friend: the result, or null and
        /// the status to give the player (502 when the friend can't be reached).
        /// </summary>
        internal async Task<(IActionResult? Result, int Status)> FetchAsync(Handoff handoff, bool playlist, string? relayPrefix, CancellationToken cancellationToken, TimeSpan? deadline = null)
        {
            var wait = deadline ?? TimeSpan.FromSeconds(30);
            try
            {
                // Over the direct link when it's up (faster for this server's own
                // fetches too), otherwise, or when the link fails, to the friend's address.
                HttpRequestMessage Make(bool viaLink)
                {
                    var made = new HttpRequestMessage(HttpMethod.Get, viaLink ? DirectLink.DirectLinkRelay.ToLinkUri(handoff.Url) : new Uri(handoff.Url));
                    made.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
                    return made;
                }

                var link = LinkClient(handoff.ServerId);
                HttpResponseMessage? viaLinkResponse = null;
                byte[]? viaLinkBytes = null;
                if (link != null)
                {
                    using var linkRequest = Make(true);
                    using var linkTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    linkTimeout.CancelAfter(wait);
                    try
                    {
                        viaLinkResponse = await link.SendAsync(linkRequest, HttpCompletionOption.ResponseHeadersRead, linkTimeout.Token).ConfigureAwait(false);
                        if (viaLinkResponse.IsSuccessStatusCode)
                        {
                            // Headers arriving is not a successful transfer. Keep the
                            // body under the same deadline and fallback catch.
                            viaLinkBytes = playlist
                                ? await HttpContentReader.ReadBytesAsync(viaLinkResponse.Content, MaxPlaylistBytes, linkTimeout.Token).ConfigureAwait(false)
                                : await viaLinkResponse.Content.ReadAsByteArrayAsync(linkTimeout.Token).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex) when (ex is HttpRequestException or System.IO.IOException or ObjectDisposedException
                        || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
                    {
                        viaLinkResponse?.Dispose();
                        viaLinkResponse = null;
                        viaLinkBytes = null;
                        _logger.LogInformation("[Federation] The direct link to {Server} didn't answer ({Error}); using its address", handoff.ServerName, ex.GetType().Name);
                    }
                }

                using var peerTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                peerTimeout.CancelAfter(wait);
                for (var attempt = 0; ; attempt++)
                {
                    using var request = Make(false);
                    if (attempt > 0)
                    {
                        request.Headers.ConnectionClose = true;
                    }

                    try
                    {
                        using var response = viaLinkResponse ?? await Send(request, peerTimeout.Token).ConfigureAwait(false);
                        if (!response.IsSuccessStatusCode)
                        {
                            return (null, (int)response.StatusCode);
                        }

                        var bodyToken = viaLinkBytes == null ? peerTimeout.Token : cancellationToken;
                        var bytes = viaLinkBytes ?? (playlist
                            ? await HttpContentReader.ReadBytesAsync(response.Content, MaxPlaylistBytes, bodyToken).ConfigureAwait(false)
                            : await response.Content.ReadAsByteArrayAsync(bodyToken).ConfigureAwait(false));
                        var contentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
                        if (playlist)
                        {
                            // The friend's addresses can be http behind its proxy; put
                            // them on the address this server uses for it.
                            var text = HandoffPlaylistLinks.Decode(bytes);
                            var withLevels = handoff.Kind == RemoteMediaKind.HlsMaster
                                && Plugin.Instance?.Configuration?.AdaptiveQualityLadder == true
                                && PeerTranscodeCapability.HasQualityLadder(handoff.ServerId)
                                    ? HlsQualityLadder.Expand(text, handoff.SourceBitrate)
                                    : text;
                            if (!ReferenceEquals(withLevels, text))
                            {
                                _logger.LogInformation("[Federation] Offering lower quality levels for a title {Server} converts, so the player can step down instead of buffering", handoff.ServerName);
                            }

                            bytes = System.Text.Encoding.UTF8.GetBytes(HandoffPlaylistLinks.Rewrite(withLevels, handoff.PlayerBase, handoff.ViaLink ? null : relayPrefix));
                            if (handoff.Kind == RemoteMediaKind.HlsMaster && !handoff.ViaLink)
                            {
                                // Warm only the top level: the others start when a player asks.
                                HandoffPlaylistLinks.WarmUp(text, handoff.PeerBase, PeerTranscodeCapability.HasSharedSegments(handoff.ServerId), _logger);
                            }
                        }

                        return (new FileContentResult(bytes, contentType), StatusCodes.Status200OK);
                    }
                    catch (Exception ex) when (attempt == 0 && viaLinkResponse == null && ex is HttpRequestException or System.IO.IOException)
                    {
                        _logger.LogInformation("[Federation] {Server} handoff connection failed ({Error}); retrying on a fresh connection", handoff.ServerName, ex.GetType().Name);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("[Federation] Could not reach {Server} for a handoff ({Error})", handoff.ServerName, ex.GetType().Name);
                return (null, StatusCodes.Status502BadGateway);
            }
        }

        internal sealed record Handoff(string Url, string ServerId, string ServerName, Guid UserId, string SourceId, string PeerBase, RemoteMediaKind Kind, string PlayerBase, bool ViaLink, long? SourceBitrate = null)
        {
            /// <summary>Gets the copy this handoff plays.</summary>
            public FederatedSource? Source { get; init; }

            /// <summary>Gets the title's cache entry (its other copies).</summary>
            public FederatedCacheEntry? Entry { get; init; }

            /// <summary>Gets the status this friend answered with when it failed to start.</summary>
            public int FailedStatus { get; init; }

            /// <summary>Gets where the player is sent: the friend, or this server's direct link relay.</summary>
            public string PlayerUrl => ViaLink ? HandoffPlaylistLinks.ToPeer(Url, PlayerBase) ?? Url : Url;
        }
    }
}
