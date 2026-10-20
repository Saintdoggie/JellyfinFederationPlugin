using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// A title in a watch party, described so that each server can find its own copy:
    /// the ids it has on the servers that own the file, plus provider ids for servers
    /// that have their own copy. Never carries a path, a stream URL or a credential.
    /// </summary>
    public sealed class PartyItemRef
    {
        /// <summary>Gets or sets the item's ids on the servers that own the file.</summary>
        public List<PartyOwnerRef> Owners { get; set; } = new();

        /// <summary>Gets or sets the item kind ("Movie", "Episode", ...).</summary>
        public string? Type { get; set; }

        /// <summary>Gets or sets the item's provider ids (imdb, tmdb, tvdb).</summary>
        public Dictionary<string, string>? ProviderIds { get; set; }

        /// <summary>Gets or sets the show's provider ids, for an episode.</summary>
        public Dictionary<string, string>? SeriesProviderIds { get; set; }

        /// <summary>Gets or sets the season number, for an episode.</summary>
        public int? Season { get; set; }

        /// <summary>Gets or sets the episode number, for an episode.</summary>
        public int? Episode { get; set; }

        /// <summary>Gets or sets the title, for messages only.</summary>
        public string? Name { get; set; }
    }

    /// <summary>One server that owns a party title, and its id there.</summary>
    public sealed class PartyOwnerRef
    {
        /// <summary>Gets or sets the owning server's federation id.</summary>
        public string Fid { get; set; } = string.Empty;

        /// <summary>Gets or sets the item id on that server ("N" form).</summary>
        public string Id { get; set; } = string.Empty;
    }

    /// <summary>
    /// What a party is doing: its queue, the playing title, the position and whether
    /// it's playing. Sent when the queue changes, when a server joins, and as the
    /// host's heartbeat.
    /// </summary>
    public sealed class PartySnapshot
    {
        /// <summary>Gets or sets the queue.</summary>
        public List<PartyItemRef> Items { get; set; } = new();

        /// <summary>Gets or sets the index of the playing title in <see cref="Items"/>.</summary>
        public int Index { get; set; }

        /// <summary>Gets or sets the position, as of when the snapshot was sent.</summary>
        public long PositionTicks { get; set; }

        /// <summary>Gets or sets a value indicating whether the party is playing.</summary>
        public bool IsPlaying { get; set; }

        /// <summary>Gets or sets a value indicating whether the sender's group is settled (not waiting for someone to load).</summary>
        public bool Stable { get; set; }

        /// <summary>Gets or sets the shuffle mode ("Sorted"/"Shuffle").</summary>
        public string? Shuffle { get; set; }

        /// <summary>Gets or sets the repeat mode.</summary>
        public string? Repeat { get; set; }
    }

    /// <summary>Kinds of <see cref="PartyEvent"/>.</summary>
    public static class PartyEventKind
    {
        /// <summary>The queue or playing title changed (or a full resync).</summary>
        public const string Snapshot = "Snapshot";

        /// <summary>Periodic snapshot from the host, to correct drift and missed messages.</summary>
        public const string Heartbeat = "Heartbeat";

        /// <summary>Playback paused.</summary>
        public const string Pause = "Pause";

        /// <summary>Playback resumed.</summary>
        public const string Unpause = "Unpause";

        /// <summary>Someone jumped to a position.</summary>
        public const string Seek = "Seek";

        /// <summary>Playback stopped.</summary>
        public const string Stop = "Stop";

        /// <summary>A joining server finished loading (remote to host).</summary>
        public const string Ready = "Ready";

        /// <summary>Gets every kind.</summary>
        public static readonly IReadOnlyCollection<string> All = new[] { Snapshot, Heartbeat, Pause, Unpause, Seek, Stop, Ready };
    }

    /// <summary>One message between two linked groups of a party.</summary>
    public sealed class PartyEvent
    {
        /// <summary>Gets or sets the kind (see <see cref="PartyEventKind"/>).</summary>
        public string Kind { get; set; } = string.Empty;

        /// <summary>Gets or sets the position for Pause/Unpause/Seek.</summary>
        public long PositionTicks { get; set; }

        /// <summary>Gets or sets the snapshot for Snapshot/Heartbeat.</summary>
        public PartySnapshot? Snapshot { get; set; }

        /// <summary>Gets or sets who is in the sender's group, for the other side's labels.</summary>
        public List<string>? Participants { get; set; }
    }

    /// <summary>A host telling a friend about a party its viewers may join.</summary>
    public sealed class PartyAnnouncement
    {
        /// <summary>Gets or sets the party id.</summary>
        public Guid PartyId { get; set; }

        /// <summary>Gets or sets the party's name.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Gets or sets who is watching on the host's side.</summary>
        public List<string> Participants { get; set; } = new();

        /// <summary>Gets or sets what the party is doing, with titles described for this friend.</summary>
        public PartySnapshot? Snapshot { get; set; }

        /// <summary>Gets or sets a value indicating whether the party ended.</summary>
        public bool Ended { get; set; }
    }

    /// <summary>A friend's server joining or leaving a party (remote to host).</summary>
    public sealed class PartyMembership
    {
        /// <summary>Gets or sets who is watching on the friend's side.</summary>
        public List<string> Participants { get; set; } = new();
    }
}
