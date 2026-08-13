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
    /// 2. For a confirmed WAN link, what can it actually sustain? Measured via the
    ///    remote's own <c>/Playback/BitrateTest</c> endpoint (see
    ///    <see cref="RemoteServerClient.MeasureBandwidthMbpsAsync"/>) - the same
    ///    mechanism jellyfin-web itself uses client-side. If that measurement is
    ///    generously above what any real source is likely to need, the cap is skipped
    ///    entirely rather than pointlessly capping a connection that didn't need it.
    /// </summary>
    public class WanBandwidthMonitor
    {
        // Above this, capping would not help - no realistic source exceeds it by
        // enough to matter, so direct play is left alone rather than forcing a
        // pointless second transcode pass.
        private const double UncappedThresholdMbps = 50.0;

        // A confirmed-WAN server whose bandwidth has not been measured yet gets this
        // conservative placeholder for the (usually brief) window before the first
        // probe completes - better than accidentally pulling a 25+ Mbps raw file
        // across a link already known to be a WAN one.
        private const int PendingMeasurementCapMbps = 10;

        private const int MinAutoCapMbps = 4;
        private const int MaxAutoCapMbps = 45;
        private const double SafetyMargin = 0.75;

        private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(20);

        private readonly ILogger<WanBandwidthMonitor> _logger;
        private readonly IRemoteServerClientFactory _clientFactory;
        private readonly ConcurrentDictionary<string, ServerNetworkInfo> _cache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Initializes a new instance of the <see cref="WanBandwidthMonitor"/> class.
        /// </summary>
        public WanBandwidthMonitor(ILogger<WanBandwidthMonitor> logger, IRemoteServerClientFactory clientFactory)
        {
            _logger = logger;
            _clientFactory = clientFactory;
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

                    if (info.MeasuredMbps.Value >= UncappedThresholdMbps)
                    {
                        return null;
                    }

                    var target = (int)Math.Round(info.MeasuredMbps.Value * SafetyMargin);
                    return Math.Clamp(target, MinAutoCapMbps, MaxAutoCapMbps);
            }
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
            if (server.WanCapMode != WanCapMode.Auto || !server.Enabled)
            {
                return;
            }

            var info = _cache.GetOrAdd(server.Id, _ => new ServerNetworkInfo());
            if (DateTime.UtcNow - info.LastChecked < RefreshInterval)
            {
                return;
            }

            try
            {
                info.IsLocalNetwork = await ClassifyAsync(server.Url).ConfigureAwait(false);

                if (info.IsLocalNetwork == false)
                {
                    var client = _clientFactory.GetClient(server.Id);
                    if (client != null)
                    {
                        var measured = await client.MeasureBandwidthMbpsAsync(cancellationToken).ConfigureAwait(false);
                        if (measured.HasValue)
                        {
                            info.MeasuredMbps = measured;
                            _logger.LogInformation(
                                "[Federation] WAN bandwidth to {ServerName} measured at {Mbps:F1} Mbps",
                                server.Name,
                                measured.Value);
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

                var addresses = await Dns.GetHostAddressesAsync(host).ConfigureAwait(false);
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
        internal void SeedForTests(string serverId, bool? isLocalNetwork, double? measuredMbps)
        {
            _cache[serverId] = new ServerNetworkInfo
            {
                IsLocalNetwork = isLocalNetwork,
                MeasuredMbps = measuredMbps,
                LastChecked = DateTime.UtcNow
            };
        }

        private class ServerNetworkInfo
        {
            public bool? IsLocalNetwork { get; set; }

            public double? MeasuredMbps { get; set; }

            public DateTime LastChecked { get; set; }
        }
    }
}
