using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Federation.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.SyncPlay;
using MediaBrowser.Controller.SyncPlay.PlaybackRequests;
using MediaBrowser.Controller.SyncPlay.Requests;
using MediaBrowser.Model.Session;
using MediaBrowser.Model.SyncPlay;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>Outcome of a viewer's watch-party request.</summary>
    /// <param name="Status">HTTP status to answer with (200 when it worked).</param>
    /// <param name="Message">What happened, safe to show the viewer.</param>
    /// <param name="GroupId">The SyncPlay group, when one was created or shared.</param>
    public sealed record PartyResult(int Status, string Message, Guid? GroupId = null);

    /// <summary>
    /// Federated watch parties: a SyncPlay group whose viewers are on several servers.
    /// </summary>
    /// <remarks>
    /// Every server keeps an ordinary SyncPlay group, so every app that can watch
    /// together keeps working unchanged. The host's group is the hub. A hidden member
    /// (a "link": a session with this plugin's own <see cref="ISessionController"/>)
    /// sits in each group for every linked server and relays play, pause, seek, stop
    /// and title changes to the linked group, which applies them through its own link
    /// (<see cref="PartyLink"/> holds the rules). A friend's viewers find the party in
    /// their normal group list and join it like any other group; their server then
    /// creates its copy of the group and links it to the host. Each server plays its own
    /// copy of the title (<see cref="PartyItemMapper"/>) and SyncPlay's own access check
    /// still applies to every viewer.
    /// <para>
    /// All party state is touched only on one background worker, in order: Jellyfin
    /// delivers group messages while holding the group's lock, so links only queue them.
    /// Messages to friends go out in order per friend.
    /// </para>
    /// </remarks>
    public sealed class FederatedPartyService : IHostedService, IDisposable
    {
        internal const string AppName = "Jellyfin Federation";
        internal const string ObserverLabel = "Watching with friends";

        private static readonly TimeSpan Tick = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan AnnounceEvery = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan HoldTimeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan Silence = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(10);

        private readonly ISyncPlayManager _syncPlay;
        private readonly ISessionManager _sessions;
        private readonly ILibraryManager _library;
        private readonly FederationLibraryManager _federation;
        private readonly PartyItemMapper _mapper;
        private readonly IRemoteServerClientFactory _clients;
        private readonly ILogger<FederatedPartyService> _logger;

        private readonly Channel<Func<Task>> _work = Channel.CreateUnbounded<Func<Task>>(new UnboundedChannelOptions { SingleReader = true });
        private readonly ConcurrentDictionary<string, Channel<Func<Task>>> _outboxes = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<Guid, HostParty> _hosted = new();
        private readonly Dictionary<Guid, RemoteParty> _remote = new();
        private readonly CancellationTokenSource _stopping = new();
        private Timer? _timer;

        /// <summary>
        /// Initializes a new instance of the <see cref="FederatedPartyService"/> class.
        /// </summary>
        public FederatedPartyService(
            ISyncPlayManager syncPlay,
            ISessionManager sessions,
            ILibraryManager library,
            FederationLibraryManager federation,
            PartyItemMapper mapper,
            IRemoteServerClientFactory clients,
            ILogger<FederatedPartyService> logger)
        {
            _syncPlay = syncPlay;
            _sessions = sessions;
            _library = library;
            _federation = federation;
            _mapper = mapper;
            _clients = clients;
            _logger = logger;
        }

        private static PluginConfiguration? Config => Plugin.Instance?.Configuration;

        /// <inheritdoc />
        public Task StartAsync(CancellationToken cancellationToken)
        {
            _ = Task.Run(WorkAsync, CancellationToken.None);
            _timer = new Timer(_ => Post(TickAsync), null, Tick, Tick);
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            _timer?.Dispose();
            await RunAsync(async () =>
            {
                foreach (var party in _hosted.Values.ToList())
                {
                    await EndHostedAsync(party, "server shutting down").ConfigureAwait(false);
                }

                foreach (var party in _remote.Values.ToList())
                {
                    await DropRemoteAsync(party, tellHost: true).ConfigureAwait(false);
                }

                return true;
            }).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            _stopping.Cancel();
        }

        /// <inheritdoc />
        public void Dispose()
        {
            _timer?.Dispose();
            _stopping.Dispose();
        }

        // ------------------------------------------------------------------
        // Viewer requests (this server hosts)
        // ------------------------------------------------------------------

        /// <summary>
        /// Starts a new SyncPlay group for the viewer and shares it with friends: the
        /// "New group with friends" entry of the group menu. The viewer's app is told it
        /// joined the group exactly as with a normal new group.
        /// </summary>
        public Task<PartyResult> CreateAsync(User user, AuthorizationInfo info, string remoteIp, string? name)
            => RunAsync(async () =>
            {
                if (Config?.HostWatchPartiesWithFriends == false)
                {
                    return new PartyResult(403, "Watch parties with friends are turned off on this server.");
                }

                if (user.SyncPlayAccess != SyncPlayUserAccessType.CreateAndJoinGroups)
                {
                    return new PartyResult(403, "You aren't allowed to start watch parties.");
                }

                var friends = Shareable().ToList();
                if (friends.Count == 0)
                {
                    return new PartyResult(409, "No friend's server can join a watch party right now.");
                }

                var session = await ViewerSessionAsync(user, info, remoteIp).ConfigureAwait(false);
                var groupName = string.IsNullOrWhiteSpace(name) ? user.Username + "'s watch party" : name.Trim();
                var group = _syncPlay.NewGroup(session, new NewGroupRequest(groupName), CancellationToken.None);
                var party = await HostAsync(group.GroupId, groupName, user, friends).ConfigureAwait(false);
                return party == null
                    ? new PartyResult(500, "The watch party couldn't be shared with friends.", group.GroupId)
                    : new PartyResult(200, "Friends can join from their group list.", group.GroupId);
            });

        /// <summary>
        /// Shares the group the viewer is in with friends ("Invite friends"). Only a
        /// group started on this server can be shared, by someone in it.
        /// </summary>
        public Task<PartyResult> ShareAsync(User user, Guid groupId)
            => RunAsync(async () =>
            {
                if (Config?.HostWatchPartiesWithFriends == false)
                {
                    return new PartyResult(403, "Watch parties with friends are turned off on this server.");
                }

                if (user.SyncPlayAccess != SyncPlayUserAccessType.CreateAndJoinGroups)
                {
                    return new PartyResult(403, "You aren't allowed to start watch parties.");
                }

                if (_hosted.Values.Any(p => p.GroupId == groupId))
                {
                    return new PartyResult(200, "This group is already shared with friends.", groupId);
                }

                if (_remote.Values.Any(p => p.Mirror?.GroupId == groupId))
                {
                    return new PartyResult(409, "This party is hosted by a friend; only they can share it.");
                }

                var info = _syncPlay.GetGroup(Probe(user.Id), groupId);
                if (info == null || !info.Participants.Contains(user.Username, StringComparer.Ordinal))
                {
                    return new PartyResult(404, "You're not in that group.");
                }

                var friends = Shareable().ToList();
                if (friends.Count == 0)
                {
                    return new PartyResult(409, "No friend's server can join a watch party right now.");
                }

                var party = await HostAsync(groupId, info.GroupName, user, friends).ConfigureAwait(false);
                return party == null
                    ? new PartyResult(500, "The group couldn't be shared with friends.")
                    : new PartyResult(200, "Friends can now join this group from their group list.", groupId);
            });

        /// <summary>Whether a group is a watch party with friends, for the group menu.</summary>
        public Task<(bool Shared, bool HostedHere, IReadOnlyList<string> Friends)> StatusAsync(Guid groupId)
            => RunAsync(() =>
            {
                if (_hosted.Values.FirstOrDefault(p => p.GroupId == groupId) is { } hosted)
                {
                    return Task.FromResult<(bool, bool, IReadOnlyList<string>)>((true, true, hosted.Links.Values.Select(l => l.Peer!.Name).ToList()));
                }

                if (_remote.Values.FirstOrDefault(p => p.Mirror?.GroupId == groupId) is { } remote)
                {
                    var host = _federation.GetServer(remote.HostServerId);
                    return Task.FromResult<(bool, bool, IReadOnlyList<string>)>((true, false, new[] { host?.Name ?? "a friend" }));
                }

                return Task.FromResult<(bool, bool, IReadOnlyList<string>)>((false, false, Array.Empty<string>()));
            });

        // ------------------------------------------------------------------
        // Viewer requests (a friend hosts): the native group list and join
        // ------------------------------------------------------------------

        /// <summary>
        /// Friends' parties this viewer could join, shaped like SyncPlay's own list.
        /// Only parties this server hasn't joined yet (once it has, its own copy of
        /// the group is in the native list) and whose title this viewer can open here.
        /// </summary>
        public Task<IReadOnlyList<GroupInfoDto>> ListAsync(User user)
            => RunAsync(() =>
            {
                var list = new List<GroupInfoDto>();
                if (Config?.JoinFriendsWatchParties == false || user.SyncPlayAccess == SyncPlayUserAccessType.None)
                {
                    return Task.FromResult<IReadOnlyList<GroupInfoDto>>(list);
                }

                foreach (var party in _remote.Values.Where(p => p.Mirror == null))
                {
                    var host = _federation.GetServer(party.HostServerId);
                    if (host is not { Enabled: true } || !CanWatch(party.Snapshot, user))
                    {
                        continue;
                    }

                    var state = party.Snapshot is { Items.Count: > 0 } s
                        ? s.IsPlaying ? GroupStateType.Playing : GroupStateType.Paused
                        : GroupStateType.Idle;
                    list.Add(new GroupInfoDto(
                        party.PartyId,
                        party.Name + " · " + host.Name,
                        state,
                        party.Participants.Select(n => n + " (" + host.Name + ")").ToList(),
                        DateTime.UtcNow));
                }

                return Task.FromResult<IReadOnlyList<GroupInfoDto>>(list);
            });

        /// <summary>Whether <paramref name="groupId"/> is a friend's party listed here.</summary>
        public Task<bool> IsFriendPartyAsync(Guid groupId)
            => RunAsync(() => Task.FromResult(_remote.ContainsKey(groupId)));

        /// <summary>
        /// A viewer joining a friend's party from the group list: joins this server's
        /// copy of the party, creating it (and linking it to the host) on first join.
        /// </summary>
        public Task<PartyResult> JoinAsync(User user, AuthorizationInfo info, string remoteIp, Guid partyId)
            => RunAsync(async () =>
            {
                if (!_remote.TryGetValue(partyId, out var party))
                {
                    return new PartyResult(404, "That watch party has ended.");
                }

                if (Config?.JoinFriendsWatchParties == false || user.SyncPlayAccess == SyncPlayUserAccessType.None)
                {
                    return new PartyResult(403, "You can't join watch parties.");
                }

                var host = _federation.GetServer(party.HostServerId);
                if (host is not { Enabled: true } || FederationItemPersistenceService.AvailabilityOverride?.IsOffline(host.Id) == true)
                {
                    return new PartyResult(503, "The friend hosting this party is offline.");
                }

                if (!CanWatch(party.Snapshot, user))
                {
                    return new PartyResult(403, "You can't watch what this party is playing.");
                }

                var session = await ViewerSessionAsync(user, info, remoteIp).ConfigureAwait(false);
                if (party.Mirror is { Closed: false } mirror)
                {
                    _syncPlay.JoinGroup(session, new JoinGroupRequest(mirror.GroupId), CancellationToken.None);
                    return new PartyResult(200, "Joined.", mirror.GroupId);
                }

                var group = _syncPlay.NewGroup(session, new NewGroupRequest(party.Name + " · " + host.Name), CancellationToken.None);
                var link = await CreateLinkAsync(party.PartyId, PartyLinkRole.RemoteSide, host, user, LinkLabel(party.Participants, host.Name), group.GroupId).ConfigureAwait(false);
                party.Mirror = link;
                Execute(link, new IgnoreWaitGroupRequest(true));
                _logger.LogInformation("[Federation] {User} joined {Host}'s watch party {Party}", user.Username, host.Name, party.Name);
                Send(link, party.PartyId.ToString("N") + "/Join", new PartyMembership { Participants = RealParticipants(link.GroupId, user.Id) }, status =>
                {
                    if (status != 200)
                    {
                        _logger.LogInformation("[Federation] {Host} didn't accept this server into watch party {Party} (HTTP {Status})", host.Name, party.Name, status);
                        Post(() => DropRemoteAsync(party, tellHost: false, leaveViewers: true));
                    }
                });
                return new PartyResult(200, "Joined.", group.GroupId);
            });

        // ------------------------------------------------------------------
        // Messages from friends
        // ------------------------------------------------------------------

        /// <summary>A friend hosting a party tells this server about it (or that it ended).</summary>
        public Task<int> ReceiveAnnouncementAsync(RemoteServer caller, PartyAnnouncement announcement)
            => RunAsync(async () =>
            {
                if (Config?.JoinFriendsWatchParties == false)
                {
                    return 403;
                }

                if (announcement.PartyId == Guid.Empty)
                {
                    return 400;
                }

                if (_remote.TryGetValue(announcement.PartyId, out var existing) && !string.Equals(existing.HostServerId, caller.Id, StringComparison.Ordinal))
                {
                    return 409;
                }

                if (announcement.Ended)
                {
                    if (existing != null)
                    {
                        await DropRemoteAsync(existing, tellHost: false).ConfigureAwait(false);
                    }

                    return 200;
                }

                existing ??= _remote[announcement.PartyId] = new RemoteParty(announcement.PartyId, caller.Id);
                existing.Name = Clean(announcement.Name, 80) ?? "Watch party";
                existing.Participants = (announcement.Participants ?? new List<string>()).Select(n => Clean(n, 60)).OfType<string>().Take(20).ToList();
                existing.Snapshot = announcement.Snapshot;
                existing.LastHeard = DateTime.UtcNow;
                if (existing.Mirror is { } mirror)
                {
                    Relabel(mirror, LinkLabel(existing.Participants, caller.Name));
                }

                return 200;
            });

        /// <summary>A friend's server joins a party this server hosts.</summary>
        public Task<int> ReceiveJoinAsync(RemoteServer caller, Guid partyId, PartyMembership membership)
            => RunAsync(async () =>
            {
                if (!_hosted.TryGetValue(partyId, out var party))
                {
                    return 404;
                }

                if (!party.SharedWith.Contains(caller.Id) || Config?.HostWatchPartiesWithFriends == false)
                {
                    return 403;
                }

                if (party.Links.TryGetValue(caller.Id, out var old))
                {
                    await CloseLinkAsync(old).ConfigureAwait(false);
                    party.Links.Remove(caller.Id);
                }

                var creator = party.Creator;
                var link = await CreateLinkAsync(partyId, PartyLinkRole.HostSide, caller, creator, LinkLabel(membership?.Participants, caller.Name), party.GroupId).ConfigureAwait(false);
                link.Logic.Holding = true;
                link.Logic.HoldingSince = DateTime.UtcNow;
                party.Links[caller.Id] = link;
                _logger.LogInformation("[Federation] {Friend} joined watch party {Party}", caller.Name, party.Name);

                // The join messages are queued behind this; afterwards a group that had
                // nothing to play has nothing to wait for.
                Post(() =>
                {
                    if (!link.Closed && link.Logic.Local.Playlist.Count == 0)
                    {
                        link.Logic.Holding = false;
                        Execute(link, new IgnoreWaitGroupRequest(true));
                        SendEvent(link, BuildSnapshotEvent(link, PartyEventKind.Snapshot));
                    }

                    return Task.CompletedTask;
                });
                return 200;
            });

        /// <summary>A friend's server leaves a party this server hosts.</summary>
        public Task<int> ReceiveLeaveAsync(RemoteServer caller, Guid partyId)
            => RunAsync(async () =>
            {
                if (_hosted.TryGetValue(partyId, out var party) && party.Links.Remove(caller.Id, out var link))
                {
                    _logger.LogInformation("[Federation] {Friend} left watch party {Party}", caller.Name, party.Name);
                    await CloseLinkAsync(link).ConfigureAwait(false);
                }

                return 200;
            });

        /// <summary>A play/pause/seek/queue message from the linked server.</summary>
        public Task<int> ReceiveEventAsync(RemoteServer caller, Guid partyId, PartyEvent e)
            => RunAsync(() =>
            {
                if (e == null || !(PartyEventKind.All.Contains(e.Kind) || e.Kind is "Members" or PartyLink.Wait or PartyLink.Go))
                {
                    return Task.FromResult(400);
                }

                LinkHandle? link = null;
                if (_hosted.TryGetValue(partyId, out var hosted))
                {
                    hosted.Links.TryGetValue(caller.Id, out link);
                }
                else if (_remote.TryGetValue(partyId, out var remote) && string.Equals(remote.HostServerId, caller.Id, StringComparison.Ordinal))
                {
                    remote.LastHeard = DateTime.UtcNow;
                    if (e.Participants != null)
                    {
                        remote.Participants = e.Participants.Select(n => Clean(n, 60)).OfType<string>().Take(20).ToList();
                    }

                    if (e.Snapshot != null)
                    {
                        remote.Snapshot = e.Snapshot;
                    }

                    link = remote.Mirror;
                }

                if (link == null || link.Closed)
                {
                    return Task.FromResult(404);
                }

                if (e.Participants != null)
                {
                    Relabel(link, LinkLabel(e.Participants, caller.Name));
                }

                Apply(link, e);
                return Task.FromResult(200);
            });

        // ------------------------------------------------------------------
        // Internals
        // ------------------------------------------------------------------

        /// <summary>Friends a party can be shared with: enabled Jellyfin friends we can reach.</summary>
        private IEnumerable<RemoteServer> Shareable()
            => (Config?.RemoteServers ?? new List<RemoteServer>()).Where(s =>
                s.Enabled
                && s.Kind == ServerKind.Jellyfin
                && !string.IsNullOrEmpty(s.ApiKey)
                && FederationItemPersistenceService.AvailabilityOverride?.IsOffline(s.Id) != true);

        private async Task<HostParty?> HostAsync(Guid groupId, string name, User creator, List<RemoteServer> friends)
        {
            var party = new HostParty(Guid.NewGuid(), groupId, name, creator);
            foreach (var friend in friends)
            {
                party.SharedWith.Add(friend.Id);
            }

            party.Observer = await CreateLinkAsync(party.PartyId, PartyLinkRole.HostSide, null, creator, ObserverLabel, groupId).ConfigureAwait(false);
            if (party.Observer.Closed)
            {
                return null;
            }

            Execute(party.Observer, new IgnoreWaitGroupRequest(true));
            _hosted[party.PartyId] = party;
            _logger.LogInformation("[Federation] {User} shared watch party {Party} with {Count} friend(s)", creator.Username, name, friends.Count);
            Post(() =>
            {
                AnnounceAll(party);
                return Task.CompletedTask;
            });
            return party;
        }

        private void AnnounceAll(HostParty party, bool ended = false)
        {
            party.LastAnnounce = DateTime.UtcNow;
            var participants = HostParticipants(party);
            foreach (var serverId in party.SharedWith.ToList())
            {
                var friend = _federation.GetServer(serverId);
                if (friend == null)
                {
                    party.SharedWith.Remove(serverId);
                    continue;
                }

                var announcement = new PartyAnnouncement
                {
                    PartyId = party.PartyId,
                    Name = party.Name,
                    Participants = participants,
                    Snapshot = ended || party.Observer == null ? null : BuildSnapshot(party.Observer.Logic, friend),
                    Ended = ended
                };
                SendTo(friend, "Announce", announcement, status =>
                {
                    if (!ended && status is 403 or 404)
                    {
                        // An older friend, or one that turned parties off.
                        Post(() =>
                        {
                            party.SharedWith.Remove(serverId);
                            return Task.CompletedTask;
                        });
                    }
                });
            }
        }

        /// <summary>Who is watching on the host side, for friends' labels: this server's viewers.</summary>
        private List<string> HostParticipants(HostParty party)
            => RealParticipants(party.GroupId, party.Creator.Id);

        /// <summary>The group's members that are people, not party links.</summary>
        private List<string> RealParticipants(Guid groupId, Guid probeUserId)
        {
            var info = _syncPlay.GetGroup(Probe(probeUserId), groupId);
            if (info == null)
            {
                return new List<string>();
            }

            var labels = AllLinks().Where(l => l.GroupId == groupId && !l.Closed).Select(l => l.Bridge.UserName).ToHashSet(StringComparer.Ordinal);
            return info.Participants.Where(p => !labels.Contains(p)).ToList();
        }

        private IEnumerable<LinkHandle> AllLinks()
            => _hosted.Values.SelectMany(p => p.Links.Values.Concat(p.Observer == null ? Enumerable.Empty<LinkHandle>() : new[] { p.Observer }))
                .Concat(_remote.Values.Where(p => p.Mirror != null).Select(p => p.Mirror!));

        private async Task<LinkHandle> CreateLinkAsync(Guid partyId, PartyLinkRole role, RemoteServer? peer, User user, string label, Guid groupId)
        {
            var deviceId = "federation-party-" + partyId.ToString("N") + "-" + (peer == null ? "observer" : Safe(peer.Id));
            var session = await _sessions.LogSessionActivity(
                AppName,
                Plugin.Instance?.Version?.ToString() ?? "0",
                deviceId,
                peer == null ? "Watch party (friends)" : "Watch party link · " + peer.Name,
                "127.0.0.1",
                user).ConfigureAwait(false);
            session.UserName = label;
            var link = new LinkHandle(partyId, new PartyLink(role, DateTime.UtcNow), session, groupId, peer);
            link.Controller = new PartyBridgeController(message => Post(() => OnLinkMessageAsync(link, message)));
            session.SessionControllers = new ISessionController[] { link.Controller };
            _syncPlay.JoinGroup(session, new JoinGroupRequest(groupId), CancellationToken.None);
            return link;
        }

        private async Task OnLinkMessageAsync(LinkHandle link, object message)
        {
            if (link.Closed)
            {
                return;
            }

            var now = DateTime.UtcNow;
            var observation = link.Logic.Observe(message, ActualState(link), now);
            if (link.Peer == null && observation.SendQueue && _hosted.Values.FirstOrDefault(p => p.Observer == link) is { } announced)
            {
                // A new title: friends' lists must show (or hide) the party for it now.
                AnnounceAll(announced);
            }
            if (link.Peer != null && !(link.Logic.Role == PartyLinkRole.RemoteSide && !link.Logic.Initialized))
            {
                foreach (var e in observation.Forward)
                {
                    SendEvent(link, e);
                }

                if (observation.SendQueue)
                {
                    SendEvent(link, BuildSnapshotEvent(link, PartyEventKind.Snapshot));
                }
            }

            if (observation.AutoReady && link.Logic.Local.Playlist.Count > 0)
            {
                Execute(link, link.Logic.ReadyAction());
            }

            if (observation.Left)
            {
                await OnLinkLeftAsync(link).ConfigureAwait(false);
                return;
            }

            if (observation.MembersChanged)
            {
                await OnMembersChangedAsync(link).ConfigureAwait(false);
            }
        }

        private async Task OnMembersChangedAsync(LinkHandle link)
        {
            if (_remote.Values.FirstOrDefault(p => p.Mirror == link) is { } remote)
            {
                var real = RealParticipants(link.GroupId, link.Bridge.UserId);
                if (real.Count == 0)
                {
                    // Everyone here left: this server leaves the party.
                    await DropRemoteAsync(remote, tellHost: true).ConfigureAwait(false);
                }
                else
                {
                    SendEvent(link, new PartyEvent { Kind = "Members", Participants = real });
                }

                return;
            }

            if (_hosted.Values.FirstOrDefault(p => p.Observer == link) is { } hosted)
            {
                var real = HostParticipants(hosted);
                if (real.Count == 0 && hosted.Links.Count == 0)
                {
                    await EndHostedAsync(hosted, "everyone left").ConfigureAwait(false);
                }
            }
        }

        private async Task OnLinkLeftAsync(LinkHandle link)
        {
            if (_hosted.Values.FirstOrDefault(p => p.Observer == link) is { } hosted)
            {
                await EndHostedAsync(hosted, "the group ended").ConfigureAwait(false);
                return;
            }

            foreach (var party in _hosted.Values)
            {
                if (party.Links.FirstOrDefault(l => l.Value == link) is { Value: not null } pair)
                {
                    party.Links.Remove(pair.Key);
                    await CloseLinkAsync(link).ConfigureAwait(false);
                    return;
                }
            }

            if (_remote.Values.FirstOrDefault(p => p.Mirror == link) is { } remote)
            {
                await DropRemoteAsync(remote, tellHost: true).ConfigureAwait(false);
            }
        }

        private void Apply(LinkHandle link, PartyEvent e)
        {
            var now = DateTime.UtcNow;
            IReadOnlyList<Guid>? mapped = null;
            var applied = e;
            if (e.Snapshot != null)
            {
                var (ids, index) = MapSnapshot(e.Snapshot);
                mapped = ids;
                if (ids != null && index != e.Snapshot.Index)
                {
                    applied = new PartyEvent
                    {
                        Kind = e.Kind,
                        PositionTicks = e.PositionTicks,
                        Participants = e.Participants,
                        Snapshot = new PartySnapshot
                        {
                            Items = e.Snapshot.Items,
                            Index = index,
                            PositionTicks = e.Snapshot.PositionTicks,
                            IsPlaying = e.Snapshot.IsPlaying,
                            Stable = e.Snapshot.Stable,
                            Shuffle = e.Snapshot.Shuffle,
                            Repeat = e.Snapshot.Repeat
                        }
                    };
                }
            }

            Refresh(link);
            var wasInitialized = link.Logic.Initialized;
            foreach (var action in link.Logic.Plan(applied, mapped, now))
            {
                Execute(link, action);
            }

            if (link.Logic.Role == PartyLinkRole.RemoteSide && !wasInitialized && link.Logic.Initialized)
            {
                if (link.Logic.WaitSent)
                {
                    WatchLoad(link, DateTime.UtcNow);
                }
                else
                {
                    // Nothing to load here (or nothing this server has): don't keep the host waiting.
                    SendEvent(link, new PartyEvent { Kind = PartyLink.Go });
                }
            }
        }

        /// <summary>The group's actual state from Jellyfin, or the link's last view when it can't be read.</summary>
        private GroupStateType ActualState(LinkHandle link)
            => (link.Closed ? null : _syncPlay.GetGroup(link.Bridge, link.GroupId))?.State ?? link.Logic.Local.State;

        /// <summary>
        /// Brings the link in line with the group's actual state (some changes reach no
        /// member) and sends what that change means to the linked server.
        /// </summary>
        private void Refresh(LinkHandle link)
        {
            if (link.Closed)
            {
                return;
            }

            var observation = link.Logic.Refresh(ActualState(link), DateTime.UtcNow);
            if (link.Peer != null)
            {
                foreach (var e in observation.Forward)
                {
                    SendEvent(link, e);
                }
            }
        }

        /// <summary>
        /// Checks twice a second until this server's viewers have loaded the party's
        /// title, then tells the host, which is holding everyone until then.
        /// </summary>
        private void WatchLoad(LinkHandle link, DateTime since)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(500).ConfigureAwait(false);
                Post(() =>
                {
                    if (link.Closed || !link.Logic.WaitSent)
                    {
                        return Task.CompletedTask;
                    }

                    Refresh(link);
                    if (link.Logic.WaitSent && DateTime.UtcNow - since < HoldTimeout)
                    {
                        WatchLoad(link, since);
                    }

                    return Task.CompletedTask;
                });
            });
        }

        /// <summary>
        /// This server's copy of a party queue: titles it has, in order. Null when it
        /// doesn't have the playing title (it can't follow along).
        /// </summary>
        private (IReadOnlyList<Guid>? Ids, int Index) MapSnapshot(PartySnapshot snapshot)
        {
            if (snapshot.Items == null || snapshot.Items.Count == 0)
            {
                return (Array.Empty<Guid>(), 0);
            }

            var ids = new List<Guid>();
            var index = -1;
            for (var i = 0; i < snapshot.Items.Count && i < 500; i++)
            {
                var local = _mapper.Resolve(snapshot.Items[i]);
                if (local == null)
                {
                    continue;
                }

                if (i == snapshot.Index)
                {
                    index = ids.Count;
                }

                ids.Add(local.Value);
            }

            return index < 0 ? (null, 0) : (ids, index);
        }

        private bool CanWatch(PartySnapshot? snapshot, User user)
        {
            if (snapshot?.Items is not { Count: > 0 } items)
            {
                return true;
            }

            var playing = items[Math.Clamp(snapshot.Index, 0, items.Count - 1)];
            var local = _mapper.Resolve(playing);
            var item = local == null ? null : _library.GetItemById(local.Value);
            return item != null && item.IsVisibleStandalone(user);
        }

        private PartyEvent BuildSnapshotEvent(LinkHandle link, string kind)
        {
            var participants = link.Logic.Role == PartyLinkRole.HostSide
                ? _hosted.TryGetValue(link.PartyId, out var hosted) ? HostParticipants(hosted) : null
                : RealParticipants(link.GroupId, link.Bridge.UserId);
            return new PartyEvent { Kind = kind, Snapshot = BuildSnapshot(link.Logic, link.Peer!), Participants = participants };
        }

        private PartySnapshot BuildSnapshot(PartyLink logic, RemoteServer peer)
        {
            var now = DateTime.UtcNow;
            var local = logic.Local;
            return new PartySnapshot
            {
                Items = local.Playlist.Select(p => _mapper.Describe(p.ItemId, peer)).ToList(),
                Index = local.Index,
                PositionTicks = local.PositionAt(now),
                IsPlaying = local.State == GroupStateType.Playing,
                Stable = local.State is GroupStateType.Playing or GroupStateType.Paused,
                Shuffle = local.Shuffle.ToString(),
                Repeat = local.Repeat.ToString()
            };
        }

        private void Execute(LinkHandle link, PartyAction action)
        {
            if (action.Kind == PartyActionKind.Hold)
            {
                // Make the group wait for the link: the friend's viewers are loading.
                Execute(link, new IgnoreWaitGroupRequest(false));
                Execute(link, new BufferGroupRequest(DateTime.UtcNow, action.PositionTicks, false, action.PlaylistItemId));
                return;
            }

            if (action.Kind == PartyActionKind.Ready && link.Logic.Role == PartyLinkRole.HostSide && link.Peer != null)
            {
                Execute(link, new ReadyGroupRequest(DateTime.UtcNow, action.PositionTicks, false, action.PlaylistItemId));
                Execute(link, new IgnoreWaitGroupRequest(true));
                return;
            }

            IGroupPlaybackRequest request = action.Kind switch
            {
                PartyActionKind.SetQueue => new PlayGroupRequest(action.Queue ?? Array.Empty<Guid>(), action.Index, action.PositionTicks),
                PartyActionKind.SetItem => new SetPlaylistItemGroupRequest(action.PlaylistItemId),
                PartyActionKind.Pause => new PauseGroupRequest(),
                PartyActionKind.Unpause => new UnpauseGroupRequest(),
                PartyActionKind.Seek => new SeekGroupRequest(action.PositionTicks),
                PartyActionKind.Stop => new StopGroupRequest(),
                _ => new ReadyGroupRequest(DateTime.UtcNow, action.PositionTicks, false, action.PlaylistItemId)
            };
            Execute(link, request);
        }

        private void Execute(LinkHandle link, IGroupPlaybackRequest request)
        {
            if (link.Closed)
            {
                return;
            }

            try
            {
                _syncPlay.HandleRequest(link.Bridge, request, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Federation] Watch party request {Request} failed", request.Action);
            }
        }

        private void SendEvent(LinkHandle link, PartyEvent e)
            => Send(link, link.PartyId.ToString("N") + "/Event", e, status =>
            {
                if (status is 404 or 410)
                {
                    // The other side no longer has this link.
                    Post(() => OnLinkLeftAsync(link));
                }
            });

        private void Send(LinkHandle link, string path, object body, Action<int>? done = null)
        {
            if (link.Peer == null || link.Closed)
            {
                return;
            }

            SendTo(link.Peer, path, body, status =>
            {
                if (status is >= 200 and < 300)
                {
                    link.LastPeerSuccess = DateTime.UtcNow;
                }

                done?.Invoke(status);
            });
        }

        private void SendTo(RemoteServer peer, string path, object body, Action<int>? done)
        {
            var outbox = _outboxes.GetOrAdd(peer.Id, id =>
            {
                var channel = Channel.CreateUnbounded<Func<Task>>(new UnboundedChannelOptions { SingleReader = true });
                _ = Task.Run(async () =>
                {
                    await foreach (var send in channel.Reader.ReadAllAsync(_stopping.Token).ConfigureAwait(false))
                    {
                        try
                        {
                            await send().ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            _logger.LogDebug(ex, "[Federation] Watch party message failed");
                        }
                    }
                });
                return channel;
            });

            outbox.Writer.TryWrite(async () =>
            {
                var client = _clients.GetClient(peer);
                var status = 0;
                if (client != null)
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
                    timeout.CancelAfter(SendTimeout);
                    try
                    {
                        status = await client.PostPartyAsync(path, body, timeout.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!_stopping.IsCancellationRequested)
                    {
                        status = 0;
                    }
                }

                if (status is < 200 or >= 300)
                {
                    _logger.LogInformation("[Federation] Watch party message {Path} to {Friend} failed (HTTP {Status})", path.Split('/').Last(), peer.Name, status);
                }
                else
                {
                    _logger.LogDebug("[Federation] Watch party message {Path} to {Friend} delivered", path.Split('/').Last(), peer.Name);
                }

                if (done != null)
                {
                    Post(() =>
                    {
                        done(status);
                        return Task.CompletedTask;
                    });
                }
            });
        }

        private async Task TickAsync()
        {
            var now = DateTime.UtcNow;
            foreach (var party in _hosted.Values.ToList())
            {
                if (party.Observer == null || _syncPlay.GetGroup(Probe(party.Creator.Id), party.GroupId) == null)
                {
                    await EndHostedAsync(party, "the group ended").ConfigureAwait(false);
                    continue;
                }

                var participants = HostParticipants(party);
                foreach (var (serverId, link) in party.Links.ToList())
                {
                    if (now - link.LastPeerSuccess > Silence)
                    {
                        _logger.LogInformation("[Federation] Lost contact with {Friend}; removing them from watch party {Party}", link.Peer?.Name, party.Name);
                        party.Links.Remove(serverId);
                        await CloseLinkAsync(link).ConfigureAwait(false);
                        continue;
                    }

                    if (link.Logic.Holding && now - link.Logic.HoldingSince > HoldTimeout)
                    {
                        _logger.LogInformation("[Federation] {Friend} took too long to load; watch party {Party} continues without waiting", link.Peer?.Name, party.Name);
                        Execute(link, link.Logic.Release(now));
                    }

                    var heartbeat = BuildSnapshotEvent(link, PartyEventKind.Heartbeat);
                    heartbeat.Participants = participants;
                    SendEvent(link, heartbeat);
                }

                if (participants.Count == 0 && party.Links.Count == 0)
                {
                    await EndHostedAsync(party, "everyone left").ConfigureAwait(false);
                    continue;
                }

                if (now - party.LastAnnounce >= AnnounceEvery)
                {
                    AnnounceAll(party);
                }
            }

            foreach (var party in _remote.Values.ToList())
            {
                if (now - party.LastHeard > Silence)
                {
                    _logger.LogInformation("[Federation] No word from the host of watch party {Party}; leaving it", party.Name);
                    await DropRemoteAsync(party, tellHost: false).ConfigureAwait(false);
                }
            }
        }

        private async Task EndHostedAsync(HostParty party, string reason)
        {
            if (!_hosted.Remove(party.PartyId))
            {
                return;
            }

            _logger.LogInformation("[Federation] Watch party {Party} ended ({Reason})", party.Name, reason);
            AnnounceAll(party, ended: true);
            foreach (var link in party.Links.Values.ToList())
            {
                await CloseLinkAsync(link).ConfigureAwait(false);
            }

            party.Links.Clear();
            if (party.Observer != null)
            {
                await CloseLinkAsync(party.Observer).ConfigureAwait(false);
            }
        }

        private async Task DropRemoteAsync(RemoteParty party, bool tellHost, bool leaveViewers = false)
        {
            var mirror = party.Mirror;
            party.Mirror = null;
            if (!tellHost)
            {
                _remote.Remove(party.PartyId);
            }

            if (mirror == null)
            {
                return;
            }

            if (tellHost)
            {
                Send(mirror, party.PartyId.ToString("N") + "/Leave", new PartyMembership());
            }

            if (leaveViewers)
            {
                foreach (var session in _sessions.Sessions.ToList())
                {
                    if (session.Id != mirror.Bridge.Id && session.UserId != Guid.Empty && _syncPlay.GetGroup(session, mirror.GroupId) != null
                        && RealParticipants(mirror.GroupId, session.UserId).Contains(session.UserName, StringComparer.Ordinal))
                    {
                        try
                        {
                            _syncPlay.LeaveGroup(session, new LeaveGroupRequest(), CancellationToken.None);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "[Federation] Could not take a viewer out of a refused watch party");
                        }
                    }
                }
            }

            await CloseLinkAsync(mirror).ConfigureAwait(false);
        }

        private async Task CloseLinkAsync(LinkHandle link)
        {
            if (link.Closed)
            {
                return;
            }

            link.Closed = true;
            link.Controller?.Close();
            try
            {
                await _sessions.ReportSessionEnded(link.Bridge.Id).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[Federation] Could not end a watch party link session");
            }
        }

        private void Relabel(LinkHandle link, string label)
        {
            if (!link.Closed && link.Peer != null)
            {
                link.Bridge.UserName = label;
            }
        }

        /// <summary>How a linked server's viewers appear in a group: "bob, carol (friendflix)".</summary>
        internal static string LinkLabel(IReadOnlyCollection<string>? participants, string serverName)
        {
            var names = (participants ?? Array.Empty<string>()).Where(n => !string.IsNullOrWhiteSpace(n)).Take(4).ToList();
            var more = (participants?.Count ?? 0) - names.Count;
            var who = names.Count == 0 ? string.Empty : string.Join(", ", names) + (more > 0 ? " +" + more : string.Empty) + " ";
            return who + "(" + serverName + ")";
        }

        private async Task<SessionInfo> ViewerSessionAsync(User user, AuthorizationInfo info, string remoteIp)
            => await _sessions.LogSessionActivity(
                string.IsNullOrEmpty(info.Client) ? "Unknown" : info.Client,
                string.IsNullOrEmpty(info.Version) ? "0" : info.Version,
                string.IsNullOrEmpty(info.DeviceId) ? "federation-unknown-device" : info.DeviceId,
                info.Device ?? string.Empty,
                remoteIp,
                user).ConfigureAwait(false);

        /// <summary>A throwaway session identity for SyncPlay's group lookups, which only read the user.</summary>
        private SessionInfo Probe(Guid userId)
            => new(_sessions, _logger) { UserId = userId };

        private static string Safe(string value)
            => new string(value.Where(char.IsLetterOrDigit).Take(40).ToArray());

        private static string? Clean(string? value, int max)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var cleaned = new string(value.Where(c => !char.IsControl(c)).ToArray()).Trim();
            return cleaned.Length == 0 ? null : cleaned.Length > max ? cleaned[..max] : cleaned;
        }

        private void Post(Func<Task> work) => _work.Writer.TryWrite(work);

        private Task<T> RunAsync<T>(Func<Task<T>> work)
        {
            var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(async () =>
            {
                try
                {
                    done.SetResult(await work().ConfigureAwait(false));
                }
                catch (Exception ex)
                {
                    done.SetException(ex);
                }
            });
            return done.Task;
        }

        private async Task WorkAsync()
        {
            try
            {
                await foreach (var work in _work.Reader.ReadAllAsync(_stopping.Token).ConfigureAwait(false))
                {
                    try
                    {
                        await work().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[Federation] Watch party step failed");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Shutting down.
            }
        }

        /// <summary>One link's live pieces.</summary>
        internal sealed class LinkHandle
        {
            public LinkHandle(Guid partyId, PartyLink logic, SessionInfo bridge, Guid groupId, RemoteServer? peer)
            {
                PartyId = partyId;
                Logic = logic;
                Bridge = bridge;
                GroupId = groupId;
                Peer = peer;
            }

            public Guid PartyId { get; }

            public PartyLink Logic { get; }

            public SessionInfo Bridge { get; }

            public Guid GroupId { get; }

            public RemoteServer? Peer { get; }

            public PartyBridgeController? Controller { get; set; }

            public DateTime LastPeerSuccess { get; set; } = DateTime.UtcNow;

            public bool Closed { get; set; }
        }

        /// <summary>A party this server hosts.</summary>
        internal sealed class HostParty
        {
            public HostParty(Guid partyId, Guid groupId, string name, User creator)
            {
                PartyId = partyId;
                GroupId = groupId;
                Name = name;
                Creator = creator;
            }

            public Guid PartyId { get; }

            public Guid GroupId { get; }

            public string Name { get; }

            public User Creator { get; }

            public HashSet<string> SharedWith { get; } = new(StringComparer.Ordinal);

            public Dictionary<string, LinkHandle> Links { get; } = new(StringComparer.Ordinal);

            public LinkHandle? Observer { get; set; }

            public DateTime LastAnnounce { get; set; }
        }

        /// <summary>A friend's party this server knows about (and may have joined).</summary>
        internal sealed class RemoteParty
        {
            public RemoteParty(Guid partyId, string hostServerId)
            {
                PartyId = partyId;
                HostServerId = hostServerId;
            }

            public Guid PartyId { get; }

            public string HostServerId { get; }

            public string Name { get; set; } = string.Empty;

            public List<string> Participants { get; set; } = new();

            public PartySnapshot? Snapshot { get; set; }

            public DateTime LastHeard { get; set; } = DateTime.UtcNow;

            public LinkHandle? Mirror { get; set; }
        }
    }

    /// <summary>
    /// The party link's connection to its session: receives every SyncPlay message the
    /// group sends the link and hands it to the party worker. Never blocks: Jellyfin
    /// calls this while holding the group's lock.
    /// </summary>
    public sealed class PartyBridgeController : ISessionController
    {
        private readonly Action<object> _deliver;
        private volatile bool _open = true;

        /// <summary>
        /// Initializes a new instance of the <see cref="PartyBridgeController"/> class.
        /// </summary>
        public PartyBridgeController(Action<object> deliver) => _deliver = deliver;

        /// <inheritdoc />
        public bool IsSessionActive => _open;

        /// <inheritdoc />
        public bool SupportsMediaControl => false;

        /// <summary>Stops delivering messages.</summary>
        public void Close() => _open = false;

        /// <inheritdoc />
        public Task SendMessage<T>(SessionMessageType name, Guid messageId, T data, CancellationToken cancellationToken)
        {
            if (_open && data is not null && name is SessionMessageType.SyncPlayCommand or SessionMessageType.SyncPlayGroupUpdate)
            {
                _deliver(data);
            }

            return Task.CompletedTask;
        }
    }
}
