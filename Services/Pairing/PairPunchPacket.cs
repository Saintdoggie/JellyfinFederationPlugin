using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace Jellyfin.Plugin.Federation.Services.Pairing
{
    /// <summary>
    /// Punch packets on the pairing port:
    /// <c>"JFPR" | type | senderRole | pairTag(16) | nonce(16) | unixSeconds(4) | tag(32)</c>,
    /// with <c>tag = HMAC-SHA256(key, everything before)</c>. The pair tag says which key to
    /// check (an offer hash before pairing, then a tag derived from the pair key). An ack
    /// is the same size as the probe it answers, so the port can't amplify anything, and
    /// nothing without a valid tag is ever answered.
    /// </summary>
    public static class PairPunchPacket
    {
        /// <summary>Probe.</summary>
        public const byte Probe = 1;

        /// <summary>Answer to a probe.</summary>
        public const byte Ack = 2;

        /// <summary>Exact packet length.</summary>
        public const int Length = 4 + 1 + 1 + 16 + 16 + 4 + 32;

        /// <summary>How far a packet's time may be from ours.</summary>
        public static readonly TimeSpan MaxClockDifference = TimeSpan.FromSeconds(120);

        private const int TagOffset = Length - 32;
        private static readonly byte[] Magic = "JFPR"u8.ToArray();

        /// <summary>The pair tag for a pair key: lets the port pick the key without trying all of them.</summary>
        /// <returns>16 bytes.</returns>
        public static byte[] PairTag(byte[] pairKey) => PairingCrypto.SubKey(pairKey, "pair-tag").AsSpan(0, 16).ToArray();

        /// <summary>Builds a packet.</summary>
        /// <returns><see cref="Length"/> bytes.</returns>
        public static byte[] Build(byte type, byte senderRole, byte[] pairTag, byte[] key, DateTimeOffset now, byte[]? nonce = null)
        {
            if (pairTag.Length != 16 || type is not (Probe or Ack) || senderRole > 1)
            {
                throw new ArgumentException("Bad punch packet fields.");
            }

            var packet = new byte[Length];
            Magic.CopyTo(packet, 0);
            packet[4] = type;
            packet[5] = senderRole;
            pairTag.CopyTo(packet, 6);
            (nonce ?? RandomNumberGenerator.GetBytes(16)).AsSpan(0, 16).CopyTo(packet.AsSpan(22));
            BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(38), (uint)now.ToUnixTimeSeconds());
            HMACSHA256.HashData(key, packet.AsSpan(0, TagOffset)).CopyTo(packet.AsSpan(TagOffset));
            return packet;
        }

        /// <summary>Reads the pair tag of something that looks like a punch packet, before its key is known.</summary>
        /// <returns>The pair tag, or null.</returns>
        public static byte[]? PeekPairTag(ReadOnlySpan<byte> packet)
            => packet.Length == Length && packet.StartsWith(Magic) ? packet.Slice(6, 16).ToArray() : null;

        /// <summary>
        /// Checks a packet: exact length, magic, tag, a sender role that isn't ours (so our
        /// own packets reflected back fail), a time within two minutes and a nonce not seen
        /// before.
        /// </summary>
        /// <returns>The packet type, or 0 when it must be dropped.</returns>
        public static byte Verify(ReadOnlySpan<byte> packet, byte ownRole, byte[] key, DateTimeOffset now, ReplayWindow replay)
        {
            if (packet.Length != Length || !packet.StartsWith(Magic) || packet[4] is not (Probe or Ack) || packet[5] > 1 || packet[5] == ownRole)
            {
                return 0;
            }

            Span<byte> expected = stackalloc byte[32];
            HMACSHA256.HashData(key, packet.Slice(0, TagOffset), expected);
            if (!CryptographicOperations.FixedTimeEquals(expected, packet.Slice(TagOffset)))
            {
                return 0;
            }

            var sent = DateTimeOffset.FromUnixTimeSeconds(BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(38)));
            if ((sent - now).Duration() > MaxClockDifference)
            {
                return 0;
            }

            return replay.TryAdd(packet.Slice(22, 16), now) ? packet[4] : (byte)0;
        }

        /// <summary>The nonce of a packet (an ack carries the probe's nonce back).</summary>
        /// <returns>16 bytes.</returns>
        public static byte[] Nonce(ReadOnlySpan<byte> packet) => packet.Slice(22, 16).ToArray();
    }

    /// <summary>
    /// Nonces seen in the last few minutes for one key. Bounded: when full, the oldest
    /// are forgotten, and those are older than the clock window anyway at any sane rate.
    /// </summary>
    public sealed class ReplayWindow
    {
        private readonly TimeSpan _keep;
        private readonly int _capacity;
        private readonly Dictionary<UInt128, DateTimeOffset> _seen = new();
        private readonly Queue<(UInt128 Nonce, DateTimeOffset At)> _order = new();
        private readonly object _gate = new();

        /// <summary>Initializes a new instance of the <see cref="ReplayWindow"/> class.</summary>
        public ReplayWindow(TimeSpan? keep = null, int capacity = 8192)
        {
            // Longer than the clock window on both sides, so a replay can't outlive it.
            _keep = keep ?? TimeSpan.FromMinutes(5);
            _capacity = capacity;
        }

        /// <summary>Records a nonce.</summary>
        /// <returns>False when it was seen before.</returns>
        public bool TryAdd(ReadOnlySpan<byte> nonce, DateTimeOffset now)
        {
            var key = BinaryPrimitives.ReadUInt128BigEndian(nonce);
            lock (_gate)
            {
                while (_order.Count > 0 && (now - _order.Peek().At > _keep || _order.Count >= _capacity))
                {
                    var (old, _) = _order.Dequeue();
                    _seen.Remove(old);
                }

                if (!_seen.TryAdd(key, now))
                {
                    return false;
                }

                _order.Enqueue((key, now));
                return true;
            }
        }
    }
}
