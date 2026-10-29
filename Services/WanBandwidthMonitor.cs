using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Decides whether a Direct-mode server's stream should be capped, and to what
    /// bitrate, implementing "direct play whenever possible, only transcode down when
    /// there's real evidence the link can't sustain the raw file" - never a blind
    /// guess. Two independent judgments, both refreshed periodically in the
    /// background so <see cref="GetEffectiveCapMbps"/> itself is a fast, synchronous,
    /// no-network-call lookup safe to call from hot paths like
    /// <see cref="FederationLibraryManager.BuildPlaybackUrl"/>:
    ///
    /// 1. Is this server reachable only over the internet, or the same local network?
    ///    Same network (or not yet known either way) always means uncapped - the raw
    ///    file, best quality, no extra transcode cost on either end. A cap only ever
    ///    applies once this has positively confirmed the link is a WAN one.
    /// 2. For a confirmed WAN link, what can it actually sustain? Repeated steady-state
    ///    samples over the real media route are combined into a robust measurement (see
    ///    <see cref="RemoteServerClient.MeasureBandwidthMbpsAsync"/>). A compatibility
    ///    bitrate-test endpoint is used only when media sampling is unavailable.
    ///
    /// A Manual cap also applies to proxied/non-Jellyfin (Plex) peers. Over-cap
    /// sources require real conversion and the cap is applied to the converted output,
    /// never by starving the original input. Auto has no meaning there (see
    /// <see cref="GetEffectiveCapMbps"/>), since there's no bandwidth probe to run.
    /// </summary>
    public class WanBandwidthMonitor
    {
        // A confirmed-WAN server whose bandwidth has not been measured yet gets this
        // conservative placeholder for the (usually brief) window before the first
        // probe completes - better than accidentally pulling a 25+ Mbps raw file
        // across a link already known to be a WAN one.
        private const int PendingMeasurementCapMbps = 10;

        // Failed-probe count used for the admin-facing measurement warning.
        private const int MaxPendingFailures = 3;

        // A speed reading this young is reused instead of probing the same friend again.
        private static readonly TimeSpan SampleReuseWindow = TimeSpan.FromMinutes(10);

        // Reserve part of the measured link for variation, audio and protocol overhead.
        private const double SafetyMargin = 0.85;

        private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(20);

        // The link-speed sample behind the loading-time estimate. Independent of the
        // WAN cap above: it is measured for every enabled server (including Plex and
        // servers whose cap is off) and only needs to be roughly current, so it is a
        // daily sample plus a fresh one whenever a server appears or comes back online.
        private static readonly TimeSpan LinkMeasurementInterval = TimeSpan.FromHours(24);

        // After a failed sample, try again soon instead of waiting a whole day.
        private static readonly TimeSpan LinkMeasurementRetryInterval = TimeSpan.FromMinutes(30);

        private readonly ILogger<WanBandwidthMonitor> _logger;
        private readonly IRemoteServerClientFactory _clientFactory;
        private readonly ExternalCatalogRegistry? _externalCatalogs;
        private readonly FederationItemCache? _itemCache;
        private readonly ConcurrentDictionary<string, ServerNetworkInfo> _cache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Initializes a new instance of the <see cref="WanBandwidthMonitor"/> class.
        /// </summary>
        public WanBandwidthMonitor(
            ILogger<WanBandwidthMonitor> logger,
            IRemoteServerClientFactory clientFactory,
            ExternalCatalogRegistry? externalCatalogs = null,
            FederationItemCache? itemCache = null)
        {
            _logger = logger;
            _clientFactory = clientFactory;
            _externalCatalogs = externalCatalogs;
            _itemCache = itemCache;
        }

        /// <summary>
        /// The most recent measured download speed from a server in Mbps, or null when
        /// it has not been measured yet. Fast, synchronous and network-free.
        /// </summary>
        /// <param name="serverId">The server's configured id.</param>
        /// <returns>Megabits per second, or null.</returns>
        public double? GetMeasuredLinkMbps(string serverId)
            => _cache.TryGetValue(serverId, out var info) ? info.LinkMbps : null;

        /// <summary>
        /// The bitrate that will actually cross the link for a source: the file's own, or
        /// the Direct-mode cap when the peer is asked to transcode it down to one.
        /// </summary>
        /// <param name="server">The source server.</param>
        /// <param name="sourceBitrateBps">The file's bitrate in bits per second, if known.</param>
        /// <returns>Bits per second, or null when unknown.</returns>
        public long? EffectiveSourceBitrate(RemoteServer server, long? sourceBitrateBps)
        {
            if (server.StreamingMode == StreamingMode.Direct
                && GetEffectiveCapMbps(server) is { } cap
                && (sourceBitrateBps is not > 0 || sourceBitrateBps.Value > cap * 1_000_000L))
            {
                return cap * 1_000_000L;
            }

            return sourceBitrateBps is > 0 ? sourceBitrateBps : null;
        }

        /// <summary>
        /// Projected seconds from pressing play to video for a source on this server, or
        /// null when the connection speed or the bitrate is not known yet.
        /// </summary>
        /// <param name="server">The source server.</param>
        /// <param name="sourceBitrateBps">The file's bitrate in bits per second, if known.</param>
        /// <returns>Seconds, or null.</returns>
        public double? ProjectedStartSeconds(RemoteServer server, long? sourceBitrateBps)
            => StartEstimator.ProjectedSeconds(EffectiveSourceBitrate(server, sourceBitrateBps), GetMeasuredLinkMbps(server.Id));

        /// <summary>
        /// Marks a server's speed sample stale so the next call to
        /// <see cref="MeasureLinkIfDueAsync"/> measures again - used when a server comes
        /// back online, since its old reading may describe a different connection.
        /// </summary>
        /// <param name="serverId">The server's configured id.</param>
        public void InvalidateMeasurement(string serverId)
        {
            if (_cache.TryGetValue(serverId, out var info))
            {
                info.LinkNextDueUtc = DateTime.MinValue;
            }
        }

        /// <summary>
        /// Measures a server's real download speed if the last sample is older than a
        /// day (or it has never been sampled, or was just invalidated). Never throws;
        /// at most one sample per server runs at a time; a failure keeps the previous
        /// reading and retries in 30 minutes.
        /// </summary>
        /// <param name="server">The server to sample.</param>
        /// <param name="cancellationToken">Cancellation.</param>
        /// <returns>A task that completes when the sample has been taken or skipped.</returns>
        public async Task MeasureLinkIfDueAsync(RemoteServer server, CancellationToken cancellationToken = default)
        {
            if (!server.Enabled || FederationItemPersistenceService.AvailabilityOverride?.IsOffline(server.Id) == true)
            {
                return;
            }

            var info = _cache.GetOrAdd(server.Id, _ => new ServerNetworkInfo());
            if (DateTime.UtcNow < info.LinkNextDueUtc || Interlocked.Exchange(ref info.LinkMeasuring, 1) == 1)
            {
                return;
            }

            double? measured = null;
            try
            {
                if (info.MeasuredMbps is > 0 && DateTime.UtcNow - info.MeasuredAtUtc < SampleReuseWindow)
                {
                    measured = info.MeasuredMbps;
                }
                else if (_externalCatalogs?.For(server) is { } provider)
                {
                    var nativeId = FindImportedNativeId(server);
                    if (nativeId != null)
                    {
                        measured = await provider.MeasureBandwidthMbpsAsync(server, nativeId, cancellationToken).ConfigureAwait(false);
                    }
                }
                else if (server.Kind == ServerKind.Jellyfin)
                {
                    var client = _clientFactory.GetClient(server.Id);
                    if (client != null)
                    {
                        measured = await client.MeasureBandwidthMbpsAsync(cancellationToken, FindSampleVideoId(server)).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[Federation] Link-speed sample for {ServerName} failed", server.Name);
            }
            finally
            {
                if (measured is > 0)
                {
                    info.LinkMbps = measured;
                    info.LinkSampledUtc = DateTime.UtcNow;
                    info.LinkNextDueUtc = DateTime.UtcNow + LinkMeasurementInterval;
                    _logger.LogInformation("[Federation] Link speed to {ServerName} measured at {Mbps:F1} Mbps", server.Name, measured.Value);
                }
                else
                {
                    info.LinkNextDueUtc = DateTime.UtcNow + LinkMeasurementRetryInterval;
                }

                Interlocked.Exchange(ref info.LinkMeasuring, 0);
            }
        }

        // Any title this server already shares with us - the probe only ever reads a
        // file that is part of the consented catalog.
        private string? FindImportedNativeId(RemoteServer server)
        {
            if (_itemCache == null)
            {
                return null;
            }

            foreach (var entry in _itemCache.GetAllEntries())
            {
                foreach (var source in entry.GetSourcesSnapshot())
                {
                    if (source.ServerId == server.Id && entry.GetNativeId(source) is { Length: > 0 } nativeId)
                    {
                        return nativeId;
                    }
                }
            }

            return null;
        }

        // A shared video on a Jellyfin peer to time the link on (see
        // RemoteServerClient.MeasureBandwidthMbpsAsync). The highest-bitrate one,
        // so the 10 MB sample is a small part of a real file.
        private Guid? FindSampleVideoId(RemoteServer server)
        {
            if (_itemCache == null)
            {
                return null;
            }

            FederatedSource? best = null;
            foreach (var entry in _itemCache.GetAllEntries())
            {
                if (entry.ItemType is not ("Movie" or "Episode"))
                {
                    continue;
                }

                foreach (var source in entry.GetSourcesSnapshot())
                {
                    if (source.ServerId == server.Id && source.RemoteItemId != Guid.Empty
                        && (best == null || source.Bitrate.GetValueOrDefault() > best.Bitrate.GetValueOrDefault()))
                    {
                        best = source;
                    }
                }
            }

            return best?.RemoteItemId;
        }

        /// <summary>
        /// Fast, synchronous, network-free lookup of the bitrate cap that should
        /// currently apply to a server's Direct-mode streams, or null for "stream the
        /// raw file, no cap" - the default and always-safe answer.
        /// </summary>
        public int? GetEffectiveCapMbps(RemoteServer server)
        {
            switch (server.WanCapMode)
            {
                case WanCapMode.Off:
                    return null;

                case WanCapMode.Manual:
                    return server.WanMaxBitrateMbps > 0 ? server.WanMaxBitrateMbps : null;

                case WanCapMode.Auto:
                default:
                    // Auto's measurement half relies on a bandwidth probe endpoint
                    // (this plugin's own Peer/BitrateTest, or Jellyfin's native
                    // Playback/BitrateTest - see MeasureBandwidthMbpsAsync) that only
                    // exists on a Jellyfin peer. A non-Jellyfin server (Plex) can
                    // never get a MeasuredMbps value, which would otherwise mean
                    // settling permanently on PendingMeasurementCapMbps instead of
                    // the "still waiting to find out" placeholder it's meant to be -
                    // worse than today's uncapped behavior, not better. Manual (an
                    // explicit value the user sets themselves) is the only cap mode
                    // that makes sense for a non-Jellyfin peer.
                    if (server.Kind != ServerKind.Jellyfin)
                    {
                        return null;
                    }

                    if (!_cache.TryGetValue(server.Id, out var info) || info.IsLocalNetwork != false)
                    {
                        // Not classified yet, or positively confirmed same-network:
                        // direct play. A cap is never applied speculatively.
                        return null;
                    }

                    if (!info.MeasuredMbps.HasValue)
                    {
                        return PendingMeasurementCapMbps;
                    }

                    return AutoCapFromMeasured(info.MeasuredMbps.Value);
            }
        }

        /// <summary>
        /// Cap for a measured WAN speed: 85% of its sustained throughput, without
        /// discarding capacity on fast links or exceeding capacity on slow links.
        /// </summary>
        internal static int AutoCapFromMeasured(double measuredMbps)
        {
            var target = (int)Math.Floor(measuredMbps * SafetyMargin);
            return Math.Max(1, Math.Min(target, (int)Math.Floor(measuredMbps)));
        }

        /// <summary>
        /// Short admin-facing note on the WAN measurement for a server, or null when
        /// there is nothing worth saying (not Auto, same-network, or measured fine).
        /// </summary>
        /// <param name="server">The server.</param>
        /// <returns>Text, or null.</returns>
        public string? GetWanNote(RemoteServer server)
        {
            if (server.WanCapMode != WanCapMode.Auto || server.Kind != ServerKind.Jellyfin
                || !_cache.TryGetValue(server.Id, out var info) || info.IsLocalNetwork != false || info.MeasuredMbps.HasValue)
            {
                return null;
            }

            return info.FailedProbes >= MaxPendingFailures
                ? "Never measured (probe keeps failing); conservative " + PendingMeasurementCapMbps + " Mbps cap"
                : "Not measured yet; temporary " + PendingMeasurementCapMbps + " Mbps cap";
        }

        /// <summary>
        /// True only when this server has been positively confirmed as same-network.
        /// Used by <see cref="FederationLibraryManager"/> to decide whether a static
        /// <c>item.Path</c> is safe to stamp once at item-creation time: it is, for a
        /// confirmed-LAN server, since that classification is for practical purposes
        /// permanent (no bandwidth measurement to ever go stale) - but not yet
        /// classified and confirmed-WAN both still mean "could start needing a cap
        /// later", exactly the staleness this method exists to avoid freezing in.
        /// </summary>
        public bool IsConfirmedLocalNetwork(RemoteServer server)
        {
            return _cache.TryGetValue(server.Id, out var info) && info.IsLocalNetwork == true;
        }

        /// <summary>
        /// Refreshes this server's network classification and bandwidth measurement if
        /// due (rate-limited internally to <see cref="RefreshInterval"/>). Intended to
        /// be called from the regular background sync cycle for every enabled
        /// Direct-mode server; safe to call as often as convenient and never throws.
        /// </summary>
        public async Task RefreshIfDueAsync(RemoteServer server, CancellationToken cancellationToken = default)
        {
            // Auto is a no-op for a non-Jellyfin server (see GetEffectiveCapMbps) -
            // skip the classification/measurement work entirely rather than issuing
            // a probe request every cycle that can only ever fail (a Plex server has
            // no BitrateTest endpoint to hit).
            if (server.WanCapMode != WanCapMode.Auto || !server.Enabled || server.Kind != ServerKind.Jellyfin)
            {
                if (server.Enabled)
                {
                    _logger.LogDebug("[Federation] WAN bandwidth check skipped for {ServerName}: cap mode is {Mode} ({Kind})", server.Name, server.WanCapMode, server.Kind);
                }

                return;
            }

            var info = _cache.GetOrAdd(server.Id, _ => new ServerNetworkInfo());
            if (DateTime.UtcNow - info.LastChecked < RefreshInterval || Interlocked.Exchange(ref info.Refreshing, 1) == 1)
            {
                return;
            }

            try
            {
                // A DNS failure (null) must not wipe a confirmed WAN/LAN answer.
                var classified = await ClassifyAsync(server.Url).ConfigureAwait(false);
                if (classified.HasValue)
                {
                    if (info.IsLocalNetwork != classified)
                    {
                        _logger.LogInformation("[Federation] {ServerName} classified as {Class}", server.Name, classified.Value ? "same-network (no WAN cap or bandwidth probe)" : "WAN");
                    }

                    info.IsLocalNetwork = classified;
                }
                else
                {
                    _logger.LogInformation("[Federation] Could not classify {ServerName} (DNS failed); keeping {Previous}", server.Name, info.IsLocalNetwork?.ToString() ?? "unclassified");
                }

                if (info.IsLocalNetwork == false)
                {
                    var client = _clientFactory.GetClient(server.Id);
                    if (client != null)
                    {
                        // Reuse the link-speed sample if one was just taken: same
                        // friend, same path, no need for a second 10 MB download.
                        var recentLink = info.LinkMbps is > 0 && DateTime.UtcNow - info.LinkSampledUtc < SampleReuseWindow
                            ? info.LinkMbps
                            : null;
                        var measured = recentLink
                            ?? await client.MeasureBandwidthMbpsAsync(cancellationToken, FindSampleVideoId(server)).ConfigureAwait(false);
                        if (measured.HasValue)
                        {
                            info.MeasuredMbps = measured;
                            info.MeasuredAtUtc = DateTime.UtcNow;
                            info.FailedProbes = 0;
                            if (recentLink == null)
                            {
                                info.LinkMbps = measured;
                                info.LinkSampledUtc = info.MeasuredAtUtc;
                            }

                            _logger.LogInformation(
                                "[Federation] WAN bandwidth to {ServerName} measured at {Mbps:F1} Mbps",
                                server.Name,
                                measured.Value);
                        }
                        else if (++info.FailedProbes == MaxPendingFailures && !info.MeasuredMbps.HasValue)
                        {
                            _logger.LogWarning(
                                "[Federation] WAN bandwidth to {ServerName} has never been measured after {Count} attempts; its temporary cap is lifted until a probe succeeds",
                                server.Name,
                                MaxPendingFailures);
                        }
                    }
                }
                else
                {
                    _logger.LogDebug("[Federation] {ServerName} classified as same-network; Direct mode stays uncapped", server.Name);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Federation] WAN auto-detection failed for {ServerName}; keeping previous values", server.Name);
            }
            finally
            {
                // Backs off on failure too, so a server that is briefly unreachable
                // doesn't get hammered with a probe attempt on every sync cycle.
                info.LastChecked = DateTime.UtcNow;
                Interlocked.Exchange(ref info.Refreshing, 0);
            }
        }

        /// <summary>
        /// Resolves a server's hostname and checks whether every address it resolves
        /// to is a private/local one. Null means "couldn't tell" (DNS failure, etc.) -
        /// treated the same as same-network by <see cref="GetEffectiveCapMbps"/>,
        /// since guessing WAN and capping unnecessarily is a worse failure mode than
        /// staying uncapped a bit longer.
        /// </summary>
        private static async Task<bool?> ClassifyAsync(string url)
        {
            try
            {
                var host = new Uri(url).Host;
                if (IPAddress.TryParse(host, out var literal))
                {
                    return IsPrivateAddress(literal);
                }

                // An unresponsive DNS resolver has no built-in bound on how long this
                // can hang otherwise - same stalling risk as the unbounded HTTP
                // timeout fixed in MeasureBandwidthMbpsAsync, and for the same reason
                // (this runs at the start of every sync cycle), it needs its own
                // short, explicit one instead.
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                var addresses = await Dns.GetHostAddressesAsync(host, timeoutCts.Token).ConfigureAwait(false);
                return addresses.Length == 0 ? null : addresses.All(IsPrivateAddress);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool IsPrivateAddress(IPAddress ip)
        {
            if (IPAddress.IsLoopback(ip))
            {
                return true;
            }

            var bytes = ip.GetAddressBytes();
            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                return bytes[0] == 10 // 10.0.0.0/8
                    || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) // 172.16.0.0/12
                    || (bytes[0] == 192 && bytes[1] == 168) // 192.168.0.0/16
                    || (bytes[0] == 169 && bytes[1] == 254); // 169.254.0.0/16 link-local
            }

            if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            {
                return (bytes[0] & 0xFE) == 0xFC // fc00::/7 unique local
                    || (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80); // fe80::/10 link-local
            }

            return false;
        }

        /// <summary>
        /// Drops any cached classification/measurement for a server - called when a
        /// server is removed, so a stale entry does not linger in memory forever
        /// keyed by an id nothing references anymore.
        /// </summary>
        public void RemoveServer(string serverId)
        {
            _cache.TryRemove(serverId, out _);
        }

        /// <summary>
        /// Test-only seam: directly populates the cache so decision logic
        /// (<see cref="GetEffectiveCapMbps"/>) can be unit tested without performing
        /// real DNS lookups or network calls. Internal, gated by
        /// <c>InternalsVisibleTo</c>.
        /// </summary>
        internal void SeedForTests(string serverId, bool? isLocalNetwork, double? measuredMbps, int failedProbes = 0)
        {
            _cache[serverId] = new ServerNetworkInfo
            {
                IsLocalNetwork = isLocalNetwork,
                MeasuredMbps = measuredMbps,
                FailedProbes = failedProbes,
                LastChecked = DateTime.UtcNow
            };
        }

        /// <summary>Test-only seam: makes the next refresh run.</summary>
        internal void ExpireForTests(string serverId)
        {
            if (_cache.TryGetValue(serverId, out var info))
            {
                info.LastChecked = DateTime.MinValue;
            }
        }

        /// <summary>Test-only seam: records a link-speed reading without any network call.</summary>
        internal void SeedLinkForTests(string serverId, double mbps)
        {
            var info = _cache.GetOrAdd(serverId, _ => new ServerNetworkInfo());
            info.LinkMbps = mbps;
            info.LinkNextDueUtc = DateTime.UtcNow + LinkMeasurementInterval;
        }

        private class ServerNetworkInfo
        {
            public bool? IsLocalNetwork { get; set; }

            public double? MeasuredMbps { get; set; }

            public DateTime LastChecked { get; set; }

            public DateTime MeasuredAtUtc { get; set; }

            public DateTime LinkSampledUtc { get; set; }

            public int FailedProbes { get; set; }

            public double? LinkMbps { get; set; }

            public DateTime LinkNextDueUtc { get; set; } = DateTime.MinValue;

            // Fields, not properties: Interlocked needs a ref.
            public int LinkMeasuring;
            public int Refreshing;
        }
    }
}
