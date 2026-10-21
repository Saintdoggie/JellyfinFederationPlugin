#pragma warning disable CA2252, CA1416 // System.Net.Quic: only reached when MsQuicLoader says QUIC works.
using System;
using System.IO;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services.DirectLink
{
    /// <summary>
    /// Responder side: serves the friend's requests that arrive over a direct
    /// link. Each QUIC stream carries one HTTP/1.1 request. It's checked by
    /// <see cref="DirectLinkRequestHead"/> (only GET/HEAD to the play, direct
    /// stream and speed-test routes), then piped to this server's own Jellyfin on
    /// its loopback address, and the answer is piped back.
    /// </summary>
    public sealed class DirectLinkForwarder
    {
        private static readonly byte[] Forbidden = Encoding.ASCII.GetBytes("HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        private static readonly byte[] BadGateway = Encoding.ASCII.GetBytes("HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");

        private readonly Func<Uri> _localBase;
        private readonly ILogger _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="DirectLinkForwarder"/> class.
        /// </summary>
        /// <param name="localBase">This server's own Jellyfin address (loopback).</param>
        /// <param name="logger">Logger.</param>
        public DirectLinkForwarder(Func<Uri> localBase, ILogger logger)
        {
            _localBase = localBase;
            _logger = logger;
        }

        /// <summary>Gets or sets the most requests served at once on one link.</summary>
        public int MaxConcurrentStreams { get; set; } = 128;

        /// <summary>Accepts and serves streams until the link ends.</summary>
        public async Task RunAsync(DirectLinkConnection link, string friendName)
        {
            using var slots = new SemaphoreSlim(MaxConcurrentStreams);
            try
            {
                while (!link.Closing.IsCancellationRequested)
                {
                    var stream = await link.Quic.AcceptInboundStreamAsync(link.Closing).ConfigureAwait(false);
                    if (stream.Type != QuicStreamType.Bidirectional || !await slots.WaitAsync(0).ConfigureAwait(false))
                    {
                        stream.Abort(QuicAbortDirection.Both, 1);
                        await stream.DisposeAsync().ConfigureAwait(false);
                        continue;
                    }

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await ServeAsync(stream, link, friendName).ConfigureAwait(false);
                        }
                        finally
                        {
                            slots.Release();
                        }
                    });
                }
            }
            catch (QuicException ex)
            {
                link.MarkClosed(DirectLinkConnection.Describe(ex));
            }
            catch (OperationCanceledException)
            {
                link.MarkClosed(link.CloseReason ?? "closed");
            }
            catch (ObjectDisposedException)
            {
                link.MarkClosed("closed");
            }
        }

        /// <summary>Serves one request stream (also used by tests with any duplex stream).</summary>
        internal async Task ServeAsync(Stream stream, DirectLinkConnection? link, string friendName)
        {
            link?.StreamDelta(1);
            try
            {
                await using (stream.ConfigureAwait(false))
                {
                    var cancel = link?.Closing ?? CancellationToken.None;
                    var (head, leftover, refusal) = await ReadHeadAsync(stream, cancel).ConfigureAwait(false);
                    if (head == null || leftover > 0)
                    {
                        _logger.LogDebug("[Federation] Direct link: refused a request from {Friend}: {Reason}", friendName, refusal ?? "a body isn't allowed");
                        await WriteAndFinishAsync(stream, Forbidden, cancel).ConfigureAwait(false);
                        return;
                    }

                    var local = _localBase();
                    Stream upstream;
                    TcpClient? tcp = null;
                    try
                    {
                        tcp = new TcpClient();
                        tcp.NoDelay = true;
                        using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancel))
                        {
                            connectTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                            await tcp.ConnectAsync(local.Host, local.Port, connectTimeout.Token).ConfigureAwait(false);
                        }

                        upstream = tcp.GetStream();
                        if (local.Scheme == Uri.UriSchemeHttps)
                        {
                            // This server's own address, as its admin configured it.
                            var tls = new SslStream(upstream, false, (_, _, _, _) => true);
                            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = local.Host }, cancel).ConfigureAwait(false);
                            upstream = tls;
                        }
                    }
                    catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or System.Security.Authentication.AuthenticationException)
                    {
                        tcp?.Dispose();
                        _logger.LogWarning("[Federation] Direct link: couldn't reach this server's own Jellyfin at {Base}: {Error}", local, ex.Message);
                        await WriteAndFinishAsync(stream, BadGateway, cancel).ConfigureAwait(false);
                        return;
                    }

                    using (tcp)
                    {
                        await using (upstream.ConfigureAwait(false))
                        {
                            var hostHeader = local.IsDefaultPort ? local.Host : local.Host + ":" + local.Port;
                            await upstream.WriteAsync(head.BuildForward(hostHeader, local.AbsolutePath), cancel).ConfigureAwait(false);
                            await upstream.FlushAsync(cancel).ConfigureAwait(false);

                            var buffer = new byte[64 * 1024];
                            int read;
                            while ((read = await upstream.ReadAsync(buffer, cancel).ConfigureAwait(false)) > 0)
                            {
                                await stream.WriteAsync(buffer.AsMemory(0, read), cancel).ConfigureAwait(false);
                                link?.AddOut(read);
                            }

                            if (stream is QuicStream quic)
                            {
                                quic.CompleteWrites();
                            }
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is QuicException or IOException or OperationCanceledException or ObjectDisposedException)
            {
                // The friend's player gave up or the link dropped mid-answer.
            }
            finally
            {
                link?.StreamDelta(-1);
            }
        }

        /// <summary>
        /// Reads a request head (16 KB, 10 s at most). Returns the parsed head and
        /// how many bytes came after it (must be 0: no bodies).
        /// </summary>
        internal static async Task<(DirectLinkRequestHead? Head, int Leftover, string? Refusal)> ReadHeadAsync(Stream stream, CancellationToken cancellationToken)
        {
            var buffer = new byte[DirectLinkRequestHead.MaxHeadBytes + 1];
            var filled = 0;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                while (filled < buffer.Length)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(filled), timeout.Token).ConfigureAwait(false);
                    if (read == 0)
                    {
                        return (null, 0, "the request ended early");
                    }

                    filled += read;
                    var end = DirectLinkRequestHead.FindHeadEnd(buffer.AsSpan(0, filled));
                    if (end > 0)
                    {
                        var head = DirectLinkRequestHead.Parse(buffer.AsSpan(0, end), out var refusal);
                        return (head, filled - end, refusal);
                    }
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return (null, 0, "the request head took too long");
            }

            return (null, 0, "request head too large");
        }

        private static async Task WriteAndFinishAsync(Stream stream, byte[] response, CancellationToken cancellationToken)
        {
            try
            {
                await stream.WriteAsync(response, cancellationToken).ConfigureAwait(false);
                if (stream is QuicStream quic)
                {
                    quic.CompleteWrites();
                }
            }
            catch (Exception ex) when (ex is QuicException or IOException or OperationCanceledException)
            {
                // The friend is gone.
            }
        }
    }
}
