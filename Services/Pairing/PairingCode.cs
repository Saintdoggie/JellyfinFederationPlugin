using System;
using System.Collections.Generic;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.Federation.Services.Pairing
{
    /// <summary>Which of the two codes this is.</summary>
    public enum PairingCodeKind : byte
    {
        /// <summary>Made by the server that starts pairing.</summary>
        Offer = 1,

        /// <summary>Made by the friend in reply to an offer.</summary>
        Answer = 2
    }

    /// <summary>
    /// One pairing code (see docs/PAIRING.md). The tag is keyed with secrets that are in
    /// the codes themselves, so it catches typos and tampering, not who made the code:
    /// that comes from the admins swapping codes with each other.
    /// </summary>
    public sealed class PairingCode
    {
        /// <summary>Format version.</summary>
        public const byte Version = 1;

        /// <summary>How long a code is valid.</summary>
        public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

        /// <summary>Allowed difference between the two servers' clocks.</summary>
        public static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(10);

        /// <summary>Most endpoints a code carries.</summary>
        public const int MaxEndpoints = 3;

        /// <summary>Longest server name, in UTF-8 bytes.</summary>
        public const int MaxNameBytes = 40;

        internal const int FingerprintLength = 32;
        internal const int PublicKeyLength = 65;
        internal const int SecretLength = 32;
        internal const int OfferHashLength = 16;
        internal const int TagLength = 16;

        private const int MaxEncodedBytes = 4 + 8 + OfferHashLength + FingerprintLength + PublicKeyLength + SecretLength + 1 + (MaxEndpoints * 6) + 1 + MaxNameBytes + TagLength;

        /// <summary>Gets or sets which code this is.</summary>
        public PairingCodeKind Kind { get; set; }

        /// <summary>Gets or sets when the code was made.</summary>
        public DateTimeOffset Created { get; set; }

        /// <summary>Gets or sets when the code stops working.</summary>
        public DateTimeOffset Expires { get; set; }

        /// <summary>Gets or sets, on an answer, the first 16 bytes of SHA-256 of the offer's bytes.</summary>
        public byte[] OfferHash { get; set; } = Array.Empty<byte>();

        /// <summary>Gets or sets the SHA-256 of the sender's direct-link certificate (DER).</summary>
        public byte[] CertificateFingerprint { get; set; } = Array.Empty<byte>();

        /// <summary>Gets or sets the sender's pairing (ECDH P-256) public key, uncompressed.</summary>
        public byte[] PublicKey { get; set; } = Array.Empty<byte>();

        /// <summary>Gets or sets the sender's fresh random secret.</summary>
        public byte[] Secret { get; set; } = Array.Empty<byte>();

        /// <summary>Gets or sets the sender's pairing-port endpoints (public first).</summary>
        public List<IPEndPoint> Endpoints { get; set; } = new();

        /// <summary>Gets or sets the sender's server name (display only).</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Encodes the code. For an answer, <paramref name="offerSecret"/> is the
        /// offer's secret (the tag covers both).
        /// </summary>
        /// <returns>The raw bytes and the text the admin copies.</returns>
        public (byte[] Bytes, string Text) Encode(byte[]? offerSecret = null)
        {
            Validate(this);
            var body = new List<byte>(MaxEncodedBytes)
            {
                Version,
                (byte)Kind
            };
            AddUInt32(body, (uint)Created.ToUnixTimeSeconds());
            AddUInt32(body, (uint)Expires.ToUnixTimeSeconds());
            if (Kind == PairingCodeKind.Answer)
            {
                body.AddRange(OfferHash);
            }

            body.AddRange(CertificateFingerprint);
            body.AddRange(PublicKey);
            body.AddRange(Secret);
            body.Add((byte)Endpoints.Count);
            foreach (var endpoint in Endpoints)
            {
                body.AddRange(endpoint.Address.GetAddressBytes());
                body.Add((byte)(endpoint.Port >> 8));
                body.Add((byte)endpoint.Port);
            }

            var name = TrimName(Name);
            body.Add((byte)name.Length);
            body.AddRange(name);
            var tagKey = TagKey(Kind, Secret, offerSecret);
            body.AddRange(HMACSHA256.HashData(tagKey, body.ToArray()).AsSpan(0, TagLength).ToArray());
            var bytes = body.ToArray();
            return (bytes, Base32.Encode(bytes));
        }

        /// <summary>
        /// Reads a code the admin pasted. For an answer, pass the offer's secret
        /// (from the pending offer the answer's offer hash points to; see
        /// <see cref="PeekOfferHash"/>).
        /// </summary>
        /// <returns>The code, or null with the reason.</returns>
        public static (PairingCode? Code, byte[] Bytes, string? Error) TryDecode(string? text, PairingCodeKind expected, DateTimeOffset now, byte[]? offerSecret = null)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length > 2000)
            {
                return (null, Array.Empty<byte>(), "That isn't a pairing code.");
            }

            var bytes = Base32.Decode(text);
            if (bytes == null || bytes.Length < 2 || bytes.Length > MaxEncodedBytes)
            {
                return (null, Array.Empty<byte>(), "That isn't a pairing code. Check it was copied whole.");
            }

            if (bytes[0] != Version)
            {
                return (null, bytes, "This code is from a different Federation version. Both servers need the same version.");
            }

            if (bytes[1] != (byte)expected)
            {
                return (null, bytes, expected == PairingCodeKind.Offer
                    ? "This is an answer code. Enter it on the server that made the first code."
                    : "This is a first code, not an answer. Enter it under \"Enter a friend's code\".");
            }

            if (expected == PairingCodeKind.Answer && (offerSecret == null || offerSecret.Length != SecretLength))
            {
                return (null, bytes, "This answer isn't for a code this server made, or that code was used or expired.");
            }

            var reader = new Reader(bytes);
            try
            {
                var code = new PairingCode { Kind = expected };
                reader.Skip(2);
                code.Created = DateTimeOffset.FromUnixTimeSeconds(reader.UInt32());
                code.Expires = DateTimeOffset.FromUnixTimeSeconds(reader.UInt32());
                if (expected == PairingCodeKind.Answer)
                {
                    code.OfferHash = reader.Bytes(OfferHashLength);
                }

                code.CertificateFingerprint = reader.Bytes(FingerprintLength);
                code.PublicKey = reader.Bytes(PublicKeyLength);
                code.Secret = reader.Bytes(SecretLength);
                var count = reader.Byte();
                if (count == 0 || count > MaxEndpoints)
                {
                    return (null, bytes, "That code is damaged. Ask for a new one.");
                }

                for (var i = 0; i < count; i++)
                {
                    var address = new IPAddress(reader.Bytes(4));
                    var port = (reader.Byte() << 8) | reader.Byte();
                    code.Endpoints.Add(new IPEndPoint(address, port));
                }

                var nameLength = reader.Byte();
                if (nameLength > MaxNameBytes)
                {
                    return (null, bytes, "That code is damaged. Ask for a new one.");
                }

                code.Name = Encoding.UTF8.GetString(reader.Bytes(nameLength));
                var signed = reader.Position;
                var tag = reader.Bytes(TagLength);
                if (!reader.AtEnd)
                {
                    return (null, bytes, "That code is damaged. Check it was copied whole.");
                }

                var expectedTag = HMACSHA256.HashData(TagKey(expected, code.Secret, offerSecret), bytes.AsSpan(0, signed)).AsSpan(0, TagLength);
                if (!CryptographicOperations.FixedTimeEquals(tag, expectedTag))
                {
                    return (null, bytes, expected == PairingCodeKind.Answer
                        ? "This answer doesn't match the code this server made, or a character was mistyped."
                        : "A character in this code is wrong. Check it was copied whole.");
                }

                Validate(code);
                if (code.Created > now + ClockSkew || code.Expires - code.Created > Lifetime)
                {
                    return (null, bytes, "This code's time is off. Check both servers' clocks.");
                }

                if (now > code.Expires + ClockSkew)
                {
                    return (null, bytes, "This code has expired. Ask for a new one (codes last 15 minutes).");
                }

                return (code, bytes, null);
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException or CryptographicException or IndexOutOfRangeException)
            {
                return (null, bytes, "That code is damaged. Ask for a new one.");
            }
        }

        /// <summary>The offer hash an answer refers to, read before its tag can be checked.</summary>
        /// <returns>The hash, or null when the text isn't an answer code.</returns>
        public static byte[]? PeekOfferHash(string? text)
        {
            var bytes = string.IsNullOrWhiteSpace(text) || text.Length > 2000 ? null : Base32.Decode(text);
            if (bytes == null || bytes.Length < 10 + OfferHashLength || bytes[0] != Version || bytes[1] != (byte)PairingCodeKind.Answer)
            {
                return null;
            }

            return bytes.AsSpan(10, OfferHashLength).ToArray();
        }

        /// <summary>The value an answer carries to point at its offer.</summary>
        /// <returns>16 bytes.</returns>
        public static byte[] HashOffer(byte[] offerBytes) => SHA256.HashData(offerBytes).AsSpan(0, OfferHashLength).ToArray();

        private static byte[] TagKey(PairingCodeKind kind, byte[] ownSecret, byte[]? offerSecret)
        {
            if (kind == PairingCodeKind.Offer)
            {
                return ownSecret;
            }

            var key = new byte[SecretLength * 2];
            offerSecret!.CopyTo(key, 0);
            ownSecret.CopyTo(key, SecretLength);
            return key;
        }

        private static void Validate(PairingCode code)
        {
            if (code.Kind is not (PairingCodeKind.Offer or PairingCodeKind.Answer)
                || code.CertificateFingerprint.Length != FingerprintLength
                || code.PublicKey.Length != PublicKeyLength
                || code.PublicKey[0] != 0x04
                || code.Secret.Length != SecretLength
                || (code.Kind == PairingCodeKind.Answer && code.OfferHash.Length != OfferHashLength)
                || code.Endpoints.Count is 0 or > MaxEndpoints)
            {
                throw new ArgumentException("Incomplete pairing code.");
            }

            foreach (var endpoint in code.Endpoints)
            {
                if (endpoint.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || endpoint.Port is <= 0 or > 65535)
                {
                    throw new ArgumentException("Pairing codes carry IPv4 endpoints only.");
                }
            }

            // Points off the curve are refused here, so a code can't smuggle a key
            // that weakens the key agreement.
            using var key = ECDiffieHellman.Create();
            key.ImportParameters(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = code.PublicKey.AsSpan(1, 32).ToArray(), Y = code.PublicKey.AsSpan(33, 32).ToArray() }
            });
        }

        private static byte[] TrimName(string name)
        {
            var bytes = Encoding.UTF8.GetBytes(name ?? string.Empty);
            if (bytes.Length <= MaxNameBytes)
            {
                return bytes;
            }

            // Cut on a character boundary.
            var text = name!;
            while (Encoding.UTF8.GetByteCount(text) > MaxNameBytes)
            {
                text = text.Substring(0, text.Length - 1);
            }

            return Encoding.UTF8.GetBytes(text);
        }

        private static void AddUInt32(List<byte> list, uint value)
        {
            list.Add((byte)(value >> 24));
            list.Add((byte)(value >> 16));
            list.Add((byte)(value >> 8));
            list.Add((byte)value);
        }

        private sealed class Reader
        {
            private readonly byte[] _bytes;

            public Reader(byte[] bytes) => _bytes = bytes;

            public int Position { get; private set; }

            public bool AtEnd => Position == _bytes.Length;

            public void Skip(int count) => Take(count);

            public byte Byte() => _bytes[Take(1)];

            public uint UInt32()
            {
                var at = Take(4);
                return ((uint)_bytes[at] << 24) | ((uint)_bytes[at + 1] << 16) | ((uint)_bytes[at + 2] << 8) | _bytes[at + 3];
            }

            public byte[] Bytes(int count) => _bytes.AsSpan(Take(count), count).ToArray();

            private int Take(int count)
            {
                if (count < 0 || Position + count > _bytes.Length)
                {
                    throw new FormatException("Truncated.");
                }

                var at = Position;
                Position += count;
                return at;
            }
        }
    }

    /// <summary>Crockford base32, grouped in fives with dashes; reading forgives case, spaces and look-alike letters.</summary>
    public static class Base32
    {
        private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

        /// <summary>Encodes bytes.</summary>
        /// <returns>Text like <c>ABCDE-FGHJK-...</c>.</returns>
        public static string Encode(byte[] bytes)
        {
            var text = new StringBuilder((bytes.Length * 8 / 5) + 2);
            int buffer = 0, bits = 0, written = 0;
            foreach (var b in bytes)
            {
                buffer = (buffer << 8) | b;
                bits += 8;
                while (bits >= 5)
                {
                    Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                    bits -= 5;
                }
            }

            if (bits > 0)
            {
                Append(Alphabet[(buffer << (5 - bits)) & 31]);
            }

            return text.ToString();

            void Append(char c)
            {
                if (written > 0 && written % 5 == 0)
                {
                    text.Append('-');
                }

                text.Append(c);
                written++;
            }
        }

        /// <summary>Decodes text from <see cref="Encode"/>.</summary>
        /// <returns>The bytes, or null when a character isn't base32 or the padding bits aren't zero.</returns>
        public static byte[]? Decode(string text)
        {
            var output = new List<byte>(text.Length * 5 / 8);
            int buffer = 0, bits = 0;
            foreach (var raw in text)
            {
                if (raw is '-' or ' ' or '\t' or '\r' or '\n')
                {
                    continue;
                }

                var c = char.ToUpperInvariant(raw) switch
                {
                    'O' => '0',
                    'I' or 'L' => '1',
                    var other => other
                };
                var value = Alphabet.IndexOf(c, StringComparison.Ordinal);
                if (value < 0)
                {
                    return null;
                }

                buffer = ((buffer << 5) | value) & 0xFFFF;
                bits += 5;
                if (bits >= 8)
                {
                    output.Add((byte)(buffer >> (bits - 8)));
                    bits -= 8;
                }
            }

            // Leftover bits must be padding (zero) and fewer than a byte's worth.
            if (bits >= 5 || (buffer & ((1 << bits) - 1)) != 0)
            {
                return null;
            }

            return output.ToArray();
        }
    }
}
