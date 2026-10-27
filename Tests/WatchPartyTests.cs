using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Model.Session;
using MediaBrowser.Model.SyncPlay;
using Microsoft.AspNetCore.Authorization;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

/// <summary>
/// Watch parties with friends: the rules a party link follows to keep two servers'
/// SyncPlay groups in step without echoes, loops, stale messages or two servers
/// waiting on each other.
/// </summary>
public class WatchPartyTests
{
    private static readonly Guid Group = Guid.NewGuid();
    private static readonly DateTime T0 = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
    private static readonly long S = TimeSpan.TicksPerSecond;

    private static readonly Guid Item = Guid.NewGuid();
    private static readonly SyncPlayQueueItem Entry = new(Item);

    private static SendCommand Command(SendCommandType type, long seconds, DateTime? when = null)
        => new(Group, Entry.PlaylistItemId, when ?? T0, type, seconds * S, when ?? T0);

    private static SyncPlayPlayQueueUpdate Queue(PlayQueueUpdateReason reason = PlayQueueUpdateReason.NewPlaylist, long seconds = 0, params Guid[] items)
    {
        var list = items.Length == 0 ? new List<SyncPlayQueueItem> { Entry } : items.Select(i => new SyncPlayQueueItem(i)).ToList();
        return new SyncPlayPlayQueueUpdate(Group, new PlayQueueUpdate(reason, T0, list, 0, seconds * S, false, GroupShuffleMode.Sorted, GroupRepeatMode.RepeatNone));
    }

    private static SyncPlayStateUpdate State(GroupStateType state)
        => new(Group, new GroupStateUpdate(state, PlaybackRequestType.Ready));

    /// <summary>A link whose group has the title loaded and is playing, agreed with the other side.</summary>
    private static PartyLink Playing(PartyLinkRole role)
    {
        var link = new PartyLink(role, T0) { Initialized = true };
        link.Observe(Queue(), GroupStateType.Waiting, T0);
        link.Observe(Command(SendCommandType.Unpause, 10), GroupStateType.Playing, T0);
        link.Synced.Unpause(10 * S, T0);
        return link;
    }

    private static string Kinds(PartyObservation o) => string.Join(",", o.Forward.Select(f => f.Kind));

    private static string Kinds(IEnumerable<PartyAction> actions) => string.Join(",", actions.Select(a => a.Kind));

    [Fact]
    public void PartyWire_RejectsOversizedSnapshotsAndNestedData()
    {
        PartyItemRef Item() => new()
        {
            Owners = new List<PartyOwnerRef> { new PartyOwnerRef { Fid = "friend", Id = Guid.NewGuid().ToString("N") } }
        };
        var snapshot = new PartySnapshot { Items = Enumerable.Range(0, 500).Select(_ => Item()).ToList() };
        Assert.True(PartyWireValidation.ValidSnapshot(snapshot));

        snapshot.Items.Add(Item());
        Assert.False(PartyWireValidation.ValidSnapshot(snapshot));
        Assert.False(PartyWireValidation.ValidAnnouncement(new PartyAnnouncement
        {
            Participants = Enumerable.Range(0, 21).Select(i => $"user-{i}").ToList()
        }));
    }

    [Fact]
    public void PartyLabels_RemoveMarkupAndControlCharacters()
    {
        var cleaned = FederatedPartyService.Clean("<script>alert(1)</script>\n", 80);
        Assert.Equal("scriptalert(1)/script", cleaned);
        Assert.DoesNotContain('<', cleaned!);
        Assert.DoesNotContain('>', cleaned!);
    }

    [Fact]
    public void UserPause_IsSentOnce_AndItsEchoIsDropped()
    {
        var link = Playing(PartyLinkRole.HostSide);
        var pause = link.Observe(Command(SendCommandType.Pause, 20), GroupStateType.Paused, T0.AddSeconds(10));
        Assert.Equal("Pause", Kinds(pause));
        Assert.Equal(20 * S, pause.Forward[0].PositionTicks);

        // Same pause again (the friend's echo, or Jellyfin re-telling the link): nothing.
        Assert.Empty(link.Observe(Command(SendCommandType.Pause, 20), GroupStateType.Paused, T0.AddSeconds(11)).Forward);
    }

