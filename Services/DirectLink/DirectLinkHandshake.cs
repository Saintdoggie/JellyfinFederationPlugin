#pragma warning disable CA2252, CA1416 // System.Net.Quic: only reached when MsQuicLoader says QUIC works.
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Federation.Services.DirectLink
{
    /// <summary>
    /// The QUIC half of making a direct link: mutual TLS where each side accepts
    /// only the certificate fingerprint it learned in this session's signaling,
    /// then a proof on a control stream that both sides hold this session's key.
    /// </summary>
    public static class DirectLinkHandshake
    {
        private const byte ProofMarker = 0x01;

        private static readonly List<SslApplicationProtocol> Protocols = new() { new SslApplicationProtocol(DirectLinkProtocol.Alpn) };

        /// <summary>The link's idle timeout.</summary>
        public static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);

        /// <summary>The link's keep-alive interval (keeps both routers' mappings open).</summary>
        public static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(15);

        /// <summary>Initiator: connects from <paramref name="localPort"/> to the punched path and proves the session.</summary>
        public static async Task<QuicConnection> ConnectAsync(
            int localPort,
            IPEndPoint remote,
            X509Certificate2 ownCertificate,
            string expectedFingerprint,
            byte[] sessionKey,
            string sessionId,
            string ownFingerprint,
            CancellationToken cancellationToken)
        {
            var options = new QuicClientConnectionOptions
            {
                RemoteEndPoint = remote,
                LocalEndPoint = new IPEndPoint(IPAddress.Any, localPort),
                DefaultStreamErrorCode = 1,
                DefaultCloseErrorCode = 1,
                IdleTimeout = IdleTimeout,
                KeepAliveInterval = KeepAlive,
                HandshakeTimeout = TimeSpan.FromSeconds(10),
                MaxInboundBidirectionalStreams = 0,
                MaxInboundUnidirectionalStreams = 0,
                ClientAuthenticationOptions = new SslClientAuthenticationOptions
                {
                    ApplicationProtocols = Protocols,
                    TargetHost = "jellyfin-federation",
                    ClientCertificates = new X509CertificateCollection { ownCertificate },
                    RemoteCertificateValidationCallback = (_, certificate, _, _) => DirectLinkIdentity.Matches(certificate, expectedFingerprint)
                }
            };

            var connection = await QuicConnection.ConnectAsync(options, cancellationToken).ConfigureAwait(false);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                await using var control = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, timeout.Token).ConfigureAwait(false);
                var mine = DirectLinkProtocol.ConnectProof(sessionKey, true, sessionId, ownFingerprint, expectedFingerprint);
                var message = new byte[33];
                message[0] = ProofMarker;
                mine.CopyTo(message, 1);
                await control.WriteAsync(message, timeout.Token).ConfigureAwait(false);
                var answer = await ReadExactlyAsync(control, 33, timeout.Token).ConfigureAwait(false);
                var expected = DirectLinkProtocol.ConnectProof(sessionKey, false, sessionId, ownFingerprint, expectedFingerprint);
                if (answer == null || answer[0] != ProofMarker || !CryptographicOperations.FixedTimeEquals(answer.AsSpan(1), expected))
                {
                    throw new AuthenticationFailedException("the friend's link proof didn't match");
                }

                control.CompleteWrites();
                return connection;
            }
            catch
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        /// <summary>
        /// Responder: listens on <paramref name="localPort"/> for exactly one
        /// connection from <paramref name="expectedRemote"/>'s address with the
        /// pinned certificate, checks its proof, and answers with this side's.
        /// </summary>
        public static async Task<QuicConnection> AcceptAsync(
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
        {
            var serverOptions = new QuicServerConnectionOptions
            {
                DefaultStreamErrorCode = 1,
                DefaultCloseErrorCode = 1,
                IdleTimeout = IdleTimeout,
                KeepAliveInterval = KeepAlive,
                HandshakeTimeout = TimeSpan.FromSeconds(10),
                MaxInboundBidirectionalStreams = maxStreams,
                MaxInboundUnidirectionalStreams = 0,
                ServerAuthenticationOptions = new SslServerAuthenticationOptions
                {
                    ApplicationProtocols = Protocols,
                    ServerCertificate = ownCertificate,
                    ClientCertificateRequired = true,
                    RemoteCertificateValidationCallback = (_, certificate, _, _) => DirectLinkIdentity.Matches(certificate, expectedFingerprint)
                }
            };

            var listener = await QuicListener.ListenAsync(
                new QuicListenerOptions
                {
                    ListenEndPoint = new IPEndPoint(IPAddress.Any, localPort),
                    ApplicationProtocols = Protocols,
                    ListenBacklog = 4,
                    ConnectionOptionsCallback = (_, hello, _) => ValueTask.FromResult(serverOptions)
                },
                cancellationToken).ConfigureAwait(false);

            QuicConnection? connection = null;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(wait);
                while (connection == null)
                {
                    QuicConnection candidate;
                    try
                    {
                        candidate = await listener.AcceptConnectionAsync(timeout.Token).ConfigureAwait(false);
                    }
                    catch (QuicException) when (!timeout.IsCancellationRequested)
                    {
                        // A handshake that failed (wrong certificate, garbage): keep waiting for the friend.
                        continue;
                    }
                    catch (AuthenticationException) when (!timeout.IsCancellationRequested)
                    {
                        continue;
                    }

                    if (!candidate.RemoteEndPoint.Address.Equals(expectedRemote))
                    {
                        await candidate.DisposeAsync().ConfigureAwait(false);
                        continue;
                    }

                    connection = candidate;
                }

                await using var control = await connection.AcceptInboundStreamAsync(timeout.Token).ConfigureAwait(false);
                var proof = await ReadExactlyAsync(control, 33, timeout.Token).ConfigureAwait(false);
                var expected = DirectLinkProtocol.ConnectProof(sessionKey, true, sessionId, expectedFingerprint, ownFingerprint);
                if (proof == null || proof[0] != ProofMarker || !CryptographicOperations.FixedTimeEquals(proof.AsSpan(1), expected))
                {
                    throw new AuthenticationFailedException("the friend's link proof didn't match");
                }

                var mine = DirectLinkProtocol.ConnectProof(sessionKey, false, sessionId, expectedFingerprint, ownFingerprint);
                var message = new byte[33];
                message[0] = ProofMarker;
                mine.CopyTo(message, 1);
                await control.WriteAsync(message, timeout.Token).ConfigureAwait(false);
                control.CompleteWrites();
                return connection;
            }
            catch
            {
                if (connection != null)
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                }

                throw;
            }
            finally
            {
                // The accepted connection outlives the listener; nobody else can
                // connect to this port once it's gone.
                await listener.DisposeAsync().ConfigureAwait(false);
            }
        }

        private static async Task<byte[]?> ReadExactlyAsync(QuicStream stream, int count, CancellationToken cancellationToken)
        {
            var buffer = new byte[count];
            var filled = 0;
            while (filled < count)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(filled), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return null;
                }

                filled += read;
            }

            return buffer;
        }
    }

    /// <summary>The other side failed the link's proof.</summary>
    public sealed class AuthenticationFailedException : Exception
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="AuthenticationFailedException"/> class.
        /// </summary>
        /// <param name="message">What failed.</param>
        public AuthenticationFailedException(string message)
            : base(message)
        {
        }
    }
}
