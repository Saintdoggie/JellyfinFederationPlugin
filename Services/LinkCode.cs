using System;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// The rotating six-digit code shown while two servers are being linked:
    /// RFC 6238 (TOTP, HMAC-SHA1, 30-second steps), the scheme authenticator apps
    /// use. The secret never leaves the server that shows the code; the other
    /// server only ever sends back the digits an admin typed.
    /// </summary>
    internal static class LinkCode
    {
        /// <summary>Seconds each code is shown for.</summary>
        internal const int StepSeconds = 30;

        /// <summary>Digits in a code.</summary>
        internal const int Digits = 6;

        /// <summary>A fresh 160-bit secret.</summary>
        internal static byte[] NewSecret() => RandomNumberGenerator.GetBytes(20);

        /// <summary>The time step <paramref name="utc"/> falls in.</summary>
        internal static long Step(DateTime utc) => new DateTimeOffset(utc, TimeSpan.Zero).ToUnixTimeSeconds() / StepSeconds;

        /// <summary>Seconds until the code shown at <paramref name="utc"/> changes.</summary>
        internal static int SecondsLeft(DateTime utc) => StepSeconds - (int)(new DateTimeOffset(utc, TimeSpan.Zero).ToUnixTimeSeconds() % StepSeconds);

        /// <summary>The code for <paramref name="step"/>, zero-padded.</summary>
        internal static string At(byte[] secret, long step)
        {
            Span<byte> counter = stackalloc byte[8];
            BinaryPrimitives.WriteInt64BigEndian(counter, step);
            Span<byte> hash = stackalloc byte[20];
            HMACSHA1.HashData(secret, counter, hash);
            var offset = hash[19] & 0x0f;
            var value = ((hash[offset] & 0x7f) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
            return (value % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>The code to show at <paramref name="utc"/>.</summary>
        internal static string Current(byte[] secret, DateTime utc) => At(secret, Step(utc));

        /// <summary>
        /// True when <paramref name="typed"/> is the code of the current step or
        /// the one before (an admin reading it out as it changes). Spaces and
        /// dashes are ignored; anything else that is not six digits never matches.
        /// </summary>
        internal static bool Matches(byte[] secret, string? typed, DateTime utc)
        {
            var digits = Normalize(typed);
            if (digits == null)
            {
                return false;
            }

            var step = Step(utc);
            var ok = false;
            foreach (var candidate in new[] { At(secret, step), At(secret, step - 1) })
            {
                ok |= CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.ASCII.GetBytes(candidate),
                    System.Text.Encoding.ASCII.GetBytes(digits));
            }

            return ok;
        }

        /// <summary>The six digits in <paramref name="typed"/>, or null when it is not a code.</summary>
        internal static string? Normalize(string? typed)
        {
            if (string.IsNullOrWhiteSpace(typed) || typed.Length > 20)
            {
                return null;
            }

            var digits = typed.Replace(" ", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal);
            if (digits.Length != Digits)
            {
                return null;
            }

            foreach (var c in digits)
            {
                if (c < '0' || c > '9')
                {
                    return null;
                }
            }

            return digits;
        }
    }
}