    [Fact]
    public void ApplyingTheFriendsPause_DoesNotSendItBack()
    {
        var link = Playing(PartyLinkRole.RemoteSide);
        var actions = link.Plan(new PartyEvent { Kind = PartyEventKind.Pause, PositionTicks = 20 * S }, null, T0.AddSeconds(10));
        Assert.Equal("Pause", Kinds(actions));
        Assert.Empty(link.Observe(Command(SendCommandType.Pause, 20), GroupStateType.Paused, T0.AddSeconds(10)).Forward);
        Assert.Empty(link.Observe(State(GroupStateType.Paused), GroupStateType.Paused, T0.AddSeconds(10)).Forward);
    }

    [Fact]
    public void StalePause_AfterTheGroupAlreadyResumed_IsNotSent()
    {
        var link = Playing(PartyLinkRole.RemoteSide);

        // A pause emitted a moment ago, but the group is playing again by the time it's read.
        Assert.Empty(link.Observe(Command(SendCommandType.Pause, 15), GroupStateType.Playing, T0.AddSeconds(5)).Forward);
        Assert.Equal(GroupStateType.Playing, link.Local.State);
    }

    [Fact]
    public void LoadingPause_IsNotAUserPause_TheRemoteAsksTheHostToWait()
    {
        var link = Playing(PartyLinkRole.RemoteSide);

        // A viewer here starts buffering: Jellyfin pauses the others and waits.
        var waiting = link.Observe(Command(SendCommandType.Pause, 30), GroupStateType.Waiting, T0.AddSeconds(20));
        Assert.Equal("Wait", Kinds(waiting));
        Assert.True(link.WaitSent);
        Assert.Empty(link.Observe(State(GroupStateType.Waiting), GroupStateType.Waiting, T0.AddSeconds(20)).Forward);

        // Loaded and resumed: the host only needs to hear "go" (both were playing).
        var resumed = link.Observe(Command(SendCommandType.Unpause, 30, T0.AddSeconds(25)), GroupStateType.Playing, T0.AddSeconds(25));
        Assert.Equal("Go", Kinds(resumed));
        Assert.False(link.WaitSent);
    }

    [Fact]
    public void RemoteResolvingToAPause_TellsTheHostToPause()
    {
        var link = Playing(PartyLinkRole.RemoteSide);
        link.Observe(State(GroupStateType.Waiting), GroupStateType.Waiting, T0.AddSeconds(20));

        // Someone here paused while it loaded.
        var paused = link.Refresh(GroupStateType.Paused, T0.AddSeconds(22));
        Assert.Equal("Pause", Kinds(paused));
    }

    [Fact]
    public void HostHoldsForALoadingFriend_AndReleasesOnGo()
    {
        var link = Playing(PartyLinkRole.HostSide);
        var hold = link.Plan(new PartyEvent { Kind = PartyLink.Wait }, null, T0.AddSeconds(20));
        Assert.Equal("Hold", Kinds(hold));
        Assert.Equal(Entry.PlaylistItemId, hold[0].PlaylistItemId);
        Assert.True(link.Holding);
        Assert.Empty(link.Plan(new PartyEvent { Kind = PartyLink.Wait }, null, T0.AddSeconds(21)));

        // While holding, the link never reports itself ready on its own.
        Assert.False(link.Observe(State(GroupStateType.Waiting), GroupStateType.Waiting, T0.AddSeconds(21)).AutoReady);

        Assert.Equal("Ready", Kinds(link.Plan(new PartyEvent { Kind = PartyLink.Go }, null, T0.AddSeconds(25))));
        Assert.False(link.Holding);
    }

    [Fact]
    public void HostHolding_FriendLoadedIntoAPause_PausesThenReleases()
    {
        var link = Playing(PartyLinkRole.HostSide);
        link.Plan(new PartyEvent { Kind = PartyLink.Wait }, null, T0.AddSeconds(20));
        link.Observe(State(GroupStateType.Waiting), GroupStateType.Waiting, T0.AddSeconds(20));
        Assert.Equal("Pause,Ready", Kinds(link.Plan(new PartyEvent { Kind = PartyEventKind.Pause, PositionTicks = 30 * S }, null, T0.AddSeconds(25))));
    }

