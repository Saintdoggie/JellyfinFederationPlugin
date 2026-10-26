using System;
using System.IO;
using Microsoft.AspNetCore.DataProtection;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Encrypts the federation API keys/tokens this plugin stores at rest, using
    /// ASP.NET Core's DataProtection API with a key ring kept alongside this
    /// plugin's own config file (see <see cref="Plugin.GetDefaultCachePath"/> for
    /// the sibling convention of scoping plugin-owned files under its data
    /// folder). Native Jellyfin relay keys are process-memory only and never use
    /// this protector. These are scoped federation tokens minted by
    /// <see cref="FederationFriendService"/>'s handshake, which already carry no
    /// access beyond the <c>Peer/*</c> endpoints - but a friend's server address
    /// plus a valid token for it is still a real credential worth not leaving in
    /// plaintext on disk.
    /// </summary>
    internal static class ApiKeyProtector
    {
        private const string Purpose = "Jellyfin.Plugin.Federation.ApiKeys.v1";

        private static readonly object InitLock = new object();
        private static IDataProtector? _protector;

        /// <summary>
        /// Sets up the key ring, creating it on first run. Idempotent and safe to
        /// call more than once - only the first call does anything.
        /// </summary>
        public static void Initialize(string keyRingDirectory)
        {
            if (_protector != null)
            {
                return;
            }

            lock (InitLock)
            {
                if (_protector != null)
                {
                    return;
                }

                Directory.CreateDirectory(keyRingDirectory);
                var provider = DataProtectionProvider.Create(new DirectoryInfo(keyRingDirectory));
                _protector = provider.CreateProtector(Purpose);
            }
        }

        private const string ProtectedPrefix = "fedenc:v1:";
        private const string LegacyPayloadBase64Marker = "CfDJ8";
        private static readonly byte[] LegacyPayloadMarker = { 0x09, 0xf0, 0xc9 };

        /// <summary>
        /// Encrypts <paramref name="plaintext"/> for storage in a versioned envelope.
        /// Null and empty values need no protection. Callers initialize the key ring
        /// before configuration is loaded or saved.
        /// </summary>
        public static string Protect(string? plaintext)
        {
            if (string.IsNullOrEmpty(plaintext))
            {
                return string.Empty;
            }

            if (_protector == null)
            {
                throw new InvalidOperationException("The federation credential protector is not initialized.");
            }

            return ProtectedPrefix + _protector.Protect(plaintext);
        }

        /// <summary>
        /// Decrypts current and pre-envelope protected values. Damaged protected
        /// data returns an empty credential so ciphertext can never authenticate or
        /// be saved again as plaintext. Unprotected legacy credentials remain
        /// readable and are migrated to the envelope on the next configuration save.
        /// </summary>
        public static string Unprotect(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            if (_protector == null)
            {
                throw new InvalidOperationException("The federation credential protector is not initialized.");
            }

            if (value.StartsWith(ProtectedPrefix, StringComparison.Ordinal))
            {
                try
                {
                    return _protector.Unprotect(value[ProtectedPrefix.Length..]);
                }
                catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException)
                {
                    return string.Empty;
                }
            }

            try
            {
                return _protector.Unprotect(value);
            }
            catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException && LooksLikeLegacyProtectedValue(value))
            {
                return string.Empty;
            }
            catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException)
            {
                return value;
            }
        }

        private static bool LooksLikeLegacyProtectedValue(string value)
        {
            if (value.StartsWith(LegacyPayloadBase64Marker, StringComparison.Ordinal))
            {
                return true;
            }

            try
            {
                var bytes = Convert.FromBase64String(value);
                return bytes.Length >= LegacyPayloadMarker.Length
                    && bytes.AsSpan(0, LegacyPayloadMarker.Length).SequenceEqual(LegacyPayloadMarker);
            }
            catch (FormatException)
            {
                return false;
            }
        }
    }
}
