using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Federation.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// What to answer a viewer's Download of a friend's title with (see
    /// <see cref="FederationDeviceDownloadService.PlanAsync"/>).
    /// </summary>
    /// <param name="Status">200 to go ahead; otherwise the HTTP status to answer with.</param>
    /// <param name="Error">Why it can't be downloaded, safe to show the viewer.</param>
    /// <param name="RetryAfterSeconds">When the friend's download limit frees up.</param>
    /// <param name="Route">Redirect to the friend or relay through this server.</param>
    /// <param name="Url">The friend's item-scoped download address (carries a token: never log it).</param>
    /// <param name="FileName">The name to save the file under when relaying.</param>
    /// <param name="ServerName">The friend the file comes from.</param>
    /// <param name="RelayReason">Why the file is relayed instead of sent direct (null when direct).</param>
    public sealed record DeviceDownloadPlan(
        int Status,
        string? Error,
        int? RetryAfterSeconds,
        DeviceDownloadRoute Route,
        string? Url,
        string? FileName,
        string? ServerName,
        string? RelayReason = null)
    {
        /// <summary>A refusal.</summary>
        public static DeviceDownloadPlan Refused(int status, string error, int? retryAfterSeconds = null)
            => new(status, error, retryAfterSeconds, DeviceDownloadRoute.Relay, null, null, null);
    }

    /// <summary>
    /// Lets any viewer save a friend's title to their own device with their app's
    /// ordinary Download button. Jellyfin itself only offers Download for local
    /// files, so this decides who may download a federated title (shown as
    /// <c>CanDownload</c> by <see cref="FederationCanDownloadFilter"/>) and, when
    /// they press it, gets a download permit for that viewer from the friend that
    /// owns the file (see <see cref="FederationDeviceDownloadFilter"/>).
    /// </summary>
    public sealed class FederationDeviceDownloadService
    {
        private readonly FederationLibraryManager _federation;
        private readonly IRemoteServerClientFactory _clients;
        private readonly ILogger<FederationDeviceDownloadService> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="FederationDeviceDownloadService"/> class.
        /// </summary>
        public FederationDeviceDownloadService(
            FederationLibraryManager federation,
            IRemoteServerClientFactory clients,
            ILogger<FederationDeviceDownloadService> logger)
        {
            _federation = federation;
            _clients = clients;
            _logger = logger;
        }

        /// <summary>
        /// Whether the Download button should be offered to <paramref name="user"/>
        /// for this federated title: they may download in Jellyfin, it's a video or
        /// audio file, and at least one friend that has it lets them download it.
        /// Offline friends still count (the Play button stays too); pressing
        /// Download then explains the friend is offline.
        /// </summary>
        public bool CanOffer(User user, BaseItem item)
        {
            if (!user.HasPermission(PermissionKind.EnableContentDownloading) || !IsDownloadableKind(item))
            {
                return false;
            }

            var entry = Entry(item);
            return entry != null && Candidates(entry, user.Id, includeOffline: true).Any();
        }

        /// <summary>
        /// Gets a download permit for <paramref name="user"/> from a friend that has
        /// <paramref name="item"/>, trying the source this server plays from first.
        /// The caller has already checked the viewer may see the item.
        /// </summary>
        public async Task<DeviceDownloadPlan> PlanAsync(User user, BaseItem item, string requestScheme, CancellationToken cancellationToken)
        {
            if (!user.HasPermission(PermissionKind.EnableContentDownloading))
            {
                return DeviceDownloadPlan.Refused(403, "Downloading is turned off for your account.");
            }

            if (!IsDownloadableKind(item))
            {
                return DeviceDownloadPlan.Refused(400, "Only videos and music can be downloaded.");
            }

            var entry = Entry(item);
            if (entry == null)
            {
                return DeviceDownloadPlan.Refused(404, "This title is no longer shared with this server.");
            }

            var candidates = Candidates(entry, user.Id, includeOffline: false).ToList();
            if (candidates.Count == 0)
            {
                return Candidates(entry, user.Id, includeOffline: true).Any()
                    ? DeviceDownloadPlan.Refused(503, "The friend that has this title is offline right now. Try again later.")
                    : DeviceDownloadPlan.Refused(403, "This friend doesn't allow downloading this title.");
            }

            DeviceDownloadPlan? refusal = null;
            foreach (var (server, source) in candidates)
            {
                var client = _clients.GetClient(server);
                if (client == null)
                {
                    continue;
                }

                var permit = await GetPermitAsync(client, server, source, user, cancellationToken).ConfigureAwait(false);
                if (permit.Token == null)
                {
                    // Keep the first friend's reason; another friend may still allow it.
                    refusal ??= DeviceDownloadPlan.Refused(permit.Status, permit.Error ?? "This download isn't allowed.", permit.RetryAfterSeconds);
                    continue;
                }

                var friendBase = RemoteServerClientFactory.EffectiveUrl(server);
                var audio = string.Equals(entry.ItemType, "Audio", StringComparison.Ordinal);

                // A friend behind a proxy may only be configured by an http address
                // while reachable on https; only ask when an https app needs it.
                string? httpsBase = null;
                if (!server.Paired && permit.FriendNamesFile && string.Equals(requestScheme, "https", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var candidate in DeviceDownload.HttpsCandidates(friendBase))
                    {
                        if (await HttpsReachableAsync(server, candidate, cancellationToken).ConfigureAwait(false))
                        {
                            httpsBase = candidate;
                            break;
                        }
                    }
                }

                var (route, baseUrl, reason) = server.Paired
                    ? (DeviceDownloadRoute.Relay, friendBase, "the friend is paired and has no address the app can reach")
                    : DeviceDownload.Decide(permit.FriendNamesFile, friendBase, requestScheme, httpsBase);
                return new DeviceDownloadPlan(
                    200,
                    null,
                    null,
                    route,
                    DeviceDownload.FriendDownloadUrl(baseUrl, source.RemoteItemId, permit.Token, audio),
                    LocalFileName(item, source.Container ?? entry.Metadata.Container, audio),
                    server.Name,
                    reason);
            }

            return refusal ?? DeviceDownloadPlan.Refused(502, "The friend that has this title couldn't be reached.");
        }

        /// <summary>Checks an https address of a friend; replaceable in tests.</summary>
        internal Func<string, CancellationToken, Task<bool>> HttpsProbe { get; set; } = ProbeHttpsAsync;

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (bool Ok, DateTime AtUtc)> HttpsSeen = new(StringComparer.OrdinalIgnoreCase);

        private static readonly System.Net.Http.HttpClient ProbeClient = new(FederationHttp.Stamp(new System.Net.Http.SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, ConnectCallback = FederationHttp.ConnectAsync }))
        {
            Timeout = TimeSpan.FromSeconds(5)
        };

        private async Task<bool> HttpsReachableAsync(RemoteServer server, string httpsBase, CancellationToken cancellationToken)
        {
            // Remembered for a day when it works, ten minutes when it doesn't.
            if (HttpsSeen.TryGetValue(httpsBase, out var seen)
                && DateTime.UtcNow - seen.AtUtc < (seen.Ok ? TimeSpan.FromHours(24) : TimeSpan.FromMinutes(10)))
            {
                return seen.Ok;
            }

            var ok = false;
            try
            {
                ok = await HttpsProbe(httpsBase, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "[Federation] https check for {Server} failed", server.Name);
            }

            HttpsSeen[httpsBase] = (ok, DateTime.UtcNow);
            return ok;
        }

        private static async Task<bool> ProbeHttpsAsync(string httpsBase, CancellationToken cancellationToken)
        {
            using var response = await ProbeClient.GetAsync(httpsBase + "/System/Info/Public", System.Net.Http.HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }

        /// <summary>Only playable files can be saved: not folders, shows or seasons.</summary>
        internal static bool IsDownloadableKind(BaseItem item)
            => !item.IsFolder && item.MediaType is Jellyfin.Data.Enums.MediaType.Video or Jellyfin.Data.Enums.MediaType.Audio;

        /// <summary>
        /// Whether a friend lets <paramref name="userId"/> download one of its titles:
        /// the friend is enabled and a Jellyfin friend, it didn't turn downloads off
        /// for this server (unknown counts as allowed: an older friend decides
        /// itself), its rule for this viewer doesn't turn downloads off, and that
        /// rule lets them see the title at all.
        /// </summary>
        internal static bool FriendAllows(RemoteServer server, Guid userId, string? mappingName, Guid remoteItemId, FederationItemCache? cache)
        {
            if (!server.Enabled || server.Kind != ServerKind.Jellyfin || server.PeerAllowsDownloads == false)
            {
                return false;
            }

            if (RemoteAccessControlService.FindRule(server, userId) is { AllowDownload: false })
            {
                return false;
            }

            return RemoteAccessControlService.Evaluate(server, userId, mappingName, remoteItemId, cache, out _);
        }

        /// <summary>
        /// The name a relayed file is saved under: "Show - S01E02 - Title.mkv" for
        /// an episode, "Title (Year).mkv" otherwise, using the friend's container.
        /// </summary>
        internal static string LocalFileName(BaseItem item, string? container, bool audio)
        {
            var fallback = audio ? "mp3" : "mkv";
            if (item is Episode episode && !string.IsNullOrWhiteSpace(episode.SeriesName))
            {
                var code = episode.ParentIndexNumber.HasValue && episode.IndexNumber.HasValue
                    ? $" - S{episode.ParentIndexNumber.Value:00}E{episode.IndexNumber.Value:00}"
                    : string.Empty;
                return DeviceDownload.TitleFileName(episode.SeriesName + code + " - " + episode.Name, null, container, fallback);
            }

            return DeviceDownload.TitleFileName(item.Name, item.ProductionYear, container, fallback);
        }

        /// <summary>
        /// Asks the friend for a fresh permit on every press: a friend that has
        /// since turned downloads off then answers with its reason instead of the
        /// app being sent off with an old permit. Asking again for a title already
        /// counted doesn't use up any of the viewer's allowance.
        /// </summary>
        private async Task<DeviceDownloadToken> GetPermitAsync(
            RemoteServerClient client,
            RemoteServer server,
            FederatedSource source,
            User user,
            CancellationToken cancellationToken)
        {
            var permit = await client.GetDeviceDownloadTokenAsync(
                source.RemoteItemId.ToString("N"),
                user.Id.ToString("N"),
                user.Username,
                cancellationToken).ConfigureAwait(false);
            if (permit.Token == null)
            {
                _logger.LogInformation(
                    "[Federation] {Server} refused a download of {ItemId} for {User}: {Reason}",
                    server.Name,
                    source.RemoteItemId,
                    user.Username,
                    permit.Error);
            }

            return permit;
        }

        private FederatedCacheEntry? Entry(BaseItem item)
        {
            var key = FederationLibraryManager.GetFederationKey(item);
            return key == null ? null : _federation.Cache.GetEntryByKey(key);
        }

        /// <summary>
        /// The friends this viewer may download the title from: the source this
        /// server plays from first, then the rest in priority order.
        /// </summary>
        private IEnumerable<(RemoteServer Server, FederatedSource Source)> Candidates(FederatedCacheEntry entry, Guid userId, bool includeOffline)
        {
            var preferred = _federation.GetPlaybackSource(entry);
            var ordered = entry.GetSourcesSnapshot()
                .OrderBy(s => preferred != null && s.ServerId == preferred.ServerId && s.RemoteItemId == preferred.RemoteItemId ? 0 : 1)
                .ThenBy(s => s.Priority);
            foreach (var source in ordered)
            {
                var server = _federation.GetServer(source.ServerId);
                if (server == null
                    || (!includeOffline && FederationItemPersistenceService.AvailabilityOverride?.IsOffline(server.Id) == true)
                    || !FriendAllows(server, userId, entry.MappingName, source.RemoteItemId, _federation.Cache))
                {
                    continue;
                }

                yield return (server, source);
            }
        }
    }
}