    [Fact]
    public void HostLoading_PausesFriends_ExceptTheOneItWaitsFor()
    {
        var other = Playing(PartyLinkRole.HostSide);
        Assert.Equal("Wait", Kinds(other.Observe(State(GroupStateType.Waiting), GroupStateType.Waiting, T0.AddSeconds(20))));

        var cause = Playing(PartyLinkRole.HostSide);
        cause.Plan(new PartyEvent { Kind = PartyLink.Wait }, null, T0.AddSeconds(20));
        Assert.Empty(cause.Observe(State(GroupStateType.Waiting), GroupStateType.Waiting, T0.AddSeconds(20)).Forward);

        // When the host resumes, the paused friend is told to resume.
        Assert.Equal("Unpause", Kinds(other.Observe(Command(SendCommandType.Unpause, 40, T0.AddSeconds(25)), GroupStateType.Playing, T0.AddSeconds(25))));
    }

    [Fact]
    public void RemotePausedForTheHost_DoesNotPauseTheHostBack()
    {
        var link = Playing(PartyLinkRole.RemoteSide);
        Assert.Equal("Pause", Kinds(link.Plan(new PartyEvent { Kind = PartyLink.Wait }, null, T0.AddSeconds(20))));
        Assert.True(link.PausedForHost);
        Assert.Empty(link.Observe(Command(SendCommandType.Pause, 30), GroupStateType.Paused, T0.AddSeconds(20)).Forward);

        Assert.Equal("Unpause", Kinds(link.Plan(new PartyEvent { Kind = PartyEventKind.Unpause, PositionTicks = 30 * S }, null, T0.AddSeconds(26))));
        Assert.False(link.PausedForHost);
        Assert.Empty(link.Observe(Command(SendCommandType.Unpause, 30, T0.AddSeconds(26)), GroupStateType.Playing, T0.AddSeconds(26)).Forward);
    }

    [Fact]
    public void ResumeWhileTheGroupIsStillLoading_NeverForcesUnloadedViewers()
    {
        var link = Playing(PartyLinkRole.RemoteSide);
        link.Observe(State(GroupStateType.Waiting), GroupStateType.Waiting, T0.AddSeconds(20));

        // Pause first: then "resume" means "once everyone here has loaded".
        Assert.Equal("Pause,Unpause", Kinds(link.Plan(new PartyEvent { Kind = PartyEventKind.Unpause, PositionTicks = 30 * S }, null, T0.AddSeconds(21))));
    }

    [Fact]
    public void FirstSnapshot_LoadsPaused_AndOwesTheHostAGo()
    {
        var link = new PartyLink(PartyLinkRole.RemoteSide, T0);
        var local = Guid.NewGuid();
        var actions = link.Plan(
            new PartyEvent { Kind = PartyEventKind.Snapshot, Snapshot = new PartySnapshot { Items = { new PartyItemRef() }, PositionTicks = 50 * S, IsPlaying = false } },
            new[] { local },
            T0);

        Assert.Equal("SetQueue,Pause", Kinds(actions));
        Assert.Equal(new[] { local }, actions[0].Queue);
        Assert.Equal(50 * S, actions[0].PositionTicks);
        Assert.True(link.Initialized && link.WaitSent && link.PausedForHost);

        // Its own loading isn't sent back as a Wait, and finishing it (silently, from
        // idle into paused) is reported with a Go, not a Pause.
        Assert.Empty(link.Observe(Queue(items: local), GroupStateType.Waiting, T0).Forward);
        Assert.Equal("Go", Kinds(link.Refresh(GroupStateType.Paused, T0.AddSeconds(2))));
        Assert.False(link.WaitSent);
    }

    [Fact]
    public void FirstSnapshot_ThisServerHasNothingToPlay_JustJoins()
    {
        var link = new PartyLink(PartyLinkRole.RemoteSide, T0);
        var actions = link.Plan(new PartyEvent { Kind = PartyEventKind.Snapshot, Snapshot = new PartySnapshot { Items = { new PartyItemRef() } } }, null, T0);
        Assert.Empty(actions);
        Assert.True(link.Initialized);
        Assert.False(link.WaitSent);
    }

