using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Federation.Configuration;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// The one place that answers "does this server show a linked server's
    /// titles?" (per-link catalogue direction, 0.0.222). A server that does not
    /// receive from a linked server must not list, sync, dedup against or offer
    /// that server's titles or copies anywhere: library sync and pruning, the
    /// copy choice, standby, owned-title friend copies, friends' favourites and
    /// Popular rows, and the support report. Everything that picks a friend's
    /// copy calls <see cref="IsHiddenLinkedSource(RemoteServer?)"/>.
    /// </summary>
    /// <remarks>
    /// Each side stores the direction from its own side
    /// (<see cref="RemoteServer.LinkCatalog"/>) and what the other side last
    /// said (<see cref="RemoteServer.LinkCatalogPeer"/>, already mirrored).
    /// When the two disagree (one side was offline when it changed) the
    /// narrower one applies: titles flow only where both sides agree they do.
    /// The direction never limits conversion jobs, sound help, management, or
    /// the friends a linked server lends (<c>Peer/Linked/Friends</c>): those are
    /// other servers' titles, not the linked server's own.
    /// </remarks>
    internal static class LinkCatalog
    {
        /// <summary>The same direction seen from the other server's side.</summary>
        internal static LinkCatalogDirection Mirror(LinkCatalogDirection direction) => direction switch
        {
            LinkCatalogDirection.OnlyMineGoes => LinkCatalogDirection.OnlyTheirsComes,
            LinkCatalogDirection.OnlyTheirsComes => LinkCatalogDirection.OnlyMineGoes,
            _ => LinkCatalogDirection.BothWays
        };

        /// <summary>Whether <paramref name="direction"/> brings the other server's titles here.</summary>
        internal static bool Receives(LinkCatalogDirection direction) => direction != LinkCatalogDirection.OnlyMineGoes;

        /// <summary>Whether <paramref name="direction"/> sends this server's titles to the other.</summary>
        internal static bool Sends(LinkCatalogDirection direction) => direction != LinkCatalogDirection.OnlyTheirsComes;

        /// <summary>True for a defined value (inputs from the wire or the page).</summary>
        internal static bool IsValid(int value) => Enum.IsDefined(typeof(LinkCatalogDirection), value);

        /// <summary>
        /// Whether this server shows <paramref name="server"/>'s titles. Always
        /// true for anything that is not a linked server. For a linked server:
        /// only when both this side's value and the other side's last known
        /// value let titles come here (narrower wins).
        /// </summary>
        internal static bool ReceivesHere(RemoteServer server)
        {
            if (server == null || !server.Linked)
            {
                return true;
            }

            return Receives(server.LinkCatalog)
                && (server.LinkCatalogPeer is not { } peer || Receives(peer));
        }

        /// <summary>Whether this server's titles go to <paramref name="server"/> (narrower wins, as above).</summary>
        internal static bool SendsThere(RemoteServer server)
        {
            if (server == null || !server.Linked)
            {
                return true;
            }

            return Sends(server.LinkCatalog)
                && (server.LinkCatalogPeer is not { } peer || Sends(peer));
        }

        /// <summary>
        /// True when <paramref name="server"/> is a linked server whose titles are
        /// hidden here. The helper the copy choice, sync and every friend list use.
        /// </summary>
        internal static bool IsHiddenLinkedSource(RemoteServer? server)
            => server != null && server.Linked && !ReceivesHere(server);

        /// <summary>Same, by the local id of the server a copy came from.</summary>
        internal static bool IsHiddenLinkedSource(string? serverId)
        {
            if (string.IsNullOrEmpty(serverId))
            {
                return false;
            }

            var server = Plugin.Instance?.Configuration?.RemoteServers?.FirstOrDefault(s => string.Equals(s.Id, serverId, StringComparison.OrdinalIgnoreCase));
            return IsHiddenLinkedSource(server);
        }

        /// <summary>Ids of every linked server whose titles are hidden here.</summary>
        internal static HashSet<string> HiddenServerIds(PluginConfiguration? config)
            => new(
                (config?.RemoteServers ?? new List<RemoteServer>()).Where(IsHiddenLinkedSource).Select(s => s.Id),
                StringComparer.OrdinalIgnoreCase);

        /// <summary>What the admin page says the direction is, from this side.</summary>
        internal static string Describe(RemoteServer server)
        {
            var receive = ReceivesHere(server);
            var send = SendsThere(server);
            return (receive, send) switch
            {
                (true, true) => "Both ways",
                (false, true) => "Only this server's titles go to " + server.Name,
                (true, false) => "Only " + server.Name + "'s titles come here",
                _ => "No titles either way (the two servers disagree; change it again to agree)"
            };
        }
    }
}
