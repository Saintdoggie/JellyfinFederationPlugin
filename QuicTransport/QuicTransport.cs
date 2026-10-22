using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Federation.QuicTransport
{
    /// <summary>
    /// The direct link's QUIC transport, called by the plugin through delegates
    /// (only BCL types cross over: connections are opaque objects, streams are
    /// <see cref="Stream"/>s). Failures surface as <see cref="IOException"/> with a
    /// readable message, or <see cref="AuthenticationException"/> when the other
    /// side isn't the pinned friend.
    /// </summary>
    public static class QuicTransport
    {
        private const byte ProofMarker = 0x01;
        private const string Alpn = "jf-fed-dl/1";

        private static readonly List<SslApplicationProtocol> Protocols = new() { new SslApplicationProtocol(Alpn) };
        private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(15);

        /// <summary>Whether QUIC works in this load context.</summary>
        public static bool IsSupported() => QuicConnection.IsSupported && QuicListener.IsSupported;

        /// <summary>The runtime's reason QUIC doesn't work (or the library version when it does).</summary>
        public static string Diagnostics()
        {
            try
            {
                var api = typeof(QuicConnection).Assembly.GetType("System.Net.Quic.MsQuicApi");
                const BindingFlags Flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
                var reason = api?.GetProperty("NotSupportedReason", Flags)?.GetValue(null) as string;
                var version = api?.GetProperty("MsQuicLibraryVersion", Flags)?.GetValue(null) as string;
                return string.IsNullOrEmpty(reason) ? version ?? "ok" : reason;
            }
            catch (Exception ex)
            {
                return ex.GetType().Name;
            }
        }

        /// <summary>
        /// Initiator: connects from <paramref name="localPort"/> to the punched
        /// path, pinning the friend's certificate, then sends this side's proof and
        /// checks the friend's.
        /// </summary>
        public static async Task<object> ConnectAsync(
            int localPort,
            IPEndPoint remote,
            X509Certificate2 ownCertificate,
            string expectedFingerprint,
            byte[] ownProof,
            byte[] expectedProof,
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
                    RemoteCertificateValidationCallback = (_, certificate, _, _) => Matches(certificate, expectedFingerprint)
                }
            };

            QuicConnection connection;
            try
            {
                connection = await QuicConnection.ConnectAsync(options, cancellationToken).ConfigureAwait(false);
            }
            catch (QuicException ex)
            {
                throw new IOException(Describe(ex), ex);
            }

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                await using var control = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, timeout.Token).ConfigureAwait(false);
                await control.WriteAsync(ProofMessage(ownProof), timeout.Token).ConfigureAwait(false);
                var answer = await ReadExactlyAsync(control, 33, timeout.Token).ConfigureAwait(false);
                if (!ProofMatches(answer, expectedProof))
                {
                    throw new AuthenticationException("the friend's link proof didn't match");
                }

                control.CompleteWrites();
                return connection;
            }
            catch (Exception ex)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                throw Translate(ex);
            }
        }

        /// <summary>
        /// Responder: listens on <paramref name="localPort"/> for one connection
        /// from <paramref name="expectedRemote"/>'s address with the pinned client
        /// certificate, checks its proof and answers with this side's. The listener
        /// is gone afterwards; the connection lives on.
        /// </summary>
        public static async Task<object> AcceptAsync(
            int localPort,
            IPAddress expectedRemote,
            X509Certificate2 ownCertificate,
            string expectedFingerprint,
            byte[] ownProof,
            byte[] expectedProof,
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
                    RemoteCertificateValidationCallback = (_, certificate, _, _) => Matches(certificate, expectedFingerprint)
                }
            };

            QuicListener listener;
            try
            {
                listener = await QuicListener.ListenAsync(
                    new QuicListenerOptions
                    {
                        ListenEndPoint = new IPEndPoint(IPAddress.Any, localPort),
                        ApplicationProtocols = Protocols,
                        ListenBacklog = 4,
                        ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(serverOptions)
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (QuicException ex)
            {
                throw new IOException(Describe(ex), ex);
            }

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
                    catch (Exception ex) when ((ex is QuicException || ex is AuthenticationException) && !timeout.IsCancellationRequested)
                    {
                        // A handshake that failed (wrong certificate, garbage): keep waiting for the friend.
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
                if (!ProofMatches(proof, expectedProof))
                {
                    throw new AuthenticationException("the friend's link proof didn't match");
                }

                await control.WriteAsync(ProofMessage(ownProof), timeout.Token).ConfigureAwait(false);
                control.CompleteWrites();
                return connection;
            }
            catch (Exception ex)
            {
                if (connection != null)
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                }

                throw Translate(ex);
            }
            finally
            {
                await listener.DisposeAsync().ConfigureAwait(false);
            }
        }

        /// <summary>Opens a request stream on a connection.</summary>
        public static async Task<Stream> OpenStreamAsync(object connection, CancellationToken cancellationToken)
        {
            try
            {
                return await ((QuicConnection)connection).OpenOutboundStreamAsync(QuicStreamType.Bidirectional, cancellationToken).ConfigureAwait(false);
            }
            catch (QuicException ex)
            {
                throw new IOException(Describe(ex), ex);
            }
        }

        /// <summary>
        /// Waits for the next stream the other side opens (unidirectional ones
        /// are refused). Throws <see cref="IOException"/> once the connection ends.
        /// </summary>
        public static async Task<Stream> AcceptStreamAsync(object connection, CancellationToken cancellationToken)
        {
            var quic = (QuicConnection)connection;
            while (true)
            {
                QuicStream stream;
                try
                {
                    stream = await quic.AcceptInboundStreamAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (QuicException ex)
                {
                    throw new IOException(Describe(ex), ex);
                }

                if (stream.Type == QuicStreamType.Bidirectional)
                {
                    return stream;
                }

                stream.Abort(QuicAbortDirection.Both, 1);
                await stream.DisposeAsync().ConfigureAwait(false);
            }
        }

        /// <summary>Ends this side's writes on a stream (the other side reads end-of-stream).</summary>
        public static void CompleteWrites(Stream stream)
        {
            if (stream is QuicStream quic)
            {
                quic.CompleteWrites();
            }
        }

        /// <summary>Aborts a stream both ways.</summary>
        public static void Abort(Stream stream)
        {
            if (stream is QuicStream quic)
            {
                quic.Abort(QuicAbortDirection.Both, 1);
            }
        }

        /// <summary>The other side's UDP endpoint.</summary>
        public static IPEndPoint RemoteEndPoint(object connection) => ((QuicConnection)connection).RemoteEndPoint;

        /// <summary>This side's UDP endpoint.</summary>
        public static IPEndPoint LocalEndPoint(object connection) => ((QuicConnection)connection).LocalEndPoint;

        /// <summary>Closes the connection gracefully, then releases it.</summary>
        public static async Task CloseAsync(object connection)
        {
            var quic = (QuicConnection)connection;
            try
            {
                await quic.CloseAsync(0).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Closing a dead connection may throw; it's gone either way.
            }

            await quic.DisposeAsync().ConfigureAwait(false);
        }

        private static bool Matches(X509Certificate? certificate, string expected)
        {
            if (certificate == null || expected.Length != 64)
            {
                return false;
            }

            byte[] wanted;
            try
            {
                wanted = Convert.FromHexString(expected);
            }
            catch (FormatException)
            {
                return false;
            }

            return CryptographicOperations.FixedTimeEquals(SHA256.HashData(certificate.GetRawCertData()), wanted);
        }

        private static byte[] ProofMessage(byte[] proof)
        {
            var message = new byte[33];
            message[0] = ProofMarker;
            proof.AsSpan(0, 32).CopyTo(message.AsSpan(1));
            return message;
        }

        private static bool ProofMatches(byte[]? message, byte[] expected)
            => message != null && message.Length == 33 && message[0] == ProofMarker && CryptographicOperations.FixedTimeEquals(message.AsSpan(1), expected);

        private static Exception Translate(Exception ex) => ex switch
        {
            QuicException quic => new IOException(Describe(quic), quic),
            _ => ex
        };

        private static string Describe(QuicException ex) => ex.QuicError switch
        {
            QuicError.ConnectionIdle => "the link went quiet (no answer for 60 s)",
            QuicError.ConnectionTimeout => "the friend didn't answer",
            QuicError.ConnectionAborted => "the friend closed the link",
            QuicError.ConnectionRefused => "the friend refused the link",
            QuicError.OperationAborted => "closed",
            _ => "QUIC: " + ex.QuicError
        };

        private static async Task<byte[]?> ReadExactlyAsync(Stream stream, int count, CancellationToken cancellationToken)
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
}