    [Fact]
    public void NewQueue_IsSentOnce_AndTheFriendsCopyOfItIsNotSentBack()
    {
        var link = Playing(PartyLinkRole.HostSide);
        var next = Guid.NewGuid();
        Assert.True(link.Observe(Queue(items: next), GroupStateType.Waiting, T0.AddSeconds(5)).SendQueue);
        Assert.False(link.Observe(Queue(PlayQueueUpdateReason.SetCurrentItem, items: next), GroupStateType.Waiting, T0.AddSeconds(6)).SendQueue);

        var remote = Playing(PartyLinkRole.RemoteSide);
        var mapped = new[] { Guid.NewGuid() };
        Assert.Equal("SetQueue", Kinds(remote.Plan(new PartyEvent { Kind = PartyEventKind.Snapshot, Snapshot = new PartySnapshot { Items = { new PartyItemRef() }, IsPlaying = true } }, mapped, T0.AddSeconds(5))));
        Assert.False(remote.Observe(Queue(items: mapped), GroupStateType.Waiting, T0.AddSeconds(5)).SendQueue);
    }

    [Fact]
    public void LinkReportsReady_OnlyWhenTheGroupStartsWaiting_NotOnEveryReply()
    {
        var link = Playing(PartyLinkRole.HostSide);
        Assert.True(link.Observe(State(GroupStateType.Waiting), GroupStateType.Waiting, T0.AddSeconds(5)).AutoReady);

        // Jellyfin answers a Ready during a wait with "pause when ready": no new Ready (that looped).
        Assert.False(link.Observe(Command(SendCommandType.Pause, 15), GroupStateType.Waiting, T0.AddSeconds(5)).AutoReady);

        // A seek resets everyone to loading.
        Assert.True(link.Observe(Command(SendCommandType.Seek, 60), GroupStateType.Waiting, T0.AddSeconds(6)).AutoReady);
    }

    [Fact]
    public void Seek_IsSentOnce_WithinTolerance()
    {
        var link = Playing(PartyLinkRole.HostSide);
        var seek = link.Observe(Command(SendCommandType.Seek, 90), GroupStateType.Waiting, T0.AddSeconds(5));
        Assert.Contains(seek.Forward, f => f.Kind == PartyEventKind.Seek && f.PositionTicks == 90 * S);
        Assert.DoesNotContain(link.Observe(Command(SendCommandType.Seek, 90), GroupStateType.Waiting, T0.AddSeconds(5)).Forward, f => f.Kind == PartyEventKind.Seek);

        var remote = Playing(PartyLinkRole.RemoteSide);
        Assert.Empty(remote.Plan(new PartyEvent { Kind = PartyEventKind.Seek, PositionTicks = 11 * S }, null, T0.AddSeconds(0.5)));
        Assert.Equal("Seek", Kinds(remote.Plan(new PartyEvent { Kind = PartyEventKind.Seek, PositionTicks = 90 * S }, null, T0.AddSeconds(1))));
    }

    [Fact]
    public void Heartbeat_CorrectsDrift_OnlyWhenBothSidesHaveSettled()
    {
        var link = Playing(PartyLinkRole.RemoteSide);
        var mapped = new[] { Item };
        PartyEvent Beat(long seconds, bool playing, bool stable = true)
            => new() { Kind = PartyEventKind.Heartbeat, Snapshot = new PartySnapshot { Items = { new PartyItemRef() }, PositionTicks = seconds * S, IsPlaying = playing, Stable = stable } };

        // In step (10 s + 10 s elapsed = 20 s): nothing.
        Assert.Empty(link.Plan(Beat(20, true), mapped, T0.AddSeconds(10)));

        // Five seconds behind the host: jump.
        Assert.Equal("Seek", Kinds(link.Plan(Beat(25, true), mapped, T0.AddSeconds(10))));

        // The host paused but this copy missed it: pause.
        Assert.Equal("Pause", Kinds(link.Plan(Beat(20, false), mapped, T0.AddSeconds(10))));

        // The host is loading: leave it alone.
        Assert.Empty(link.Plan(Beat(90, true, stable: false), mapped, T0.AddSeconds(10)));

        // This copy only just changed state: leave it alone.
        link.Observe(Command(SendCommandType.Pause, 20), GroupStateType.Paused, T0.AddSeconds(10));
        Assert.Empty(link.Plan(Beat(90, true), mapped, T0.AddSeconds(11)));
    }

    [Fact]
    public void LeavingTheGroup_IsNoticed()
    {
        var link = Playing(PartyLinkRole.RemoteSide);
        Assert.True(link.Observe(new SyncPlayGroupLeftUpdate(Group, "gone"), GroupStateType.Idle, T0).Left);
        Assert.True(link.Observe(new SyncPlayUserJoinedUpdate(Group, "bob"), GroupStateType.Playing, T0).MembersChanged);
    }

