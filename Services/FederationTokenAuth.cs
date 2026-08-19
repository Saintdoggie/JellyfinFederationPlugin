using System;
using System.Linq;
using System.Security.Cryptography;
using Jellyfin.Plugin.Federation.Configuration;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Authenticates server-to-server Federation calls using a scoped federation
    /// token (<see cref="RemoteServer.ApiKey"/>/<see cref="RemoteServer.IssuedApiKey"/>),
    /// carried in the <see cref="Header"/> header - not Jellyfin's own
    /// <c>X-Emby-Token</c>/<c>[Authorize]</c> pipeline. Every genuinely
    /// peer-to-peer endpoint in <c>FederationController</c> (Friends/List,
    /// Friends/RemoteUserRules, Friends/SharedUserUpdate, Friends/Unfriend,
    /// PlaybackToken, and the Peer/* data endpoints) is <c>[AllowAnonymous]</c>
    /// and calls <see cref="ResolveCaller"/> itself, returning 401 on a null
    /// result - the same manual-check pattern <c>FederationController.DirectStream</c>
    /// already used for its own item-scoped token.
    /// <para>
    /// This exists specifically so a leaked federation token is not, unlike the
    /// real Jellyfin API key it replaces, a full-admin-equivalent credential: it
    /// only ever satisfies this check, never Jellyfin's own
    /// <c>[Authorize(Policy = "RequiresElevation")]</c> or any native REST
    /// endpoint, because it is never registered with Jellyfin's own
    /// <c>IAuthenticationManager</c> at all - it is purely a string this plugin
    /// generates, stores in <see cref="PluginConfiguration"/>, and compares.
    /// </para>
    /// </summary>
    public static class FederationTokenAuth
    {
        /// <summary>
        /// The header a federation token is sent in. Deliberately not
        /// <c>X-Emby-Token</c> - reusing that name would risk this token being
        /// forwarded somewhere Jellyfin's own auth middleware reads it, and would
        /// blur the "this is not a Jellyfin credential" distinction that is the
        /// entire point of this mechanism.
        /// </summary>
        public const string Header = "X-Federation-Token";

        /// <summary>
        /// Generates a fresh, opaque, non-guessable federation token. Pure - no
        /// state is recorded here; the caller is responsible for storing the
        /// result on the relevant <see cref="RemoteServer.IssuedApiKey"/> (or
        /// sending it as <see cref="RemoteServer.ApiKey"/> to whichever side is
        /// meant to hold it). There is deliberately no separate issued-token
        /// store to keep in sync - <see cref="ResolveCaller"/> simply looks the
        /// token up directly against <see cref="PluginConfiguration.RemoteServers"/>.
        /// </summary>
        public static string GenerateToken()
        {
            return Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        }

        /// <summary>
        /// Resolves which friend a request's <see cref="Header"/> token belongs
        /// to, or null if it is missing, malformed, or does not match any
        /// enabled friend's <see cref="RemoteServer.IssuedApiKey"/>. Callers must
        /// treat null as "reject this request" (401/403) - this deliberately
        /// never falls back to "allow" the way <see cref="RemoteAccessControlService.IsAllowed"/>'s
        /// null-context case does, since here a missing/invalid token means the
        /// caller was never authenticated at all, not merely "nothing to
        /// evaluate a rule against".
        /// </summary>
        public static RemoteServer? ResolveCaller(HttpRequest request)
        {
            if (!request.Headers.TryGetValue(Header, out var values))
            {
                return null;
            }

            var token = values.ToString();
            if (string.IsNullOrEmpty(token))
            {
                return null;
            }

            var servers = Plugin.Instance?.Configuration?.RemoteServers;
            if (servers == null)
            {
                return null;
            }

            foreach (var server in servers)
            {
                if (!server.Enabled || string.IsNullOrEmpty(server.IssuedApiKey))
                {
                    continue;
                }

                if (FixedTimeEquals(server.IssuedApiKey, token))
                {
                    return server;
                }
            }

            return null;
        }

        /// <summary>
        /// Constant-time string comparison so token validation does not leak
        /// timing information about how many leading characters of a guessed
        /// token happened to match a real one. Every real token is a fixed
        /// 64-character hex string (see <see cref="GenerateToken"/>), so a
        /// length mismatch alone is not a meaningful timing signal here.
        /// </summary>
        private static bool FixedTimeEquals(string a, string b)
        {
            var bytesA = System.Text.Encoding.UTF8.GetBytes(a);
            var bytesB = System.Text.Encoding.UTF8.GetBytes(b);
            return bytesA.Length == bytesB.Length && CryptographicOperations.FixedTimeEquals(bytesA, bytesB);
        }
    }
}
