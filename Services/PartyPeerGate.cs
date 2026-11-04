using System;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>What a versioned party link did with an incoming event.</summary>
    public enum PartyEventAcceptance
    {
        /// <summary>Apply the event.</summary>
        Accept,

        /// <summary>A duplicate or older event; acknowledge it without applying it again.</summary>
        Stale,

        /// <summary>The event belongs to another connection or speaks an incompatible version.</summary>
        WrongLink
    }

    /// <summary>
    /// Per-link wire state. Version 2 rejects messages from replaced links and
    /// applies each sequence at most once; version 1 retains the original behavior
    /// so mixed-version friends can keep using ordinary watch parties.
    /// </summary>
    public sealed class PartyPeerGate
    {
        private long _nextSequence;
        private long _lastReceivedSequence;

        /// <summary>Initializes a new instance of the <see cref="PartyPeerGate"/> class.</summary>
        public PartyPeerGate(int protocolVersion, Guid connectionId)
        {
            if (protocolVersion >= PartyProtocol.CurrentVersion && connectionId == Guid.Empty)
            {
                throw new ArgumentException("A versioned party link needs a connection id.", nameof(connectionId));
            }

            ProtocolVersion = protocolVersion >= PartyProtocol.CurrentVersion ? PartyProtocol.CurrentVersion : 1;
            ConnectionId = ProtocolVersion >= PartyProtocol.CurrentVersion ? connectionId : Guid.Empty;
        }

        /// <summary>Gets the negotiated wire version.</summary>
        public int ProtocolVersion { get; }

        /// <summary>Gets the connection shared by both ends, or empty for a legacy link.</summary>
        public Guid ConnectionId { get; }

        /// <summary>Marks an outgoing event immediately before it is sent.</summary>
        public void Prepare(PartyEvent e, DateTime sentAtUtc)
        {
            if (ProtocolVersion < PartyProtocol.CurrentVersion)
            {
                return;
            }

            e.ProtocolVersion = ProtocolVersion;
            e.ConnectionId = ConnectionId;
            e.Sequence = ++_nextSequence;
            e.SentAtUtc ??= sentAtUtc;
        }

        /// <summary>Checks an incoming event and remembers its sequence when accepted.</summary>
        public PartyEventAcceptance Accept(PartyEvent e)
        {
            if (ProtocolVersion < PartyProtocol.CurrentVersion)
            {
                return PartyEventAcceptance.Accept;
            }

            if (e.ProtocolVersion < PartyProtocol.CurrentVersion || e.ConnectionId != ConnectionId)
            {
                return PartyEventAcceptance.WrongLink;
            }

            if (e.Sequence <= _lastReceivedSequence)
            {
                return PartyEventAcceptance.Stale;
            }

            _lastReceivedSequence = e.Sequence;
            return PartyEventAcceptance.Accept;
        }
    }
}