    [Fact]
    public void Labels_NameTheFriendAndWhoIsWatchingThere()
    {
        Assert.Equal("(friend-a)", FederatedPartyService.LinkLabel(null, "friend-a"));
        Assert.Equal("bob, carol (friend-a)", FederatedPartyService.LinkLabel(new[] { "bob", "carol" }, "friend-a"));
        Assert.Equal("a, b, c, d +2 (x)", FederatedPartyService.LinkLabel(new[] { "a", "b", "c", "d", "e", "f" }, "x"));
    }

    [Fact]
    public void ProviderIds_OnlyTheOnesWorthMatching()
    {
        var ids = PartyItemMapper.Providers(new Dictionary<string, string> { ["imdb"] = "tt1", ["Tmdb"] = " 2 ", ["FederationKey"] = "secret", ["Tvdb"] = "" });
        Assert.Equal(new Dictionary<string, string> { ["Imdb"] = "tt1", ["Tmdb"] = "2" }, ids);
        Assert.Null(PartyItemMapper.Providers(new Dictionary<string, string> { ["FederationKey"] = "x" }));
    }

    [Fact]
    public void GroupList_AddsFriendsPartiesOnce_AfterTheNativeGroups()
    {
        var native = new GroupInfoDto(Guid.NewGuid(), "Local", GroupStateType.Idle, new List<string>(), T0);
        var party = new GroupInfoDto(Guid.NewGuid(), "Movie night · friend-a", GroupStateType.Playing, new List<string> { "alice (friend-a)" }, T0);
        var merged = FederatedPartyFilter.Merge(new[] { native }, new[] { party, party });
        Assert.Equal(new[] { native.GroupId, party.GroupId }, merged.Select(g => g.GroupId));

        Assert.Equal(party.GroupId, FederatedPartyFilter.GroupIdOf(new { GroupId = party.GroupId }));
        Assert.Null(FederatedPartyFilter.GroupIdOf(new { Other = 1 }));
        Assert.Null(FederatedPartyFilter.GroupIdOf(null));
    }

    [Fact]
    public async Task LinkSession_PassesOnSyncPlayMessagesOnly_UntilClosed()
    {
        var delivered = new List<object>();
        var controller = new PartyBridgeController(delivered.Add);
        Assert.True(controller.IsSessionActive);
        Assert.False(controller.SupportsMediaControl);

        await controller.SendMessage(SessionMessageType.SyncPlayCommand, Guid.NewGuid(), Command(SendCommandType.Pause, 1), CancellationToken.None);
        await controller.SendMessage(SessionMessageType.Play, Guid.NewGuid(), "not for the link", CancellationToken.None);
        controller.Close();
        await controller.SendMessage(SessionMessageType.SyncPlayGroupUpdate, Guid.NewGuid(), State(GroupStateType.Paused), CancellationToken.None);

        Assert.Single(delivered);
        Assert.False(controller.IsSessionActive);
    }

    [Fact]
    public void Routes_FriendsUseTheirToken_ViewersSignIn_SettingsNeedAnAdmin()
    {
        System.Reflection.MethodInfo Method(string name)
            => typeof(Jellyfin.Plugin.Federation.Api.FederationController).GetMethod(name) ?? throw new InvalidOperationException(name);

        foreach (var name in new[] { "PeerPartyAnnounce", "PeerPartyJoin", "PeerPartyLeave", "PeerPartyEvent" })
        {
            Assert.Single(Method(name).GetCustomAttributes(typeof(AllowAnonymousAttribute), false));
        }

        foreach (var name in new[] { "CreateParty", "ShareParty", "GetPartyStatus" })
        {
            var authorize = Assert.Single(Method(name).GetCustomAttributes(typeof(AuthorizeAttribute), false).Cast<AuthorizeAttribute>());
            Assert.True(string.IsNullOrEmpty(authorize.Policy));
        }

        foreach (var name in new[] { "GetWatchPartySettings", "SetWatchPartySettings" })
        {
            Assert.Equal("RequiresElevation", Assert.Single(Method(name).GetCustomAttributes(typeof(AuthorizeAttribute), false).Cast<AuthorizeAttribute>()).Policy);
        }
    }
}
