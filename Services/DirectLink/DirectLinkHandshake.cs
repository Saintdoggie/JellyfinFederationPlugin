using System;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Federation.Services.DirectLink
{
    /// <summary>
    /// The QUIC half of making a direct link (run by <see cref="QuicTransportHost"/>):
    /// mutual TLS where each side accepts only the certificate fingerprint it
    /// learned in this session's signaling, then a proof on a control stream
    /// that both sides hold this session's key. Failures throw
    /// <see cref="System.IO.IOException"/> (network) or
    /// <see cref="System.Security.Authentication.AuthenticationException"/> (not the pinned friend).
    /// </summary>
    public static class DirectLinkHandshake
    {
        /// <summary>Initiator: connects from <paramref name="localPort"/> to the punched path and proves the session.</summary>
        /// <returns>The connection handle.</returns>
        public static Task<object> ConnectAsync(
            int localPort,
            IPEndPoint remote,
            X509Certificate2 ownCertificate,
            string expectedFingerprint,
            byte[] sessionKey,
            string sessionId,
            string ownFingerprint,
            CancellationToken cancellationToken)
            => QuicTransportHost.Connect(
                localPort,
                remote,
                ownCertificate,
                expectedFingerprint,
                DirectLinkProtocol.ConnectProof(sessionKey, true, sessionId, ownFingerprint, expectedFingerprint),
                DirectLinkProtocol.ConnectProof(sessionKey, false, sessionId, ownFingerprint, expectedFingerprint),
                cancellationToken);

        /// <summary>
        /// Responder: listens on <paramref name="localPort"/> for exactly one
        /// connection from <paramref name="expectedRemote"/>'s address with the
        /// pinned certificate, checks its proof, and answers with this side's.
        /// </summary>
        /// <returns>The connection handle.</returns>
        public static Task<object> AcceptAsync(
            int localPort,
            IPAddress expectedRemote,
            X509Certificate2 ownCertificate,
            string expectedFingerprint,
            byte[] sessionKey,
            string sessionId,
            string ownFingerprint,
            int maxStreams,
            TimeSpan wait,
            CancellationToken cancellationToken)
            => QuicTransportHost.Accept(
                localPort,
                expectedRemote,
                ownCertificate,
                expectedFingerprint,
                DirectLinkProtocol.ConnectProof(sessionKey, false, sessionId, expectedFingerprint, ownFingerprint),
                DirectLinkProtocol.ConnectProof(sessionKey, true, sessionId, expectedFingerprint, ownFingerprint),
                maxStreams,
                wait,
                cancellationToken);
    }
}
