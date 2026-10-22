using System;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.Federation.Services.Pairing
{
    /// <summary>
    /// How a request that came over a paired friend's link says so. The forwarder
    /// removes every incoming <c>X-Federation-Link*</c> header and adds these, with a
    /// secret that exists only in this process's memory, so nothing else can claim to
    /// be a link (docs/PAIRING.md, "Gate rules").
    /// </summary>
    public static class PairedLinkContext
    {
        /// <summary>The secret header.</summary>
        public const string SecretHeader = "X-Federation-Link";

        /// <summary>The link's friend id.</summary>
        public const string FriendHeader = "X-Federation-Link-Friend";

        /// <summary>The friend's address as the link sees it.</summary>
        public const string PeerHeader = "X-Federation-Link-Peer";

        /// <summary>Header-name prefix the forwarder strips from incoming requests.</summary>
        public const string Prefix = "X-Federation-Link";

        private static readonly byte[] SecretBytes = RandomNumberGenerator.GetBytes(32);

        /// <summary>Gets the secret's header value (hex).</summary>
        public static string Secret { get; } = Convert.ToHexString(SecretBytes);

        /// <summary>The friend whose link a request came over, or null when it didn't.</summary>
        /// <returns>The friend id.</returns>
        public static string? FriendId(HttpRequest? request)
        {
            if (request == null || !IsFromLink(request))
            {
                return null;
            }

            var id = request.Headers[FriendHeader].ToString();
            return string.IsNullOrEmpty(id) ? null : id;
        }

        /// <summary>The paired friend's real address for a request that came over its link.</summary>
        /// <returns>The address, or null.</returns>
        public static IPAddress? PeerAddress(HttpRequest? request)
            => request != null && IsFromLink(request) && IPAddress.TryParse(request.Headers[PeerHeader].ToString(), out var ip) ? ip : null;

        private static bool IsFromLink(HttpRequest request)
        {
            var values = request.Headers[SecretHeader];
            if (values.Count != 1 || request.Headers[FriendHeader].Count != 1)
            {
                return false;
            }

            var given = Encoding.ASCII.GetBytes(values.ToString());
            var expected = Encoding.ASCII.GetBytes(Secret);
            return CryptographicOperations.FixedTimeEquals(given, expected);
        }

        /// <summary>True for a header the forwarder must never pass through from the friend.</summary>
        /// <returns>True when the name starts with <see cref="Prefix"/>.</returns>
        public static bool IsReserved(string headerName)
            => headerName.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);
    }
}
