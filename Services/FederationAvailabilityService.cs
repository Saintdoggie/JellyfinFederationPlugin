using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Reachability state for one friend server, as last observed by the
    /// background availability pinger (see <see cref="FederationAvailabilityService"/>).
    /// </summary>
    public enum ServerReachability
    {
        /// <summary>Never probed yet (e.g. just added, or plugin just started).</summary>
        Unknown = 0,

        /// <summary>Last probe succeeded.</summary>
        Online = 1,

        /// <summary>Last probe failed; the server may be down or unreachable.</summary>
        Offline = 2
    }

    /// <summary>
    /// One server's current availability snapshot, served to the config page and
    /// consulted by reconciliation when deciding whether to hide items.
    /// </summary>
    public sealed class ServerAvailability
    {
        public ServerReachability Reachability { get; set; } = ServerReachability.Unknown;

        public DateTime LastCheckedUtc { get; set; }

        public DateTime? LastOnlineUtc { get; set; }

        public string? LastError { get; set; }
    }

    /// <summary>
    /// Background availability pinger: probes every enabled friend server on a
    /// short cadence and records online/offline state in memory. When a server
    /// flips state, its federated items are hidden (offline) or re-shown
    /// (back online) via reconciliation - nothing is ever deleted from the
    /// cache or the database, so a transient outage can never wipe a library.
    /// <para>
    /// Probes are cheap and silent: a single short-timeout GET against the
    /// plugin's own anonymous <c>Peer/SystemInfo</c> route (Jellyfin peers) or
    /// the provider's own connection test (Plex). Consecutive failures are
    /// required before a server is marked offline so one slow response never
    /// hides a whole library; a single success marks it online again
    /// immediately so recovery is fast.
    /// </para>
    /// </summary>
    public class FederationAvailabilityService : IHostedService, IDisposable
    {
        internal static readonly TimeSpan ProbeInterval = TimeSpan.FromMinutes(2);
        internal static readonly TimeSpan ConfirmInterval = TimeSpan.FromSeconds(15);
        internal static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);
        internal const int OfflineThreshold = 2;
        internal static readonly TimeSpan ExpectedOfflineInterval = TimeSpan.FromMinutes(10);

        private static readonly HttpClient ProbeHttpClient = new HttpClient
        {
            Timeout = ProbeTimeout
        };

        private readonly ILogger<FederationAvailabilityService> _logger;
        private readonly IRemoteServerClientFactory _clientFactory;
        private readonly ExternalCatalogRegistry _externalCatalogs;
        private readonly FederationItemPersistenceService _persistence;
        private readonly AvailabilityScheduleStore _schedule;
        private readonly ConcurrentDictionary<string, ServerAvailability> _states = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, int> _consecutiveFailures = new(StringComparer.OrdinalIgnoreCase);
        private readonly AdaptiveSourceRanking _ranking;
        private readonly ConcurrentDictionary<string, DateTime> _nextProbe = new(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim _probeGate = new(1, 1);
        private CancellationTokenSource? _cts;
        private Task? _loop;
        private readonly object _notificationGate = new();
        private Task _notifications = Task.CompletedTask;

        /// <summary>
        /// Fires after a server's reachability flips (online to offline or back).
        /// The handler triggers reconciliation for that server's mappings.
        /// </summary>
        internal Func<string, bool, CancellationToken, Task>? OnReachabilityChangedAsync { get; set; }

        public FederationAvailabilityService(
            ILogger<FederationAvailabilityService> logger,
            IRemoteServerClientFactory clientFactory,
            ExternalCatalogRegistry externalCatalogs,
            FederationItemPersistenceService persistence,
            AvailabilityScheduleStore? schedule = null,
            AdaptiveSourceRanking? ranking = null)
        {
            _logger = logger;
            _clientFactory = clientFactory;
            _externalCatalogs = externalCatalogs;
            _persistence = persistence;
            _schedule = schedule ?? new AvailabilityScheduleStore();
            _ranking = ranking ?? new AdaptiveSourceRanking();
        }

        /// <summary>
        /// Current availability snapshot for a server, or null when never probed.
        /// </summary>
        public ServerAvailability? GetAvailability(string serverId)
            => string.IsNullOrEmpty(serverId) ? null
                : _states.TryGetValue(serverId, out var state) ? state : null;

        /// <summary>
        /// Whether a server's items should currently be hidden from the local
        /// library. Unknown (never probed) and Online both mean visible - only
        /// a confirmed Offline hides. Disabled servers are handled by the
        /// existing enabled-state path, not here.
        /// </summary>
        public bool IsOffline(string serverId)
            => _states.TryGetValue(serverId, out var state)
                && state.Reachability == ServerReachability.Offline;

        /// <summary>
        /// All current snapshots, keyed by server id. Backs the status endpoint.
        /// </summary>
        public IReadOnlyDictionary<string, ServerAvailability> GetAll()
            => _states;

        /// <inheritdoc />
        public Task StartAsync(CancellationToken cancellationToken)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _loop = Task.Run(() => LoopAsync(_cts.Token));
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            try
            {
                _cts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            if (_loop != null)
            {
                try
                {
                    await _loop.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[Federation] Availability pinger stopped with an error");
                }
            }

            _schedule.SaveIfDue(force: true);
            await _notifications.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            try
            {
                _cts?.Cancel();
                _cts?.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }

            _probeGate.Dispose();
        }

        private async Task LoopAsync(CancellationToken cancellationToken)
        {
            // Let Jellyfin finish startup before the first probe round so an
            // early failure burst never hides libraries on a fresh boot.
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await ProbeAllAsync(cancellationToken, force: false).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[Federation] Availability probe round failed");
                }

                try
                {
                    await Task.Delay(DelayAfterRound(_consecutiveFailures.Values, ShouldStretchIdle()), cancellationToken).ConfigureAwait(false);
                    _schedule.RecordSelf(DateTime.UtcNow);
                    _schedule.SaveIfDue();
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        /// <summary>
        /// Classifies initially unknown friends before cached items are reconciled.
        /// A first failure gets a confirmation round after the normal grace period.
        /// </summary>
        public async Task EnsureProbedAsync(CancellationToken cancellationToken)
        {
            await ProbeAllAsync(cancellationToken, waitForCurrentRound: true).ConfigureAwait(false);
            if (Plugin.Instance?.Configuration?.RemoteServers?.Any(server => server.Enabled
                && GetAvailability(server.Id)?.Reachability == ServerReachability.Unknown) == true)
            {
                await Task.Delay(ConfirmInterval, cancellationToken).ConfigureAwait(false);
                await ProbeAllAsync(cancellationToken, waitForCurrentRound: true).ConfigureAwait(false);
            }
        }

        /// <summary>Probes every enabled server once (only those due unless <paramref name="force"/>), optionally waiting for an ongoing round.</summary>
        public async Task ProbeAllAsync(CancellationToken cancellationToken = default, bool force = true, bool waitForCurrentRound = false)
        {
            if (waitForCurrentRound)
            {
                await _probeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            else if (!await _probeGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            {
                _logger.LogDebug("[Federation] Availability probe already running; skipping this round");
                return;
            }

            try
            {
                var servers = Plugin.Instance?.Configuration?.RemoteServers
                    ?.Where(s => s.Enabled && (force || !_nextProbe.TryGetValue(s.Id, out var due) || due <= DateTime.UtcNow))
                    .ToList() ?? new List<RemoteServer>();
                if (servers.Count == 0)
                {
                    return;
                }

                // Bounded fan-out: one wedged server must not hold the round for
                // the rest, and a 60-server roster must not open 60 sockets at
                // once either.
                using var gate = new SemaphoreSlim(4);
                var tasks = servers.Select(async server =>
                {
                    await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        await ProbeOneAsync(server, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "[Federation] Availability probe failed for {ServerName}", server.Name);
                        RecordResult(server, false, "Probe error");
                    }
                    finally
                    {
                        gate.Release();
                    }
                });

                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            finally
            {
                _probeGate.Release();
            }
        }

        private async Task ProbeOneAsync(RemoteServer server, CancellationToken cancellationToken)
        {
            var probeTime = Stopwatch.StartNew();
            using var timeoutCts = new CancellationTokenSource(ProbeTimeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            string? error = null;
            bool online;
            if (server.Kind != ServerKind.Jellyfin)
            {
                var provider = _externalCatalogs.For(server);
                if (provider == null)
                {
                    return;
                }

                try
                {
                    var name = await provider.TestConnectionAsync(server, linkedCts.Token).ConfigureAwait(false);
                    online = name != null;
                    if (!online)
                    {
                        error = "The server did not answer.";
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    online = false;
                    error = "The server did not answer in time.";
                }
            }
            else
            {
                try
                {
                    using var request = new HttpRequestMessage(
                        HttpMethod.Get,
                        server.Url.TrimEnd('/') + "/Plugins/Federation/Peer/SystemInfo");
                    request.Headers.TryAddWithoutValidation(FederationTokenAuth.Header, server.ApiKey ?? string.Empty);
                    using var response = await ProbeHttpClient.SendAsync(request, linkedCts.Token).ConfigureAwait(false);
                    online = response.IsSuccessStatusCode;
                    if (!online)
                    {
                        error = $"HTTP {(int)response.StatusCode}";
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    online = false;
                    error = "The server did not answer in time.";
                }
                catch (Exception ex)
                {
                    online = false;
                    error = ex.GetType().Name;
                }
            }

            RecordResult(server, online, error, online ? probeTime.Elapsed.TotalMilliseconds : null);
        }

        internal void RecordResult(RemoteServer server, bool online, string? error, double? latencyMs = null)
        {
            _ranking.ObserveProbe(server.Id, online, latencyMs);
            var state = _states.GetOrAdd(server.Id, _ => new ServerAvailability());
            var previous = state.Reachability;
            state.LastCheckedUtc = DateTime.UtcNow;
            state.LastError = online ? null : error;

            if (online)
            {
                _consecutiveFailures[server.Id] = 0;
                state.LastOnlineUtc = DateTime.UtcNow;
                state.Reachability = ServerReachability.Online;
            }
            else
            {
                var failures = _consecutiveFailures.AddOrUpdate(server.Id, 1, (_, count) => count + 1);
                if (failures >= OfflineThreshold)
                {
                    state.Reachability = ServerReachability.Offline;
                }
                else if (previous == ServerReachability.Unknown)
                {
                    // Stay Unknown until the threshold is reached: a single
                    // failed first probe (server added while the friend is
                    // rebooting) must not hide anything yet.
                    state.Reachability = ServerReachability.Unknown;
                }
            }

            _schedule.RecordObservation(server.Id, online, DateTime.UtcNow);
            _schedule.RememberReachability(server.Id, state);

            var failuresNow = _consecutiveFailures.GetValueOrDefault(server.Id);
            var seconds = online ? ProbeInterval.TotalSeconds : failuresNow == 1 ? ConfirmInterval.TotalSeconds
                : Math.Min(480, ProbeInterval.TotalSeconds * Math.Pow(2, Math.Min(failuresNow - 2, 2)));
            _nextProbe[server.Id] = DateTime.UtcNow.AddSeconds(seconds * (0.9 + Random.Shared.NextDouble() * 0.2));

            if (previous != state.Reachability
                && (state.Reachability == ServerReachability.Offline || state.Reachability == ServerReachability.Online))
            {
                var (forecast, _) = _schedule.Forecast(server.Id, DateTime.UtcNow);
                if (state.Reachability == ServerReachability.Offline && forecast == AvailabilityForecast.LikelyOnline)
                {
                    _logger.LogInformation(
                        "[Federation] Server {ServerName} went offline unexpectedly (usually up at this hour); cached items are preserved",
                        server.Name);
                }
                else
                {
                    _logger.LogInformation(
                        "[Federation] Server {ServerName} is now {State} (previous: {Previous}); cached items are preserved, library visibility will update on rescan",
                        server.Name,
                        state.Reachability,
                        previous);
                }
                var handler = OnReachabilityChangedAsync;
                if (handler != null)
                {
                    // Capture the transition now. Reading the mutable state in a
                    // background task can turn an offline notification into online
                    // if the server recovers before the task starts. Dispatch in
                    // order so an older pause cannot run after the recovery resume.
                    var becameOnline = state.Reachability == ServerReachability.Online;
                    lock (_notificationGate)
                    {
                        _notifications = _notifications.ContinueWith(async _ =>
                        {
                            try
                            {
                                await handler(server.Id, becameOnline, _cts?.Token ?? CancellationToken.None).ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogDebug(ex, "[Federation] Reachability-change rescan failed for {ServerName}", server.Name);
                            }
                        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
                    }
                }
            }
        }

        /// <summary>
        /// After a probe round: confirm a first failure in 15s instead of
        /// waiting the full two-minute idle interval, so hide/unhide is not
        /// delayed by a whole extra cycle.
        /// </summary>
        internal static TimeSpan DelayAfterRound(IEnumerable<int> consecutiveFailures, bool stretchIdle = false)
        {
            var confirming = consecutiveFailures.Any(count => count > 0 && count < OfflineThreshold);
            if (confirming)
            {
                return ConfirmInterval;
            }

            return stretchIdle ? ExpectedOfflineInterval : ProbeInterval;
        }

        /// <summary>
        /// Reloads last live Offline snapshots when the hour-of-week forecast
        /// also says the friend is usually down, so a restart does not flash
        /// those libraries back as Unknown.
        /// </summary>
        public void RestorePersistedReachability()
        {
            foreach (var (id, row) in _schedule.Snapshot().Friends)
            {
                if (row.LastReachability != ServerReachability.Offline)
                {
                    continue;
                }

                var (forecast, _) = _schedule.Forecast(id, DateTime.UtcNow);
                if (forecast != AvailabilityForecast.LikelyOffline)
                {
                    continue;
                }

                _states[id] = new ServerAvailability
                {
                    Reachability = ServerReachability.Offline,
                    LastCheckedUtc = row.LastCheckedUtc,
                    LastOnlineUtc = row.LastOnlineUtc
                };
                _consecutiveFailures[id] = OfflineThreshold;
            }
        }

        public (AvailabilityForecast Forecast, int Confidence) GetForecast(string serverId)
            => _schedule.Forecast(serverId, DateTime.UtcNow);

        public AvailabilityScheduleStore Schedule => _schedule;

        private bool ShouldStretchIdle()
        {
            var offline = false;
            foreach (var pair in _states)
            {
                if (pair.Value.Reachability != ServerReachability.Offline)
                {
                    continue;
                }

                offline = true;
                if (_schedule.Forecast(pair.Key, DateTime.UtcNow).Forecast != AvailabilityForecast.LikelyOffline)
                {
                    return false;
                }
            }

            return offline;
        }

        /// <summary>
        /// Drops state for servers no longer configured so the status endpoint
        /// never reports stale ids.
        /// </summary>
        public void Forget(string serverId)
        {
            if (string.IsNullOrEmpty(serverId))
            {
                return;
            }

            _states.TryRemove(serverId, out _);
            _consecutiveFailures.TryRemove(serverId, out _);
            _nextProbe.TryRemove(serverId, out _);
            _ranking.Forget(serverId);
            _schedule.Forget(serverId);
        }
    }
}
