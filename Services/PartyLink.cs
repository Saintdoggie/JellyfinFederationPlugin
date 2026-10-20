using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Model.SyncPlay;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>Which end of a party link this is.</summary>
    public enum PartyLinkRole
    {
        /// <summary>In the host's group, standing for one friend's server.</summary>
        HostSide,

        /// <summary>In a friend's copy of the party, standing for the host.</summary>
        RemoteSide
    }

    /// <summary>What the link asks its server's group to do (see <see cref="PartyLink.Plan"/>).</summary>
    public enum PartyActionKind
    {
        /// <summary>Replace the queue (<see cref="PartyAction.Queue"/>, <see cref="PartyAction.Index"/>, <see cref="PartyAction.PositionTicks"/>).</summary>
        SetQueue,

        /// <summary>Switch to another title already in the queue.</summary>
        SetItem,

        /// <summary>Pause.</summary>
        Pause,

        /// <summary>Resume.</summary>
        Unpause,

        /// <summary>Jump to <see cref="PartyAction.PositionTicks"/>.</summary>
        Seek,

        /// <summary>Stop.</summary>
        Stop,

        /// <summary>Report the link ready and stop being waited for.</summary>
        Ready,

        /// <summary>Make the group wait for the link (the linked server's viewers are loading).</summary>
        Hold
    }

    /// <summary>One request the link makes in its own group.</summary>
    /// <param name="Kind">What to do.</param>
    /// <param name="Queue">The new queue for SetQueue.</param>
    /// <param name="Index">The playing index for SetQueue, or the target index for SetItem.</param>
    /// <param name="PositionTicks">Position for SetQueue/Seek/Ready/Hold.</param>
    /// <param name="PlaylistItemId">Target for SetItem, current title for Ready/Hold.</param>
    public sealed record PartyAction(
        PartyActionKind Kind,
        IReadOnlyList<Guid>? Queue = null,
        int Index = 0,
        long PositionTicks = 0,
        Guid PlaylistItemId = default);

    /// <summary>What the link learned from a message its group sent it.</summary>
    public sealed class PartyObservation
    {
        /// <summary>Gets events to send to the linked server (a Snapshot is built by the caller from <see cref="SendQueue"/>).</summary>
        public List<PartyEvent> Forward { get; } = new();

        /// <summary>Gets or sets a value indicating whether the local queue changed and must be sent as a Snapshot.</summary>
        public bool SendQueue { get; set; }

        /// <summary>Gets or sets a value indicating whether the link should report itself ready (it isn't holding the group).</summary>
        public bool AutoReady { get; set; }

        /// <summary>Gets or sets a value indicating whether the link is no longer in its group.</summary>
        public bool Left { get; set; }

        /// <summary>Gets or sets a value indicating whether someone joined or left the group.</summary>
        public bool MembersChanged { get; set; }
    }

    /// <summary>
    /// One link of a watch party: a hidden member of this server's SyncPlay group that
    /// stands for the linked server's viewers. Pure logic (no Jellyfin calls) so every
    /// rule is unit tested.
    /// </summary>
    /// <remarks>
    /// What crosses between servers:
    /// <list type="bullet">
    /// <item>Real changes: a pause, a resume, a seek, a stop, a new queue or title. A
    /// group's state is always confirmed against Jellyfin's actual state before a change
    /// is sent, so a moment-old pause can't undo a resume that already happened.</item>
    /// <item>Loading, from a friend's server to the host: "Wait" while its viewers load or
    /// buffer, "Go" when they're ready. The host's group waits for the link meanwhile,
    /// exactly as it waits for any of its own viewers.</item>
    /// <item>Loading, from the host to friends: "Wait" pauses their copies until the host
    /// resolves, then its resume (or pause) is sent as usual. Friends never make the
    /// host's link wait on them in return, so two servers can't wait on each other.</item>
    /// </list>
    /// The last state both ends agreed on (<see cref="Synced"/>) recognises the echo of
    /// a change coming back from the other side, which is dropped.
    /// </remarks>
    public sealed class PartyLink
    {
        /// <summary>Wire kind: the linked server's viewers are loading (or the host is).</summary>
        public const string Wait = "Wait";

        /// <summary>Wire kind: the linked server's viewers finished loading.</summary>
        public const string Go = "Go";

        /// <summary>A pause is the same pause when the positions are this close.</summary>
        internal static readonly long PauseTolerance = TimeSpan.FromSeconds(2).Ticks;

        /// <summary>A seek is the same seek when the positions are this close.</summary>
        internal static readonly long SeekTolerance = TimeSpan.FromSeconds(1.5).Ticks;

        /// <summary>Heartbeat: drift tolerated while playing before a correcting seek.</summary>
        internal static readonly long PlayingDrift = TimeSpan.FromSeconds(3).Ticks;

        /// <summary>Heartbeat: drift tolerated while paused before a correcting seek.</summary>
        internal static readonly long PausedDrift = TimeSpan.FromSeconds(1).Ticks;

        /// <summary>Heartbeat: a group must have been settled this long before it's corrected.</summary>
        internal static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(3);

        /// <summary>
        /// Initializes a new instance of the <see cref="PartyLink"/> class.
        /// </summary>
        public PartyLink(PartyLinkRole role, DateTime now)
        {
            Role = role;
            Local.StateSince = now;
        }

        /// <summary>Gets which end this is.</summary>
        public PartyLinkRole Role { get; }

        /// <summary>Gets what this server's group is doing, as the link has seen it.</summary>
        public PartyGroupModel Local { get; } = new();

        /// <summary>Gets the last state both servers agreed on.</summary>
        public PartySyncedState Synced { get; } = new();

        /// <summary>
        /// Gets or sets a value indicating whether (host side) the group waits for this link
        /// because the friend's viewers are loading. True from the moment a friend's server
        /// joins until it has loaded.
        /// </summary>
        public bool Holding { get; set; }

        /// <summary>Gets or sets when <see cref="Holding"/> started.</summary>
        public DateTime HoldingSince { get; set; }

        /// <summary>Gets or sets a value indicating whether (remote side) the first snapshot from the host was applied.</summary>
        public bool Initialized { get; set; }

        /// <summary>Gets or sets a value indicating whether (remote side) this copy is paused because the host is loading.</summary>
        public bool PausedForHost { get; set; }

        /// <summary>Gets or sets a value indicating whether (remote side) a Wait was sent and its Go is still owed.</summary>
        public bool WaitSent { get; set; }

        /// <summary>
        /// Takes in one message Jellyfin sent the link as a group member, together with the
        /// group's actual state right now, and decides what to tell the linked server.
        /// </summary>
        public PartyObservation Observe(object message, GroupStateType actual, DateTime now)
        {
            var result = new PartyObservation();
            switch (message)
            {
                case SendCommand command:
                    ObserveCommand(command, actual, now, result);
                    break;
                case GroupUpdate<PlayQueueUpdate> { Data: { } queue }:
                    ObserveQueue(queue, now, result);
                    break;
                case GroupUpdate<string> update when update.Type is GroupUpdateType.UserJoined or GroupUpdateType.UserLeft:
                    result.MembersChanged = true;
                    break;
                case GroupUpdate<string> update when update.Type is GroupUpdateType.GroupLeft or GroupUpdateType.NotInGroup or GroupUpdateType.GroupDoesNotExist:
                    result.Left = true;
                    return result;
            }

            Settle(actual, now, result);
            return result;
        }

        /// <summary>
        /// Takes in the group's actual state when no message arrived (some changes reach no
        /// member at all, such as a group that loaded from idle into paused).
        /// </summary>
        public PartyObservation Refresh(GroupStateType actual, DateTime now)
        {
            var result = new PartyObservation();
            Settle(actual, now, result);
            return result;
        }

        /// <summary>
        /// Decides what to ask this server's group to do for an event from the linked
        /// server. <paramref name="mappedQueue"/> is this server's copy of the event's
        /// queue (null when it has none of the titles, or the event has no queue).
        /// </summary>
        public IReadOnlyList<PartyAction> Plan(PartyEvent e, IReadOnlyList<Guid>? mappedQueue, DateTime now)
        {
            var actions = new List<PartyAction>();
            switch (e.Kind)
            {
                case PartyEventKind.Pause:
                    Synced.Pause(e.PositionTicks, now);
                    PausedForHost = false;
                    if (Holding)
                    {
                        // The friend's viewers finished loading into a pause.
                        actions.Add(new PartyAction(PartyActionKind.Pause));
                        actions.Add(Release(now));
                    }
                    else if (Local.State is GroupStateType.Playing or GroupStateType.Waiting)
                    {
                        actions.Add(new PartyAction(PartyActionKind.Pause));
                    }
                    else if (Local.State == GroupStateType.Paused && Math.Abs(Local.PositionAt(now) - e.PositionTicks) > PauseTolerance)
                    {
                        actions.Add(new PartyAction(PartyActionKind.Seek, PositionTicks: e.PositionTicks));
                    }

                    break;
                case PartyEventKind.Unpause:
                    Synced.Unpause(e.PositionTicks, now);
                    PausedForHost = false;
                    if (Local.State == GroupStateType.Paused)
                    {
                        actions.Add(new PartyAction(PartyActionKind.Unpause));
                    }
                    else if (Local.State == GroupStateType.Waiting)
                    {
                        // Pause first so the resume means "resume once everyone here has
                        // loaded" rather than "start now even if they haven't".
                        actions.Add(new PartyAction(PartyActionKind.Pause));
                        actions.Add(new PartyAction(PartyActionKind.Unpause));
                    }

                    if (Holding)
                    {
                        actions.Add(Release(now));
                    }

                    break;
                case PartyEventKind.Seek:
                    Synced.Seek(e.PositionTicks, now);
                    if (Local.State != GroupStateType.Idle && Math.Abs(Local.PositionAt(now) - e.PositionTicks) > SeekTolerance)
                    {
                        actions.Add(new PartyAction(PartyActionKind.Seek, PositionTicks: e.PositionTicks));
                    }

                    break;
                case PartyEventKind.Stop:
                    Synced.Stopped = true;
                    if (Local.State != GroupStateType.Idle)
                    {
                        actions.Add(new PartyAction(PartyActionKind.Stop));
                    }

                    break;
                case Wait when Role == PartyLinkRole.HostSide:
                    if (!Holding && Local.Playlist.Count > 0)
                    {
                        Holding = true;
                        HoldingSince = now;
                        actions.Add(new PartyAction(PartyActionKind.Hold, PositionTicks: Local.PositionAt(now), PlaylistItemId: Local.PlayingPlaylistItemId));
                    }

                    break;
                case Wait when Role == PartyLinkRole.RemoteSide:
                    // The host is loading: pause here until it resolves.
                    if (Local.State is GroupStateType.Playing or GroupStateType.Waiting)
                    {
                        PausedForHost = true;
                        Synced.Pause(Local.PositionAt(now), now);
                        actions.Add(new PartyAction(PartyActionKind.Pause));
                    }

                    break;
                case Go:
                case PartyEventKind.Ready:
                    if (Role == PartyLinkRole.HostSide && Holding)
                    {
                        actions.Add(Release(now));
                    }

                    break;
                case PartyEventKind.Snapshot:
                case PartyEventKind.Heartbeat:
                    PlanSnapshot(e, mappedQueue, now, actions);
                    break;
            }

            return actions;
        }

        /// <summary>The link's own Ready report for the current title and position.</summary>
        public PartyAction ReadyAction()
            => new(PartyActionKind.Ready, PositionTicks: Local.PositionTicks, PlaylistItemId: Local.PlayingPlaylistItemId);

        /// <summary>Stops holding the group (host side) and reports ready.</summary>
        public PartyAction Release(DateTime now)
        {
            Holding = false;
            return ReadyAction();
        }

        /// <summary>The local queue as this server's item ids, in order.</summary>
        public IReadOnlyList<Guid> LocalQueue() => Local.Playlist.Select(p => p.ItemId).ToList();

        /// <summary>
        /// Brings the model in line with the group's actual state and decides what a
        /// change of state means for the linked server.
        /// </summary>
        private void Settle(GroupStateType actual, DateTime now, PartyObservation result)
        {
            var previous = Local.State;
            if (actual != previous)
            {
                Local.SetState(actual, now);
            }

            if (actual == GroupStateType.Waiting)
            {
                if (previous != GroupStateType.Waiting)
                {
                    // The group now waits for everyone to load the title, the link
                    // included, unless the link is the one holding it. Only on entering:
                    // while it waits, Jellyfin answers each Ready with "pause when ready".
                    result.AutoReady = !Holding && Local.Playlist.Count > 0;
                    EnterWaiting(previous, now, result);
                }

                return;
            }

            if (previous == GroupStateType.Waiting || (previous == GroupStateType.Idle && actual != GroupStateType.Idle))
            {
                LeaveWaiting(actual, now, result);
                return;
            }

            if (Role == PartyLinkRole.RemoteSide && actual == GroupStateType.Playing)
            {
                // Someone here pressed play: the host's earlier "wait" no longer applies.
                PausedForHost = false;
            }

            if (previous == GroupStateType.Paused && actual == GroupStateType.Playing)
            {
                ForwardUnpause(Local.PositionTicks, now, result);
            }
            else if (previous == GroupStateType.Playing && actual == GroupStateType.Paused)
            {
                ForwardPause(Local.PositionAt(now), now, result);
            }
        }

        private void EnterWaiting(GroupStateType previous, DateTime now, PartyObservation result)
        {
            if (Role == PartyLinkRole.RemoteSide)
            {
                // This server's viewers are loading (a seek, a new title, a buffering
                // player): the host holds everyone until they're done. A pause the host
                // itself asked for doesn't count.
                if (Initialized && !WaitSent && !PausedForHost)
                {
                    WaitSent = true;
                    result.Forward.Add(new PartyEvent { Kind = Wait });
                }

                return;
            }

            // Host side: the host is loading. Friends pause until it resolves, except
            // the friend this link is holding for (it's loading too).
            if (previous == GroupStateType.Playing && !Holding && Synced.IsPlaying == true)
            {
                Synced.Pause(Local.PositionAt(now), now);
                result.Forward.Add(new PartyEvent { Kind = Wait });
            }
        }

        private void LeaveWaiting(GroupStateType actual, DateTime now, PartyObservation result)
        {
            if (Role == PartyLinkRole.RemoteSide && (WaitSent || PausedForHost))
            {
                // Done loading. If the host made this copy pause, the host decides what
                // happens next; otherwise tell it whether this side resolved to playing
                // or to a pause.
                var owedGo = WaitSent;
                WaitSent = false;
                if (PausedForHost)
                {
                    if (owedGo)
                    {
                        result.Forward.Add(new PartyEvent { Kind = Go });
                    }

                    return;
                }

                if (actual == GroupStateType.Playing)
                {
                    if (Synced.IsPlaying == true)
                    {
                        result.Forward.Add(new PartyEvent { Kind = Go });
                    }
                    else
                    {
                        Synced.Unpause(Local.PositionTicks, now);
                        result.Forward.Add(new PartyEvent { Kind = PartyEventKind.Unpause, PositionTicks = Local.PositionTicks });
                    }
                }
                else if (Synced.IsPlaying != false)
                {
                    Synced.Pause(Local.PositionTicks, now);
                    result.Forward.Add(new PartyEvent { Kind = PartyEventKind.Pause, PositionTicks = Local.PositionTicks });
                }
                else
                {
                    result.Forward.Add(new PartyEvent { Kind = Go });
                }

                return;
            }

            if (actual == GroupStateType.Paused)
            {
                ForwardPause(Local.PositionTicks, now, result);
            }
            else if (actual == GroupStateType.Playing)
            {
                ForwardUnpause(Local.PositionTicks, now, result);
            }
        }

        private void PlanSnapshot(PartyEvent e, IReadOnlyList<Guid>? mappedQueue, DateTime now, List<PartyAction> actions)
        {
            var snapshot = e.Snapshot;
            if (snapshot == null)
            {
                return;
            }

            var first = Role == PartyLinkRole.RemoteSide && !Initialized;
            if (mappedQueue == null || mappedQueue.Count == 0)
            {
                // Nothing here to play (or an empty queue): nothing to follow.
                if (first)
                {
                    Initialized = true;
                    Synced.SetQueue(mappedQueue ?? Array.Empty<Guid>(), snapshot.Index);
                }

                return;
            }

            var index = Math.Clamp(snapshot.Index, 0, mappedQueue.Count - 1);
            var sameQueue = LocalQueue().SequenceEqual(mappedQueue);
            Synced.SetQueue(mappedQueue, index);
            if (snapshot.IsPlaying)
            {
                Synced.Unpause(snapshot.PositionTicks, now);
            }
            else
            {
                Synced.Pause(snapshot.PositionTicks, now);
            }

            if (!sameQueue)
            {
                actions.Add(new PartyAction(PartyActionKind.SetQueue, mappedQueue, index, snapshot.PositionTicks));
            }
            else if (Local.Index != index && index < Local.Playlist.Count)
            {
                actions.Add(new PartyAction(PartyActionKind.SetItem, Index: index, PlaylistItemId: Local.Playlist[index].PlaylistItemId));
            }
            else if (first)
            {
                // Already on the right title (the joiner was watching it): line up the position.
                if (Math.Abs(Local.PositionAt(now) - snapshot.PositionTicks) > SeekTolerance)
                {
                    actions.Add(new PartyAction(PartyActionKind.Seek, PositionTicks: snapshot.PositionTicks));
                }
            }
            else if (e.Kind == PartyEventKind.Heartbeat)
            {
                if (snapshot.Stable && !WaitSent)
                {
                    // The host has settled, so any pause it asked for is over.
                    PausedForHost = false;
                }

                PlanCorrection(snapshot, now, actions);
                return;
            }

            if (first)
            {
                // Load paused and owe the host a Go: it holds everyone until this server
                // has loaded, then resumes everyone together.
                Initialized = true;
                PausedForHost = true;
                WaitSent = true;
                actions.Add(new PartyAction(PartyActionKind.Pause));
            }
        }

        private void PlanCorrection(PartySnapshot snapshot, DateTime now, List<PartyAction> actions)
        {
            if (!snapshot.Stable
                || PausedForHost
                || WaitSent
                || Local.State is GroupStateType.Waiting or GroupStateType.Idle
                || now - Local.StateSince < SettleTime)
            {
                return;
            }

            var playing = Local.State == GroupStateType.Playing;
            if (Math.Abs(Local.PositionAt(now) - snapshot.PositionTicks) > (snapshot.IsPlaying ? PlayingDrift : PausedDrift))
            {
                actions.Add(new PartyAction(PartyActionKind.Seek, PositionTicks: snapshot.PositionTicks));
            }

            if (snapshot.IsPlaying != playing)
            {
                actions.Add(new PartyAction(snapshot.IsPlaying ? PartyActionKind.Unpause : PartyActionKind.Pause));
            }
        }

        private void ObserveCommand(SendCommand command, GroupStateType actual, DateTime now, PartyObservation result)
        {
            var position = command.PositionTicks ?? 0;
            switch (command.Command)
            {
                case SendCommandType.Unpause:
                    Local.Play(position, command.When, now);
                    break;
                case SendCommandType.Pause:
                    Local.Pause(position, now, keepState: true);
                    if (actual == GroupStateType.Paused && Local.State == GroupStateType.Playing)
                    {
                        // A pause from playing (no loading in between): someone pressed pause.
                        Local.SetState(GroupStateType.Paused, now);
                        ForwardPause(position, now, result);
                    }

                    break;
                case SendCommandType.Seek:
                    Local.Seek(position, now);
                    result.AutoReady = !Holding;
                    if (Math.Abs(Synced.PositionAt(now) - position) > SeekTolerance)
                    {
                        Synced.Seek(position, now);
                        result.Forward.Add(new PartyEvent { Kind = PartyEventKind.Seek, PositionTicks = position });
                    }

                    break;
                case SendCommandType.Stop:
                    Local.SetState(GroupStateType.Idle, now);
                    if (!Synced.Stopped)
                    {
                        Synced.Stopped = true;
                        result.Forward.Add(new PartyEvent { Kind = PartyEventKind.Stop });
                    }

                    break;
            }
        }

        private void ObserveQueue(PlayQueueUpdate queue, DateTime now, PartyObservation result)
        {
            Local.Playlist = (queue.Playlist ?? Array.Empty<SyncPlayQueueItem>()).Select(i => (i.ItemId, i.PlaylistItemId)).ToList();
            Local.Index = queue.PlayingItemIndex;
            Local.Shuffle = queue.ShuffleMode;
            Local.Repeat = queue.RepeatMode;
            Local.PositionTicks = queue.StartPositionTicks;
            Local.At = now;
            if (queue.Reason is PlayQueueUpdateReason.NewPlaylist or PlayQueueUpdateReason.SetCurrentItem
                or PlayQueueUpdateReason.NextItem or PlayQueueUpdateReason.PreviousItem)
            {
                // A new title resets everyone to loading, the link included.
                result.AutoReady = !Holding && Local.Playlist.Count > 0;
            }

            var ids = LocalQueue();
            if (!Synced.SameQueue(ids, Local.Index))
            {
                Synced.SetQueue(ids, Local.Index);
                result.SendQueue = true;
            }
        }

        private void ForwardPause(long position, DateTime now, PartyObservation result)
        {
            if (Synced.IsPlaying != false || Math.Abs(Synced.PositionAt(now) - position) > PauseTolerance)
            {
                Synced.Pause(position, now);
                result.Forward.Add(new PartyEvent { Kind = PartyEventKind.Pause, PositionTicks = position });
            }
        }

        private void ForwardUnpause(long position, DateTime now, PartyObservation result)
        {
            if (Synced.IsPlaying != true)
            {
                Synced.Unpause(position, now);
                result.Forward.Add(new PartyEvent { Kind = PartyEventKind.Unpause, PositionTicks = position });
            }
        }
    }

    /// <summary>A party link's view of its own server's group.</summary>
    public sealed class PartyGroupModel
    {
        /// <summary>Gets or sets the group state.</summary>
        public GroupStateType State { get; set; } = GroupStateType.Idle;

        /// <summary>Gets or sets when the state last changed.</summary>
        public DateTime StateSince { get; set; }

        /// <summary>Gets or sets the queue: item id and the group's playlist entry id.</summary>
        public List<(Guid ItemId, Guid PlaylistItemId)> Playlist { get; set; } = new();

        /// <summary>Gets or sets the playing index.</summary>
        public int Index { get; set; }

        /// <summary>Gets or sets the position at <see cref="At"/>.</summary>
        public long PositionTicks { get; set; }

        /// <summary>Gets or sets when <see cref="PositionTicks"/> applied.</summary>
        public DateTime At { get; set; }

        /// <summary>Gets or sets a value indicating whether playback advances from <see cref="At"/>.</summary>
        public bool IsPlaying { get; set; }

        /// <summary>Gets or sets the shuffle mode.</summary>
        public GroupShuffleMode Shuffle { get; set; }

        /// <summary>Gets or sets the repeat mode.</summary>
        public GroupRepeatMode Repeat { get; set; } = GroupRepeatMode.RepeatNone;

        /// <summary>Gets the playlist entry id of the playing title, or empty.</summary>
        public Guid PlayingPlaylistItemId => Index >= 0 && Index < Playlist.Count ? Playlist[Index].PlaylistItemId : Guid.Empty;

        /// <summary>The position at <paramref name="now"/>.</summary>
        public long PositionAt(DateTime now)
            => PositionTicks + (IsPlaying ? Math.Max(0, (now - At).Ticks) : 0);

        /// <summary>Records a state change.</summary>
        public void SetState(GroupStateType state, DateTime now)
        {
            if (State != state)
            {
                State = state;
                StateSince = now;
            }

            if (state != GroupStateType.Playing && IsPlaying)
            {
                PositionTicks = PositionAt(now);
                At = now;
                IsPlaying = false;
            }
        }

        /// <summary>Playback runs from <paramref name="position"/> starting at <paramref name="when"/>.</summary>
        public void Play(long position, DateTime when, DateTime now)
        {
            PositionTicks = position;
            At = when;
            IsPlaying = true;
        }

        /// <summary>Playback paused at <paramref name="position"/>.</summary>
        public void Pause(long position, DateTime now, bool keepState = false)
        {
            if (!keepState && State == GroupStateType.Playing)
            {
                SetState(GroupStateType.Paused, now);
            }

            PositionTicks = position;
            At = now;
            IsPlaying = false;
        }

        /// <summary>Jumped to <paramref name="position"/>.</summary>
        public void Seek(long position, DateTime now)
        {
            PositionTicks = position;
            At = now;
            IsPlaying = false;
        }
    }

    /// <summary>The last state both ends of a party link agreed on.</summary>
    public sealed class PartySyncedState
    {
        private List<Guid> _queue = new();

        /// <summary>Gets or sets whether playback runs (null before anything was agreed).</summary>
        public bool? IsPlaying { get; set; }

        /// <summary>Gets or sets the position at <see cref="At"/>.</summary>
        public long PositionTicks { get; set; }

        /// <summary>Gets or sets when <see cref="PositionTicks"/> applied.</summary>
        public DateTime At { get; set; }

        /// <summary>Gets or sets a value indicating whether playback was stopped.</summary>
        public bool Stopped { get; set; } = true;

        /// <summary>Gets the agreed playing index.</summary>
        public int Index { get; private set; }

        /// <summary>The agreed position at <paramref name="now"/>.</summary>
        public long PositionAt(DateTime now)
            => PositionTicks + (IsPlaying == true ? Math.Max(0, (now - At).Ticks) : 0);

        /// <summary>Whether a local queue is the agreed one.</summary>
        public bool SameQueue(IReadOnlyList<Guid> queue, int index) => Index == index && _queue.SequenceEqual(queue);

        /// <summary>Agrees on a queue.</summary>
        public void SetQueue(IReadOnlyList<Guid> queue, int index)
        {
            _queue = queue.ToList();
            Index = index;
            Stopped = false;
        }

        /// <summary>Agrees on a pause.</summary>
        public void Pause(long position, DateTime now)
        {
            IsPlaying = false;
            PositionTicks = position;
            At = now;
            Stopped = false;
        }

        /// <summary>Agrees on playback running.</summary>
        public void Unpause(long position, DateTime now)
        {
            IsPlaying = true;
            PositionTicks = position;
            At = now;
            Stopped = false;
        }

        /// <summary>Agrees on a jump.</summary>
        public void Seek(long position, DateTime now)
        {
            PositionTicks = position;
            At = now;
            Stopped = false;
        }
    }
}
