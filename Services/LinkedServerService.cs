using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Linked servers: two servers of one owner that are already friends, joined
    /// after an admin on each clicks "Link" and types the six-digit code the
    /// other server shows (<see cref="LinkCode"/>). Linked servers then use each
    /// other's friends as their own: server B gets server A's own key for each of
    /// A's friends, so to those friends B is A - same sharing, limits and
    /// conversion rules, nothing for them to accept. Settings are not shared.
    /// </summary>
    /// <remarks>
    /// <para>Trust: whoever controls either server can act as the other towards
    /// its friends, which is what linking means. The codes make sure one person
    /// holds both admin pages: a friend's key alone cannot start or finish a
    /// link, because the code is only ever shown on the admin page. Keys only
    /// travel over https, a private or Tailscale address, or a local host name.</para>
    /// <para>A friend used through a link (<see cref="RemoteServer.IsThroughLink"/>)
    /// is read and played only: it never calls this server, is never sent an
    /// unfriend, rule, pool, party, viewer or direct-link message, and is not
    /// passed on again (no chains). A direct friendship always wins over one
    /// through a link.</para>
    /// </remarks>
    public sealed class LinkedServerService : IHostedService, IDisposable
    {
        /// <summary>How long a started link waits for both codes.</summary>
        internal static readonly TimeSpan PendingLifetime = TimeSpan.FromMinutes(10);

        /// <summary>Wrong codes allowed before a started link is dropped.</summary>
        internal const int MaxAttempts = 5;

        /// <summary>How often friends are fetched again from each linked server.</summary>
        internal static readonly TimeSpan RefreshEvery = TimeSpan.FromMinutes(30);

        private readonly ILogger<LinkedServerService> _logger;
        private readonly IRemoteServerClientFactory _clientFactory;
        private readonly IServiceProvider? _services;
        private readonly ConcurrentDictionary<string, Pending> _pending = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, DateTime> _peerAsked = new(StringComparer.Ordinal);
        private readonly SemaphoreSlim _importLock = new(1, 1);
        private Timer? _timer;

        /// <summary>Initializes a new instance of the <see cref="LinkedServerService"/> class.</summary>
        public LinkedServerService(ILogger<LinkedServerService> logger, IRemoteServerClientFactory clientFactory, IServiceProvider? services = null)
        {
            _logger = logger;
            _clientFactory = clientFactory;
            _services = services;
        }

        /// <summary>Clock, replaceable in tests.</summary>
        internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

        /// <summary>Test seam: what happens after friends were added, changed or removed (default: sync and reconcile).</summary>
        internal Func<IReadOnlyList<string>, IReadOnlyList<LibraryMapping>, Task>? AfterImport { get; set; }

        private static PluginConfiguration Config => Plugin.Instance!.Configuration;

        /// <inheritdoc />
        public Task StartAsync(CancellationToken cancellationToken)
        {
            _timer = new Timer(_ => _ = RefreshAllAsync(CancellationToken.None), null, TimeSpan.FromMinutes(1), RefreshEvery);
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task StopAsync(CancellationToken cancellationToken)
        {
            _timer?.Change(Timeout.Infinite, Timeout.Infinite);
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            _timer?.Dispose();
            _importLock.Dispose();
        }

        /// <summary>
        /// Whether keys may be sent to <paramref name="url"/>: https, a private or
        /// Tailscale address, or a local name (no dot, or .local/.lan/.internal/.home.arpa).
        /// </summary>
        internal static bool TransportIsPrivate(string? url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return false;
            }

            if (uri.Scheme == Uri.UriSchemeHttps)
            {
                return true;
            }

            if (uri.Scheme != Uri.UriSchemeHttp)
            {
                return false;
            }

            var host = uri.Host.TrimEnd('.').ToLowerInvariant();
            return ConfigValidator.IsPrivateOrLoopbackHost(url)
                || (uri.HostNameType == UriHostNameType.Dns && !host.Contains('.', StringComparison.Ordinal))
                || host.EndsWith(".local", StringComparison.Ordinal)
                || host.EndsWith(".lan", StringComparison.Ordinal)
                || host.EndsWith(".internal", StringComparison.Ordinal)
                || host.EndsWith(".home.arpa", StringComparison.Ordinal);
        }

        /// <summary>Why <paramref name="friend"/> cannot be linked, or null when it can.</summary>
        internal static string? CannotLink(RemoteServer? friend)
        {
            if (friend == null)
            {
                return "Friend not found.";
            }

            if (friend.Kind != ServerKind.Jellyfin || friend.IsThroughLink)
            {
                return "Only a Jellyfin server you are friends with directly can be linked.";
            }

            if (friend.Paired)
            {
                return "A friend connected with pairing codes can't be linked.";
            }

            if (!friend.Enabled || string.IsNullOrEmpty(friend.ApiKey) || string.IsNullOrEmpty(friend.IssuedApiKey))
            {
                return $"{friend.Name} is switched off or not fully connected. Reconnect it first.";
            }

            if (!TransportIsPrivate(friend.Url))
            {
                return $"{friend.Name}'s address is plain http on the internet. Linking sends keys, so it needs an https address (or a private or Tailscale one).";
            }

            return null;
        }

        /// <summary>Admin: what the page shows for every friend that is linked or being linked.</summary>
        internal IReadOnlyList<LinkStatus> Status()
        {
            var now = UtcNow();
            Sweep(now);
            var servers = Config.RemoteServers ?? new List<RemoteServer>();
            var result = new List<LinkStatus>();
            foreach (var friend in servers.Where(s => s.Kind == ServerKind.Jellyfin && !s.IsThroughLink))
            {
                _pending.TryGetValue(friend.Id, out var pending);
                var asked = _peerAsked.TryGetValue(friend.Id, out var at) && now - at < PendingLifetime;
                if (!friend.Linked && pending == null && !asked)
                {
                    continue;
                }

                result.Add(new LinkStatus(
                    friend.Id,
                    friend.Name,
                    friend.Linked,
                    friend.LinkedAtUtc,
                    pending == null ? null : LinkCode.Current(pending.Secret, now),
                    pending == null ? 0 : LinkCode.SecondsLeft(now),
                    pending == null ? 0 : (int)Math.Max(0, (pending.CreatedUtc + PendingLifetime - now).TotalSeconds),
                    asked,
                    pending?.MyKeyAccepted == true,
                    pending?.ReceivedKey != null,
                    pending == null ? 0 : MaxAttempts - pending.Attempts,
                    servers.Where(s => s.ViaLinkedServerId == friend.Id).Select(s => s.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(),
                    CannotLink(friend)));
            }

            return result;
        }

        /// <summary>Admin: starts linking with <paramref name="friendId"/>; shows a code and tells the friend.</summary>
        internal async Task<(bool Ok, string Message)> StartAsync(string friendId, CancellationToken cancellationToken)
        {
            var friend = Config.RemoteServers?.FirstOrDefault(s => s.Id == friendId);
            var why = CannotLink(friend);
            if (why != null)
            {
                return (false, why);
            }

            if (friend!.Linked)
            {
                return (false, $"{friend.Name} is already linked.");
            }

            var now = UtcNow();
            _pending[friend.Id] = new Pending(LinkCode.NewSecret(), now);
            var (status, _) = await _clientFactory.GetClient(friend).SendLinkAsync<LinkAnswer>(HttpMethod.Post, "Request", new { }, null, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("[Federation] Started linking with {Name}", friend.Name);
            return status is >= 200 and < 300
                ? (true, $"Type this code on {friend.Name} (its Friends tab → this server → Link), then type {friend.Name}'s code here.")
                : (true, $"{friend.Name} could not be told (it may run an older version). Open {friend.Name} yourself, click Link there, and swap codes.");
        }

        /// <summary>Admin: stops a link that is being made.</summary>
        internal void Cancel(string friendId)
        {
            _pending.TryRemove(friendId, out _);
            _peerAsked.TryRemove(friendId, out _);
        }

        /// <summary>
        /// Admin: the code shown on the other server, typed here. Sends it with
        /// this server's new link key; the other server checks it against its own
        /// secret. The link is made once both codes were accepted.
        /// </summary>
        internal async Task<(bool Ok, string Message)> EnterCodeAsync(string friendId, string? code, CancellationToken cancellationToken)
        {
            var friend = Config.RemoteServers?.FirstOrDefault(s => s.Id == friendId);
            var why = CannotLink(friend);
            if (why != null)
            {
                return (false, why);
            }

            Sweep(UtcNow());
            if (!_pending.TryGetValue(friend!.Id, out var pending))
            {
                return (false, "Click Link first, so this server shows its own code too.");
            }

            var digits = LinkCode.Normalize(code);
            if (digits == null)
            {
                return (false, "Type the six digits shown on the other server.");
            }

            var key = FederationTokenAuth.GenerateToken();
            var (status, answer) = await _clientFactory.GetClient(friend).SendLinkAsync<LinkAnswer>(HttpMethod.Post, "Verify", new LinkVerifyBody { Code = digits, Key = key }, null, cancellationToken).ConfigureAwait(false);
            if (status == 0)
            {
                return (false, $"{friend.Name} could not be reached.");
            }

            if (status == 404)
            {
                return (false, $"{friend.Name} runs a version without linking. Update it first.");
            }

            if (answer == null || !answer.Ok)
            {
                return (false, answer?.Error switch
                {
                    "not-started" => $"Click Link on {friend.Name} first, then type its code here.",
                    "too-many" => $"Too many wrong codes: {friend.Name} stopped this link. Start again on both servers.",
                    _ => $"That is not the code {friend.Name} shows. Codes change every {LinkCode.StepSeconds} seconds."
                });
            }

            lock (pending)
            {
                pending.MyIssuedKey = key;
                pending.MyKeyAccepted = true;
            }

            return TryComplete(friend, pending)
                ? (true, $"Linked with {friend.Name}. Its friends are being added here.")
                : (true, $"{friend.Name} accepted this code. Now type this server's code on {friend.Name}.");
        }

        /// <summary>Peer: the friend's admin clicked Link (shown here so this admin clicks it too).</summary>
        internal void PeerRequested(RemoteServer caller) => _peerAsked[caller.Id] = UtcNow();

        /// <summary>Peer: the friend's admin typed this server's code.</summary>
        internal LinkAnswer PeerVerify(RemoteServer caller, LinkVerifyBody? body)
        {
            if (CannotLink(caller) != null || body == null || string.IsNullOrEmpty(body.Key) || body.Key.Length != 64)
            {
                return new LinkAnswer { Ok = false, Error = "invalid" };
            }

            var now = UtcNow();
            Sweep(now);
            if (!_pending.TryGetValue(caller.Id, out var pending))
            {
                return new LinkAnswer { Ok = false, Error = "not-started" };
            }

            lock (pending)
            {
                if (!LinkCode.Matches(pending.Secret, body.Code, now))
                {
                    pending.Attempts++;
                    if (pending.Attempts >= MaxAttempts)
                    {
                        _pending.TryRemove(caller.Id, out _);
                        _logger.LogWarning("[Federation] Stopped linking with {Name}: {Count} wrong codes", caller.Name, MaxAttempts);
                        return new LinkAnswer { Ok = false, Error = "too-many" };
                    }

                    return new LinkAnswer { Ok = false, Error = "wrong-code" };
                }

                pending.ReceivedKey = body.Key;
            }

            return new LinkAnswer { Ok = true, Linked = TryComplete(caller, pending) };
        }

        /// <summary>True when <paramref name="caller"/> is linked and presented its link key.</summary>
        internal static bool IsLinkedCaller(RemoteServer? caller, string? presentedKey)
            => caller != null
                && caller.Linked
                && !string.IsNullOrEmpty(caller.IssuedLinkKey)
                && !string.IsNullOrEmpty(presentedKey)
                && FederationTokenAuth.FixedTimeEquals(caller.IssuedLinkKey, presentedKey);

        /// <summary>
        /// Peer: this server's own friends, with this server's key for each, for
        /// the linked server <paramref name="caller"/>. Never friends this server
        /// itself uses through a link, paired friends, Plex/Companion entries, or
        /// the caller.
        /// </summary>
        internal static List<LinkedFriend> Export(RemoteServer caller)
        {
            var config = Config;
            var mappings = config.LibraryMappings ?? new List<LibraryMapping>();
            return (config.RemoteServers ?? new List<RemoteServer>())
                .Where(s => s.Kind == ServerKind.Jellyfin
                    && s.Enabled
                    && !s.IsThroughLink
                    && !s.Paired
                    && s.Id != caller.Id
                    && !string.IsNullOrEmpty(s.ApiKey)
                    && ConfigValidator.IsValidServerUrl(s.Url)
                    && !string.Equals(s.FederationId, caller.FederationId, StringComparison.OrdinalIgnoreCase))
                .Select(s => new LinkedFriend
                {
                    FederationId = s.FederationId,
                    Name = s.Name,
                    Url = s.Url,
                    Key = s.ApiKey,
                    Libraries = mappings
                        .Where(m => m.Enabled)
                        .SelectMany(m => (m.RemoteLibrarySources ?? new List<RemoteLibrarySource>())
                            .Where(src => src.ServerId == s.Id)
                            .Select(src => new LinkedLibrary { Id = src.RemoteLibraryId, Name = src.RemoteLibraryName, MediaType = m.MediaType }))
                        .GroupBy(l => l.Id, StringComparer.Ordinal)
                        .Select(g => g.First())
                        .ToList()
                })
                .ToList();
        }

        /// <summary>Peer: the linked server unlinked; forget the link and the friends used through it.</summary>
        internal Task PeerRemovedAsync(RemoteServer caller, CancellationToken cancellationToken) => UnlinkLocallyAsync(caller, cancellationToken);

        /// <summary>Admin: unlinks <paramref name="friendId"/> on both servers (the other side best effort).</summary>
        internal async Task<(bool Ok, string Message)> UnlinkAsync(string friendId, CancellationToken cancellationToken)
        {
            var friend = Config.RemoteServers?.FirstOrDefault(s => s.Id == friendId);
            if (friend == null || !friend.Linked)
            {
                return (false, "This server is not linked.");
            }

            await _clientFactory.GetClient(friend).SendLinkAsync<LinkAnswer>(HttpMethod.Post, "Remove", new { }, friend.LinkKey, cancellationToken).ConfigureAwait(false);
            var removed = await UnlinkLocallyAsync(friend, cancellationToken).ConfigureAwait(false);
            return (true, $"Unlinked from {friend.Name}. {removed} friend(s) used through it were removed here; the friendship with {friend.Name} itself stays.");
        }

        /// <summary>
        /// Called after this server removed <paramref name="server"/>: a linked
        /// server takes its friends with it; one of this server's own friends is
        /// dropped from every linked server soon after.
        /// </summary>
        internal async Task OnServerRemovedAsync(RemoteServer server, CancellationToken cancellationToken)
        {
            if (server.Linked)
            {
                await RemoveThroughAsync(server.Id, cancellationToken).ConfigureAwait(false);
            }
            else if (!server.IsThroughLink)
            {
                await NotifyLinkedAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>Tells every linked server that this server's friends changed.</summary>
        internal async Task NotifyLinkedAsync(CancellationToken cancellationToken)
        {
            foreach (var linked in (Config.RemoteServers ?? new List<RemoteServer>()).Where(s => s.Linked && s.Enabled).ToList())
            {
                await _clientFactory.GetClient(linked).SendLinkAsync<LinkAnswer>(HttpMethod.Post, "Changed", new { }, linked.LinkKey, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>Fetches friends again from every linked server.</summary>
        internal async Task RefreshAllAsync(CancellationToken cancellationToken)
        {
            foreach (var linked in (Config.RemoteServers ?? new List<RemoteServer>()).Where(s => s.Linked).ToList())
            {
                try
                {
                    await ImportFromAsync(linked, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "[Federation] Could not fetch friends from linked server {Name}", linked.Name);
                }
            }
        }

        /// <summary>
        /// Fetches <paramref name="linked"/>'s friends and makes this server's
        /// friends-through-the-link match them: added, updated (renamed, new key),
        /// or removed. Their libraries go into this server's libraries of the
        /// same type. Returns how many rows changed, or -1 when the linked server
        /// did not answer (nothing is removed then).
        /// </summary>
        internal async Task<int> ImportFromAsync(RemoteServer linked, CancellationToken cancellationToken)
        {
            if (!linked.Linked || !linked.Enabled)
            {
                return -1;
            }

            var (status, friends) = await _clientFactory.GetClient(linked).SendLinkAsync<List<LinkedFriend>>(HttpMethod.Get, "Friends", null, linked.LinkKey, cancellationToken).ConfigureAwait(false);
            if (status is < 200 or >= 300 || friends == null)
            {
                _logger.LogInformation("[Federation] Linked server {Name} did not list its friends (HTTP {Status}); keeping what is here", linked.Name, status);
                return -1;
            }

            await _importLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await ApplyAsync(linked, friends, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _importLock.Release();
            }
        }

        /// <summary>The reconciliation step of <see cref="ImportFromAsync"/>, on an already fetched list.</summary>
        internal async Task<int> ApplyAsync(RemoteServer linked, IReadOnlyList<LinkedFriend> friends, CancellationToken cancellationToken)
        {
            var config = Config;
            config.RemoteServers ??= new List<RemoteServer>();
            config.LibraryMappings ??= new List<LibraryMapping>();
            var selfId = config.LocalFederationId ?? string.Empty;
            var selfUrl = (config.ServerUrl ?? string.Empty).TrimEnd('/');
            var direct = config.RemoteServers.Where(s => !s.IsThroughLink).ToList();
            var mine = config.RemoteServers.Where(s => s.ViaLinkedServerId == linked.Id).ToList();
            var keep = new HashSet<string>(StringComparer.Ordinal);
            var touchedServers = new List<string>();
            var touchedMappings = new HashSet<LibraryMapping>();
            var changed = 0;

            foreach (var f in friends.Take(500))
            {
                if (f == null
                    || string.IsNullOrEmpty(f.Key) || f.Key.Length > 128
                    || !ConfigValidator.IsValidServerUrl(f.Url)
                    || (!string.IsNullOrEmpty(f.FederationId) && string.Equals(f.FederationId, selfId, StringComparison.OrdinalIgnoreCase))
                    || (selfUrl.Length > 0 && string.Equals(f.Url.TrimEnd('/'), selfUrl, StringComparison.OrdinalIgnoreCase))
                    || string.Equals(f.FederationId, linked.FederationId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // A friendship of this server's own always wins.
                if (direct.Any(d => SameServer(d, f.FederationId, f.Url)))
                {
                    continue;
                }

                // Another linked server already provides it: first one wins.
                if (config.RemoteServers.Any(s => s.IsThroughLink && s.ViaLinkedServerId != linked.Id && SameServer(s, f.FederationId, f.Url)))
                {
                    continue;
                }

                var row = mine.FirstOrDefault(s => SameServer(s, f.FederationId, f.Url));
                var name = string.IsNullOrWhiteSpace(f.Name) ? f.Url : f.Name!.Trim();
                if (name.Length > 100)
                {
                    name = name[..100];
                }

                if (row == null)
                {
                    row = new RemoteServer
                    {
                        Id = Guid.NewGuid().ToString(),
                        Kind = ServerKind.Jellyfin,
                        Name = name,
                        Url = f.Url.TrimEnd('/'),
                        ApiKey = f.Key,
                        FederationId = f.FederationId ?? string.Empty,
                        Enabled = true,
                        StreamingMode = StreamingMode.Direct,
                        ShareAllLibraries = false,
                        AllowDownloads = false,
                        ViaLinkedServerId = linked.Id
                    };
                    config.RemoteServers.Add(row);
                    changed++;
                    touchedServers.Add(row.Id);
                }
                else if (row.ApiKey != f.Key || row.Url != f.Url.TrimEnd('/') || row.Name != name)
                {
                    row.ApiKey = f.Key;
                    row.Url = f.Url.TrimEnd('/');
                    row.Name = name;
                    changed++;
                    touchedServers.Add(row.Id);
                    _clientFactory.Invalidate(row.Id);
                }

                keep.Add(row.Id);
                foreach (var lib in (f.Libraries ?? new List<LinkedLibrary>()).Take(200))
                {
                    if (lib == null || string.IsNullOrWhiteSpace(lib.Id))
                    {
                        continue;
                    }

                    var mapping = MappingFor(config, lib.MediaType);
                    if (mapping.RemoteLibrarySources.Any(s => s.ServerId == row.Id && s.RemoteLibraryId == lib.Id))
                    {
                        continue;
                    }

                    mapping.RemoteLibrarySources.Add(new RemoteLibrarySource { ServerId = row.Id, ServerName = row.Name, RemoteLibraryId = lib.Id, RemoteLibraryName = lib.Name ?? string.Empty });
                    if (!mapping.RemoteServerIds.Contains(row.Id))
                    {
                        mapping.RemoteServerIds.Add(row.Id);
                    }

                    touchedMappings.Add(mapping);
                    if (!touchedServers.Contains(row.Id))
                    {
                        touchedServers.Add(row.Id);
                    }
                }
            }

            var gone = mine.Where(s => !keep.Contains(s.Id)).ToList();
            foreach (var row in gone)
            {
                foreach (var mapping in RemoveRow(config, row))
                {
                    touchedMappings.Add(mapping);
                }

                changed++;
            }

            if (changed > 0 || touchedMappings.Count > 0)
            {
                Plugin.Instance!.SaveConfiguration();
                _logger.LogInformation("[Federation] Friends through linked server {Name}: {Count} now, {Changed} change(s)", linked.Name, keep.Count, changed);
                await AfterChangeAsync(touchedServers, touchedMappings.ToList()).ConfigureAwait(false);
            }

            return changed;
        }

        private static bool SameServer(RemoteServer s, string? federationId, string? url)
            => (!string.IsNullOrEmpty(federationId) && string.Equals(s.FederationId, federationId, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrEmpty(url) && string.Equals(s.Url.TrimEnd('/'), url.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));

        // This server's own library of that type (as the library picker would choose), or a new one.
        private static LibraryMapping MappingFor(PluginConfiguration config, string? mediaType)
        {
            var type = string.IsNullOrWhiteSpace(mediaType) ? "Movie" : mediaType!;
            var existing = config.LibraryMappings.FirstOrDefault(m => m.Enabled && m.AutoManaged && string.Equals(m.MediaType, type, StringComparison.OrdinalIgnoreCase))
                ?? config.LibraryMappings.FirstOrDefault(m => m.Enabled && string.Equals(m.MediaType, type, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                existing.RemoteLibrarySources ??= new List<RemoteLibrarySource>();
                existing.RemoteServerIds ??= new List<string>();
                return existing;
            }

            var mapping = new LibraryMapping
            {
                LocalLibraryName = FederationLibraryTargets.DefaultName(type),
                MediaType = type,
                Enabled = true,
                AutoProvision = true,
                AutoManaged = true
            };
            config.LibraryMappings.Add(mapping);
            return mapping;
        }

        // Removes a friend-through-a-link row and its library sources; returns the mappings it was in.
        private List<LibraryMapping> RemoveRow(PluginConfiguration config, RemoteServer row)
        {
            config.RemoteServers.Remove(row);
            var affected = new List<LibraryMapping>();
            foreach (var mapping in config.LibraryMappings ?? new List<LibraryMapping>())
            {
                var before = mapping.RemoteLibrarySources?.Count ?? 0;
                mapping.RemoteLibrarySources?.RemoveAll(s => s.ServerId == row.Id);
                mapping.RemoteServerIds?.RemoveAll(id => id == row.Id);
                if ((mapping.RemoteLibrarySources?.Count ?? 0) != before)
                {
                    affected.Add(mapping);
                }
            }

            RemoteServerClient.InvalidateServerCaches(row.Id);
            _clientFactory.Invalidate(row.Id);
            return affected;
        }

        private async Task<int> RemoveThroughAsync(string linkedId, CancellationToken cancellationToken)
        {
            await _importLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var config = Config;
                var rows = (config.RemoteServers ?? new List<RemoteServer>()).Where(s => s.ViaLinkedServerId == linkedId).ToList();
                var mappings = new HashSet<LibraryMapping>();
                foreach (var row in rows)
                {
                    foreach (var m in RemoveRow(config, row))
                    {
                        mappings.Add(m);
                    }
                }

                if (rows.Count > 0)
                {
                    Plugin.Instance!.SaveConfiguration();
                    await AfterChangeAsync(Array.Empty<string>(), mappings.ToList()).ConfigureAwait(false);
                }

                return rows.Count;
            }
            finally
            {
                _importLock.Release();
            }
        }

        private async Task<int> UnlinkLocallyAsync(RemoteServer friend, CancellationToken cancellationToken)
        {
            friend.Linked = false;
            friend.LinkedAtUtc = null;
            friend.LinkKey = string.Empty;
            friend.IssuedLinkKey = string.Empty;
            Plugin.Instance!.SaveConfiguration();
            Cancel(friend.Id);
            _logger.LogInformation("[Federation] Unlinked from {Name}", friend.Name);
            return await RemoveThroughAsync(friend.Id, cancellationToken).ConfigureAwait(false);
        }

        private async Task AfterChangeAsync(IReadOnlyList<string> servers, IReadOnlyList<LibraryMapping> mappings)
        {
            if (AfterImport != null)
            {
                await AfterImport(servers, mappings).ConfigureAwait(false);
                return;
            }

            // Work that may take minutes runs after the reply: sync the friends
            // that came or changed, and take removed ones' titles out.
            var services = _services;
            if (services == null)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    // A friend's library of a type this server had no library for needs one.
                    if (mappings.Count > 0 && Config.AutoProvisionLibraries)
                    {
                        var provisioning = services.GetService<LibraryProvisioningService>();
                        if (provisioning != null)
                        {
                            await provisioning.EnsureLibrariesAsync(CancellationToken.None).ConfigureAwait(false);
                        }
                    }

                    var persistence = services.GetService<FederationItemPersistenceService>();
                    foreach (var mapping in mappings.Where(m => m.Enabled))
                    {
                        if (persistence != null)
                        {
                            await persistence.ReconcileMappingAsync(mapping, CancellationToken.None).ConfigureAwait(false);
                        }
                    }

                    var sync = services.GetService<FederationSyncService>();
                    foreach (var id in servers)
                    {
                        if (sync != null && Config.RemoteServers.Any(s => s.Id == id))
                        {
                            await sync.SyncServerAsync(id, CancellationToken.None).ConfigureAwait(false);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Federation] Syncing friends through a linked server failed; the next sync retries");
                }
            });
        }

        private bool TryComplete(RemoteServer friend, Pending pending)
        {
            string received;
            string issued;
            lock (pending)
            {
                if (!pending.MyKeyAccepted || pending.ReceivedKey == null || pending.MyIssuedKey == null || pending.Completed)
                {
                    return pending.Completed;
                }

                pending.Completed = true;
                received = pending.ReceivedKey;
                issued = pending.MyIssuedKey;
            }

            friend.Linked = true;
            friend.LinkedAtUtc = UtcNow();
            friend.LinkKey = received;
            friend.IssuedLinkKey = issued;
            Plugin.Instance!.SaveConfiguration();
            _pending.TryRemove(friend.Id, out _);
            _peerAsked.TryRemove(friend.Id, out _);
            _logger.LogInformation("[Federation] Linked with {Name}", friend.Name);
            _ = Task.Run(async () =>
            {
                try
                {
                    // The other side may finish a moment later; give it that moment.
                    await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                    await ImportFromAsync(friend, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Federation] Could not fetch friends from newly linked {Name}; retrying on the next refresh", friend.Name);
                }
            });
            return true;
        }

        private void Sweep(DateTime now)
        {
            foreach (var kv in _pending)
            {
                if (now - kv.Value.CreatedUtc > PendingLifetime)
                {
                    _pending.TryRemove(kv.Key, out _);
                }
            }

            foreach (var kv in _peerAsked)
            {
                if (now - kv.Value > PendingLifetime)
                {
                    _peerAsked.TryRemove(kv.Key, out _);
                }
            }
        }

        private sealed class Pending
        {
            public Pending(byte[] secret, DateTime createdUtc)
            {
                Secret = secret;
                CreatedUtc = createdUtc;
            }

            public byte[] Secret { get; }

            public DateTime CreatedUtc { get; }

            public int Attempts { get; set; }

            public string? MyIssuedKey { get; set; }

            public bool MyKeyAccepted { get; set; }

            public string? ReceivedKey { get; set; }

            public bool Completed { get; set; }
        }
    }

    /// <summary>What the page shows for one friend that is linked or being linked.</summary>
    internal sealed record LinkStatus(
        string FriendId,
        string Name,
        bool Linked,
        DateTime? LinkedAtUtc,
        string? Code,
        int CodeSecondsLeft,
        int SecondsLeft,
        bool PeerAsked,
        bool TheyAcceptedMyEntry,
        bool IAcceptedTheirEntry,
        int AttemptsLeft,
        IReadOnlyList<string> FriendsThrough,
        string? CannotLink);

    /// <summary>Body of <c>Peer/Linked/Verify</c>.</summary>
    public sealed class LinkVerifyBody
    {
        /// <summary>Gets or sets the six digits the admin typed.</summary>
        public string? Code { get; set; }

        /// <summary>Gets or sets the link key the sender minted for the receiver to call it with.</summary>
        public string? Key { get; set; }
    }

    /// <summary>Answer of the link calls.</summary>
    public sealed class LinkAnswer
    {
        /// <summary>Gets or sets a value indicating whether the call did what it asked.</summary>
        public bool Ok { get; set; }

        /// <summary>Gets or sets a value indicating whether the link is now made on the answering side.</summary>
        public bool Linked { get; set; }

        /// <summary>Gets or sets why not: not-started, wrong-code, too-many, invalid.</summary>
        public string? Error { get; set; }
    }

    /// <summary>One friend a linked server lends (<c>Peer/Linked/Friends</c>).</summary>
    public sealed class LinkedFriend
    {
        /// <summary>Gets or sets the friend's federation id.</summary>
        public string? FederationId { get; set; }

        /// <summary>Gets or sets the friend's name.</summary>
        public string? Name { get; set; }

        /// <summary>Gets or sets the friend's address.</summary>
        public string Url { get; set; } = string.Empty;

        /// <summary>Gets or sets the lending server's key for the friend.</summary>
        public string Key { get; set; } = string.Empty;

        /// <summary>Gets or sets the friend's libraries the lending server uses.</summary>
        public List<LinkedLibrary>? Libraries { get; set; }
    }

    /// <summary>One library of a lent friend.</summary>
    public sealed class LinkedLibrary
    {
        /// <summary>Gets or sets the friend's library id.</summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>Gets or sets its name.</summary>
        public string? Name { get; set; }

        /// <summary>Gets or sets the type of the library it goes into (Movie, Series, ...).</summary>
        public string? MediaType { get; set; }
    }
}
