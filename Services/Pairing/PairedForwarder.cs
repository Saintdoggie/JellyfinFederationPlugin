using System;
using System.Linq;
using System.Diagnostics;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Services.DirectLink;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services.Pairing
{
    /// <summary>
    /// Serves the requests a paired friend opens on its link: each stream is one HTTP/1.1
    /// request, checked by <see cref="PairedRequestGate"/>, then forwarded to this server's
    /// own Jellyfin on loopback with the link headers (docs/PAIRING.md).
    /// </summary>
    public sealed class PairedForwarder
    {
        /// <summary>Most streams open at once across all paired friends.</summary>
        public const int GlobalStreams = 256;

        private static readonly byte[] Forbidden = Encoding.ASCII.GetBytes("HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        private static readonly byte[] TooMany = Encoding.ASCII.GetBytes("HTTP/1.1 429 Too Many Requests\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        private static readonly byte[] BadGateway = Encoding.ASCII.GetBytes("HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        private static readonly SemaphoreSlim Global = new(GlobalStreams);

        private readonly Func<Uri> _localBase;
        private readonly ILogger _logger;

        /// <summary>Initializes a new instance of the <see cref="PairedForwarder"/> class.</summary>
        public PairedForwarder(Func<Uri> localBase, ILogger logger)
        {
            _localBase = localBase;
            _logger = logger;
        }

        /// <summary>Gets or sets how long an answer may stall before it's cut.</summary>
        public TimeSpan StallTimeout { get; set; } = TimeSpan.FromSeconds(60);

        /// <summary>Gets or sets the most streams open at once per friend.</summary>
        public int StreamsPerFriend { get; set; } = 32;

        /// <summary>Gets or sets the most new requests per second per friend.</summary>
        public int RequestsPerSecond { get; set; } = 20;

        /// <summary>
        /// Accepts and serves streams until the link closes. <paramref name="friend"/>
        /// says, at each request, who the link belongs to and whether this side's
        /// safety number is confirmed (it can change while the link is up).
        /// </summary>
        /// <returns>When the link ends.</returns>
        public async Task RunAsync(DirectLinkConnection link, Func<(string FriendId, string? IssuedToken, bool Confirmed)?> friend)
        {
            var slots = new SemaphoreSlim(StreamsPerFriend);
            var rate = new TokenBucket(RequestsPerSecond);
            try
            {
                while (!link.Closing.IsCancellationRequested)
                {
                    var stream = await QuicTransportHost.AcceptStream(link.Quic, link.Closing).ConfigureAwait(false);
                    if (!rate.Take() || !await slots.WaitAsync(0).ConfigureAwait(false))
                    {
                        _ = RefuseAsync(stream, TooMany, link.Closing);
                        continue;
                    }

                    if (!await Global.WaitAsync(0).ConfigureAwait(false))
                    {
                        slots.Release();
                        _ = RefuseAsync(stream, TooMany, link.Closing);
                        continue;
                    }

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await ServeAsync(stream, link, friend()).ConfigureAwait(false);
                        }
                        finally
                        {
                            Global.Release();
                            slots.Release();
                        }
                    });
                }
            }
            catch (IOException ex)
            {
                link.MarkClosed(ex.Message);
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

        /// <summary>Serves one request stream.</summary>
        /// <returns>When the answer is done.</returns>
        internal async Task ServeAsync(Stream stream, DirectLinkConnection? link, (string FriendId, string? IssuedToken, bool Confirmed)? friend)
        {
            link?.StreamDelta(1);
            try
            {
                await using (stream.ConfigureAwait(false))
                {
                    var cancel = link?.Closing ?? CancellationToken.None;
                    if (friend == null)
                    {
                        await WriteAndFinishAsync(stream, Forbidden, cancel).ConfigureAwait(false);
                        return;
                    }

                    var (head, body, refusal) = await ReadRequestAsync(stream, friend.Value.Confirmed, cancel).ConfigureAwait(false);
                    if (head != null && head.Auth == PairedAuth.HeaderToken && !TokenMatches(head.FederationToken, friend.Value.IssuedToken))
                    {
                        (head, refusal) = (null, "the token isn't this link's friend's");
                    }

                    if (head == null)
                    {
                        _logger.LogDebug("[Federation] Paired link: refused a request: {Reason}", refusal);
                        await WriteAndFinishAsync(stream, Forbidden, cancel).ConfigureAwait(false);
                        return;
                    }

                    var local = _localBase();
                    if (!IsThisMachine(local.Host))
                    {
                        // The link headers carry this process's secret: never off this machine
                        // (an InternalServerUrl pointing at a proxy or another host).
                        _logger.LogWarning("[Federation] Paired link: this server's own address {Base} isn't on this machine; set the internal server URL to a local address", local);
                        await WriteAndFinishAsync(stream, BadGateway, cancel).ConfigureAwait(false);
                        return;
                    }

                    Stream upstream;
                    TcpClient? tcp = null;
                    try
                    {
                        tcp = new TcpClient { NoDelay = true };
                        using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancel))
                        {
                            connectTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                            await tcp.ConnectAsync(local.Host, local.Port, connectTimeout.Token).ConfigureAwait(false);
                        }

                        upstream = tcp.GetStream();
                        if (local.Scheme == Uri.UriSchemeHttps)
                        {
                            var loopback = System.Net.IPAddress.TryParse(local.Host, out var hostIp) ? System.Net.IPAddress.IsLoopback(hostIp) : string.Equals(local.Host, "localhost", StringComparison.OrdinalIgnoreCase);
                            var tls = loopback ? new SslStream(upstream, false, (_, _, _, _) => true) : new SslStream(upstream, false);
                            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = local.Host }, cancel).ConfigureAwait(false);
                            upstream = tls;
                        }
                    }
                    catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or System.Security.Authentication.AuthenticationException)
                    {
                        tcp?.Dispose();
                        _logger.LogWarning("[Federation] Paired link: couldn't reach this server's own Jellyfin at {Base}: {Error}", local, ex.Message);
                        await WriteAndFinishAsync(stream, BadGateway, cancel).ConfigureAwait(false);
                        return;
                    }

                    using (tcp)
                    {
                        await using (upstream.ConfigureAwait(false))
                        {
                            var hostHeader = local.IsDefaultPort ? local.Host : local.Host + ":" + local.Port;
                            var peer = link?.RemoteEndPoint.Address.ToString() ?? string.Empty;
                            await upstream.WriteAsync(head.BuildForward(hostHeader, local.AbsolutePath, friend.Value.FriendId, peer), cancel).ConfigureAwait(false);
                            if (body.Length > 0)
                            {
                                await upstream.WriteAsync(body, cancel).ConfigureAwait(false);
                            }

                            await upstream.FlushAsync(cancel).ConfigureAwait(false);

                            // Nothing more is ever read from the friend: the request is
                            // exactly the head and its declared body.
                            var buffer = new byte[64 * 1024];
                            using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                            while (true)
                            {
                                stall.CancelAfter(StallTimeout);
                                var read = await upstream.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
                                if (read <= 0)
                                {
                                    break;
                                }

                                await stream.WriteAsync(buffer.AsMemory(0, read), stall.Token).ConfigureAwait(false);
                                link?.AddOut(read);
                            }

                            FinishWrites(stream);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // The friend gave up or the link dropped mid-answer.
            }
            finally
            {
                link?.StreamDelta(-1);
            }
        }

        /// <summary>
        /// Reads the head and exactly its declared body. Any byte past the body that has
        /// already arrived is a second request smuggled in, and refuses the whole stream.
        /// </summary>
        /// <returns>The head and body, or null with the reason.</returns>
        /// <summary>True for loopback, "localhost" or an address on one of this machine's interfaces.</summary>
        internal static bool IsThisMachine(string host)
        {
            if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!System.Net.IPAddress.TryParse(host.Trim('[', ']'), out var ip))
            {
                return false;
            }

            if (System.Net.IPAddress.IsLoopback(ip))
            {
                return true;
            }

            try
            {
                return System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                    .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                    .Any(a => a.Address.Equals(ip));
            }
            catch (System.Net.NetworkInformation.NetworkInformationException)
            {
                return false;
            }
        }

        internal static async Task<(PairedRequestGate? Head, byte[] Body, string? Refusal)> ReadRequestAsync(Stream stream, bool confirmed, CancellationToken cancellationToken)
        {
            var buffer = new byte[DirectLinkRequestHead.MaxHeadBytes + PairedRequestGate.MaxBodyBytes + 1];
            var filled = 0;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                var end = -1;
                while (end < 0)
                {
                    if (filled >= DirectLinkRequestHead.MaxHeadBytes + 1)
                    {
                        return (null, Array.Empty<byte>(), "request head too large");
                    }

                    var read = await stream.ReadAsync(buffer.AsMemory(filled, DirectLinkRequestHead.MaxHeadBytes + 1 - filled), timeout.Token).ConfigureAwait(false);
                    if (read == 0)
                    {
                        return (null, Array.Empty<byte>(), "the request ended early");
                    }

                    filled += read;
                    end = DirectLinkRequestHead.FindHeadEnd(buffer.AsSpan(0, filled));
                }

                var head = PairedRequestGate.Parse(buffer.AsSpan(0, end), confirmed, out var refusal);
                if (head == null)
                {
                    return (null, Array.Empty<byte>(), refusal);
                }

                var want = end + head.ContentLength;
                if (filled > want)
                {
                    return (null, Array.Empty<byte>(), "bytes after the request");
                }

                while (filled < want)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(filled, want - filled), timeout.Token).ConfigureAwait(false);
                    if (read == 0)
                    {
                        return (null, Array.Empty<byte>(), "the body ended early");
                    }

                    filled += read;
                }

                return (head, buffer.AsSpan(end, head.ContentLength).ToArray(), null);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return (null, Array.Empty<byte>(), "the request took too long");
            }
        }

        private static bool TokenMatches(string? given, string? expected)
        {
            if (string.IsNullOrEmpty(given) || string.IsNullOrEmpty(expected))
            {
                return false;
            }

            return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(expected));
        }

        private static async Task RefuseAsync(Stream stream, byte[] response, CancellationToken cancellationToken)
        {
            await using (stream.ConfigureAwait(false))
            {
                await WriteAndFinishAsync(stream, response, cancellationToken).ConfigureAwait(false);
            }
        }

        private static void FinishWrites(Stream stream)
        {
            if (QuicTransportHost.Available)
            {
                QuicTransportHost.CompleteWrites(stream);
            }
        }

        private static async Task WriteAndFinishAsync(Stream stream, byte[] response, CancellationToken cancellationToken)
        {
            try
            {
                await stream.WriteAsync(response, cancellationToken).ConfigureAwait(false);
                FinishWrites(stream);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // The friend is gone.
            }
        }

        private sealed class TokenBucket
        {
            private readonly int _perSecond;
            private readonly Stopwatch _clock = Stopwatch.StartNew();
            private double _tokens;
            private double _last;

            public TokenBucket(int perSecond)
            {
                _perSecond = perSecond;
                _tokens = perSecond;
            }

            public bool Take()
            {
                var now = _clock.Elapsed.TotalSeconds;
                _tokens = Math.Min(_perSecond, _tokens + ((now - _last) * _perSecond));
                _last = now;
                if (_tokens < 1)
                {
                    return false;
                }

                _tokens -= 1;
                return true;
            }
        }
    }
}
