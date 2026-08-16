using System;
using System.Linq;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Keeps Jellyfin's own native "Internet streaming bitrate limit"
    /// (<c>ServerConfiguration.RemoteClientBitrateLimit</c>) divided across however
    /// many playback sessions are currently active, instead of a single static
    /// number that either wastes bandwidth (set low, safe for many viewers, wasteful
    /// for one) or oversubscribes the link (set high, fine for one viewer, causes
    /// every stream to stutter once several are active at once) - "user wants a
    /// bitrate the sending server doesn't actually have spare capacity for" is
    /// exactly the stutter this exists to prevent, without this plugin trying to
    /// reimplement bitrate enforcement Jellyfin core already does.
    /// <para>
    /// Deliberately does not attempt to distinguish federation-originated sessions
    /// from local household ones: <see cref="RemoteClientBitrateLimit"/> already only
    /// applies to whatever Jellyfin itself classifies as a remote client, and there is
    /// no way for a plugin to tell "this request came in on a federation API key"
    /// apart from any other remote client via <see cref="SessionInfo"/> today. The
    /// budget is global to the server's own upload, which is also what actually needs
    /// protecting - every concurrent remote stream competes for the same uplink
    /// regardless of who started it.
    /// </para>
    /// </summary>
    public class UploadBudgetService
    {
        // Mirrors WanBandwidthMonitor's own SafetyMargin: leave a little headroom
        // rather than promising 100% of the measured/configured capacity, since
        // real-world throughput never quite matches a link's rated speed.
        private const double SafetyMargin = 0.85;

        // A budget below this is not a usable streaming experience regardless of how
        // many viewers are active - matches WanBandwidthMonitor's own floor.
        private const int MinPerStreamMbps = 2;

        private readonly ILogger<UploadBudgetService> _logger;
        private readonly ISessionManager _sessionManager;
        private readonly IServerConfigurationManager _serverConfigManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="UploadBudgetService"/> class.
        /// </summary>
        public UploadBudgetService(
            ILogger<UploadBudgetService> logger,
            ISessionManager sessionManager,
            IServerConfigurationManager serverConfigManager)
        {
            _logger = logger;
            _sessionManager = sessionManager;
            _serverConfigManager = serverConfigManager;
        }

        /// <summary>
        /// Gets the number of currently active playback sessions on this server, as
        /// far as <see cref="ISessionManager"/> can tell - used both to compute the
        /// budget and surfaced read-only in the admin UI's dashboard.
        /// </summary>
        public int GetActiveSessionCount()
        {
            return _sessionManager.Sessions.Count(s => s.IsActive && s.NowPlayingItem != null);
        }

        /// <summary>
        /// Computes the per-stream Mbps budget for a given active session count,
        /// without touching any configuration - the pure math, kept separate so it is
        /// directly testable and so the admin UI can preview the value live as the
        /// admin edits <see cref="Configuration.PluginConfiguration.LocalUploadCapacityMbps"/>.
        /// </summary>
        public static int ComputePerStreamMbps(int uploadCapacityMbps, int activeSessionCount)
        {
            if (uploadCapacityMbps <= 0)
            {
                return 0;
            }

            var divisor = Math.Max(1, activeSessionCount);
            var target = (int)Math.Round(uploadCapacityMbps * SafetyMargin / divisor);
            return Math.Clamp(target, MinPerStreamMbps, uploadCapacityMbps);
        }

        /// <summary>
        /// Applies the current budget to Jellyfin's own <c>RemoteClientBitrateLimit</c>
        /// when <see cref="Configuration.PluginConfiguration.AutoManageUploadBudget"/>
        /// is on. A no-op (config left untouched) when it's off, so turning the toggle
        /// off simply freezes whatever limit was last applied rather than resetting it -
        /// the admin's own manually-set value, if any, is never overwritten unless they
        /// opted in first. Intended to be called from the same periodic cadence as the
        /// existing federation sync (see <see cref="Tasks.FederationRefreshTask"/>), not
        /// on every individual session change, so it doesn't rewrite Jellyfin's own
        /// config file on every play/stop.
        /// </summary>
        public void ApplyIfDue()
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null || !config.AutoManageUploadBudget)
            {
                return;
            }

            var activeSessions = GetActiveSessionCount();
            var perStreamMbps = ComputePerStreamMbps(config.LocalUploadCapacityMbps, activeSessions);
            var perStreamBps = perStreamMbps * 1_000_000;

            var serverConfig = _serverConfigManager.Configuration;
            if (serverConfig.RemoteClientBitrateLimit == perStreamBps)
            {
                return;
            }

            serverConfig.RemoteClientBitrateLimit = perStreamBps;
            _serverConfigManager.SaveConfiguration();
            _logger.LogInformation(
                "[Federation] Upload budget: {Sessions} active session(s), capacity {Capacity} Mbps -> {PerStream} Mbps per stream",
                activeSessions,
                config.LocalUploadCapacityMbps,
                perStreamMbps);
        }
    }
}
