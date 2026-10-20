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

        /// <summary>Report the link itself ready, so the group doesn't wait on it.</summary>
        Ready
    }

    /// <summary>One request the link makes in its own group.</summary>
    /// <param name="Kind">What to do.</param>
    /// <param name="Queue">The new queue for SetQueue.</param>
    /// <param name="Index">The playing index for SetQueue, or the target index for SetItem.</param>
    /// <param name="PositionTicks">Position for SetQueue/Seek/Ready.</param>
    /// <param name="PlaylistItemId">Target for SetItem, current title for Ready.</param>
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

        /// <summary>Gets or sets a value indicating whether the link should report itself ready.</summary>
        public bool AutoReady { get; set; }

        /// <summary>Gets or sets a value indicating whether the link is no longer in its group.</summary>
        public bool Left { get; set; }

        /// <summary>Gets or sets a value indicating whether someone joined or left the group.</summary>
        public bool MembersChanged { get; set; }

        /// <summary>Gets or sets a value indicating whether a joining remote server just finished loading (remote side).</summary>
        public bool ReportReady { get; set; }
    }

    /// <summary>
    /// One link of a watch party: a hidden member of this server's SyncPlay group that
    /// stands for the linked server's viewers. It keeps a model of what its group is
    /// doing (from the messages Jellyfin sends every member) and of the last state both
    /// servers agreed on, so a change made on one side is sent once and its echo from
    /// the other side is recognised and dropped. Pure logic: no Jellyfin calls, so
    /// every rule here is unit tested.
    /// </summary>
    public sealed class PartyLink
    {
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

        /// <summary>Gets or sets a value indicating whether (host side) the link holds the group until the friend has loaded.</summary>
        public bool HoldReady { get; set; }

        /// <summary>Gets or sets a value indicating whether (remote side) the first snapshot from the host was applied.</summary>
        public bool Initialized { get; set; }

        /// <summary>Gets or sets a value indicating whether (remote side) the host is still waiting for this server to load.</summary>
        public bool AwaitingLoad { get; set; }

        /// <summary>
        /// Takes in one message Jellyfin sent the link as a group member and decides what
        /// to tell the linked server.
        /// </summary>
        public PartyObservation Observe(object message, DateTime now)
        {
            var result = new PartyObservation();
            switch (message)
            {
                case SendCommand command:
                    ObserveCommand(command, now, result);
                    break;
                case GroupUpdate<PlayQueueUpdate> { Data: { } queue }:
                    ObserveQueue(queue, now, result);
                    break;
                case GroupUpdate<GroupStateUpdate> { Data: { } state }:
                    Local.SetState(state.State, now);
                    if (state.State == GroupStateType.Waiting)
                    {
                        result.AutoReady = true;
                    }

                    CheckLoaded(result);
                    break;
                case GroupUpdate<string> update when update.Type is GroupUpdateType.UserJoined or GroupUpdateType.UserLeft:
                    result.MembersChanged = true;
                    break;
                case GroupUpdate<string> update when update.Type is GroupUpdateType.GroupLeft or GroupUpdateType.NotInGroup or GroupUpdateType.GroupDoesNotExist:
                    result.Left = true;
                    break;
            }

            if (HoldReady)
            {
                result.AutoReady = false;
            }

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
                    if (Local.State is GroupStateType.Playing or GroupStateType.Waiting)
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

                    // A group still waiting for its own viewers resumes by itself when
                    // they're ready; forcing it would start them unloaded.
                    if (Local.State == GroupStateType.Paused)
                    {
                        actions.Add(new PartyAction(PartyActionKind.Unpause));
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
                case PartyEventKind.Snapshot:
                case PartyEventKind.Heartbeat:
                    PlanSnapshot(e, mappedQueue, now, actions);
                    break;
                case PartyEventKind.Ready when Role == PartyLinkRole.HostSide && HoldReady:
                    HoldReady = false;
                    actions.Add(ReadyAction());
                    break;
            }

            return actions;
        }

        /// <summary>The link's own Ready report for the current title and position.</summary>
        public PartyAction ReadyAction()
            => new(PartyActionKind.Ready, PositionTicks: Local.PositionTicks, PlaylistItemId: Local.PlayingPlaylistItemId);

        /// <summary>The local queue as this server's item ids, in order.</summary>
        public IReadOnlyList<Guid> LocalQueue() => Local.Playlist.Select(p => p.ItemId).ToList();

        private void PlanSnapshot(PartyEvent e, IReadOnlyList<Guid>? mappedQueue, DateTime now, List<PartyAction> actions)
        {
            var snapshot = e.Snapshot;
            if (snapshot == null)
            {
                return;
            }

            if (mappedQueue == null || mappedQueue.Count == 0)
            {
                // Nothing here to play (or an empty queue): nothing to follow.
                if (Role == PartyLinkRole.RemoteSide && !Initialized && e.Kind == PartyEventKind.Snapshot)
                {
                    Initialized = true;
                    AwaitingLoad = false;
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

            var first = Role == PartyLinkRole.RemoteSide && !Initialized;
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
                PlanCorrection(snapshot, now, actions);
                return;
            }

            if (first)
            {
                // Load paused; the host resumes everyone together once this server is ready.
                Initialized = true;
                AwaitingLoad = true;
                actions.Add(new PartyAction(PartyActionKind.Pause));
            }
        }

        private void PlanCorrection(PartySnapshot snapshot, DateTime now, List<PartyAction> actions)
        {
            if (!snapshot.Stable
                || AwaitingLoad
                || Local.State is GroupStateType.Waiting or GroupStateType.Idle
                || now - Local.StateSince < SettleTime)
            {
                return;
            }

            var playing = Local.State == GroupStateType.Playing;
            if (snapshot.IsPlaying != playing)
            {
                if (Math.Abs(Local.PositionAt(now) - snapshot.PositionTicks) > (snapshot.IsPlaying ? PlayingDrift : PausedDrift))
                {
                    actions.Add(new PartyAction(PartyActionKind.Seek, PositionTicks: snapshot.PositionTicks));
                }

                actions.Add(new PartyAction(snapshot.IsPlaying ? PartyActionKind.Unpause : PartyActionKind.Pause));
                return;
            }

            var drift = Math.Abs(Local.PositionAt(now) - snapshot.PositionTicks);
            if (drift > (playing ? PlayingDrift : PausedDrift))
            {
                actions.Add(new PartyAction(PartyActionKind.Seek, PositionTicks: snapshot.PositionTicks));
            }
        }

        private void ObserveCommand(SendCommand command, DateTime now, PartyObservation result)
        {
            var position = command.PositionTicks ?? 0;
            switch (command.Command)
            {
                case SendCommandType.Unpause:
                    Local.Play(position, command.When, now);
                    if (Synced.IsPlaying != true)
                    {
                        Synced.Unpause(position, now);
                        result.Forward.Add(new PartyEvent { Kind = PartyEventKind.Unpause, PositionTicks = position });
                    }

                    break;
                case SendCommandType.Pause:
                    Local.Pause(position, now);
                    if (Synced.IsPlaying != false || Math.Abs(Synced.PositionAt(now) - position) > PauseTolerance)
                    {
                        Synced.Pause(position, now);
                        result.Forward.Add(new PartyEvent { Kind = PartyEventKind.Pause, PositionTicks = position });
                    }

                    break;
                case SendCommandType.Seek:
                    Local.Seek(position, now);
                    result.AutoReady = true;
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

            CheckLoaded(result);
        }

        private void ObserveQueue(PlayQueueUpdate queue, DateTime now, PartyObservation result)
        {
            Local.Playlist = (queue.Playlist ?? Array.Empty<SyncPlayQueueItem>()).Select(i => (i.ItemId, i.PlaylistItemId)).ToList();
            Local.Index = queue.PlayingItemIndex;
            Local.Shuffle = queue.ShuffleMode;
            Local.Repeat = queue.RepeatMode;
            if (queue.IsPlaying)
            {
                Local.Play(queue.StartPositionTicks, now, now);
            }
            else
            {
                Local.Pause(queue.StartPositionTicks, now, keepState: true);
            }

            if (queue.Reason is PlayQueueUpdateReason.NewPlaylist or PlayQueueUpdateReason.SetCurrentItem
                or PlayQueueUpdateReason.NextItem or PlayQueueUpdateReason.PreviousItem)
            {
                // The group now waits for everyone to load the title, the link included.
                result.AutoReady = Local.State != GroupStateType.Idle || Local.Playlist.Count > 0;
            }

            var ids = LocalQueue();
            if (!Synced.SameQueue(ids, Local.Index))
            {
                Synced.SetQueue(ids, Local.Index);
                Synced.Stopped = false;
                result.SendQueue = true;
            }
        }

        private void CheckLoaded(PartyObservation result)
        {
            if (Role == PartyLinkRole.RemoteSide && AwaitingLoad && Local.State is GroupStateType.Paused or GroupStateType.Playing)
            {
                AwaitingLoad = false;
                result.ReportReady = true;
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

            if (state != GroupStateType.Playing)
            {
                PositionTicks = PositionAt(now);
                At = now;
                IsPlaying = false;
            }
        }

        /// <summary>Playback runs from <paramref name="position"/> starting at <paramref name="when"/>.</summary>
        public void Play(long position, DateTime when, DateTime now)
        {
            SetState(GroupStateType.Playing, now);
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

        /// <summary>Jumped to <paramref name="position"/>; the group now waits for everyone.</summary>
        public void Seek(long position, DateTime now)
        {
            SetState(GroupStateType.Waiting, now);
            PositionTicks = position;
            At = now;
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
