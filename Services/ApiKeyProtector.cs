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
    /// folder). These keys are never real Jellyfin API keys used anywhere except
    /// <see cref="Configuration.PluginConfiguration.InternalRelayApiKey"/> - the
    /// rest are the scoped federation tokens minted by
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

        /// <summary>
        /// Encrypts <paramref name="plaintext"/> for storage. Returns it unchanged
        /// if null/empty (nothing to protect) or if <see cref="Initialize"/> was
        /// never called (fails safe to "don't crash a save" rather than silently
        /// losing the key - callers only reach this after the plugin's own
        /// constructor has already called <see cref="Initialize"/>, so this is
        /// purely a defensive fallback).
        /// </summary>
        public static string Protect(string? plaintext)
        {
            if (string.IsNullOrEmpty(plaintext) || _protector == null)
            {
                return plaintext ?? string.Empty;
            }

            return _protector.Protect(plaintext);
        }

        /// <summary>
        /// Decrypts a value produced by <see cref="Protect"/>. A value that fails
        /// to decrypt is treated as already-plaintext rather than an error - the
        /// only way that happens is a key saved before this feature existed (or,
        /// after a config restore, a key ring that no longer matches), and in
        /// both cases the right behavior is to keep using the value as-is; the
        /// next save re-encrypts it for real. This is what makes upgrading an
        /// existing install transparent, with no explicit migration step.
        /// </summary>
        public static string Unprotect(string? value)
        {
            if (string.IsNullOrEmpty(value) || _protector == null)
            {
                return value ?? string.Empty;
            }

            try
            {
                return _protector.Unprotect(value);
            }
            catch (Exception)
            {
                return value;
            }
        }
    }
}
