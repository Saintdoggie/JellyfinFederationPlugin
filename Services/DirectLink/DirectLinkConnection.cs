using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Federation.Services.DirectLink
{
    /// <summary>
    /// One established direct link: the QUIC connection plus what's counted on
    /// it. Initiator side: <see cref="Http"/> sends friend requests over it, one
    /// QUIC stream per request.
    /// </summary>
    public sealed class DirectLinkConnection : IAsyncDisposable
    {
        private readonly CancellationTokenSource _closing = new();
        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private long _bytesIn;
        private long _bytesOut;
        private int _activeStreams;
        private int _disposed;
        private int _closeStarted;

        /// <summary>
        /// Initializes a new instance of the <see cref="DirectLinkConnection"/> class.
        /// </summary>
        /// <param name="connection">The authenticated QUIC connection (a <see cref="QuicTransportHost"/> handle).</param>
        /// <param name="serverId">The friend's configured server id.</param>
        /// <param name="isInitiator">True on the side that opens the streams.</param>
        /// <param name="federationToken">Initiator: gives the friend's current federation token, sent on every request (read each time, so a re-paired friend's new token is used).</param>
        public DirectLinkConnection(object connection, string serverId, bool isInitiator, Func<string?>? federationToken)
        {
            Quic = connection;
            ServerId = serverId;
            IsInitiator = isInitiator;
            RemoteEndPoint = QuicTransportHost.RemoteEndPoint(connection);
            LocalEndPoint = QuicTransportHost.LocalEndPoint(connection);
            ConnectedUtc = DateTime.UtcNow;
            if (isInitiator)
            {
                Http = CreateHttpClient(federationToken);
            }
        }

        /// <summary>Gets the QUIC connection handle.</summary>
        public object Quic { get; }

        /// <summary>Gets the friend's configured server id.</summary>
        public string ServerId { get; }

        /// <summary>Gets a value indicating whether this side opened the link.</summary>
        public bool IsInitiator { get; }

        /// <summary>Gets the friend's public UDP endpoint.</summary>
        public IPEndPoint RemoteEndPoint { get; }

        /// <summary>Gets this side's UDP endpoint.</summary>
        public IPEndPoint LocalEndPoint { get; }

        /// <summary>Gets when the link came up.</summary>
        public DateTime ConnectedUtc { get; }

        /// <summary>Gets the HTTP client that sends over the link (initiator only).</summary>
        public HttpClient? Http { get; }

        /// <summary>Gets bytes received over the link.</summary>
        public long BytesIn => Interlocked.Read(ref _bytesIn);

        /// <summary>Gets bytes sent over the link.</summary>
        public long BytesOut => Interlocked.Read(ref _bytesOut);

        /// <summary>Gets the number of requests in flight.</summary>
        public int ActiveStreams => Volatile.Read(ref _activeStreams);

        /// <summary>Gets a value indicating whether the link has ended.</summary>
        public bool IsClosed => _closed.Task.IsCompleted;

        /// <summary>Gets a task that completes when the link ends.</summary>
        public Task Closed => _closed.Task;

        /// <summary>Gets a token cancelled when the link ends.</summary>
        public CancellationToken Closing => _closing.Token;

        /// <summary>Gets or sets the throughput measured after connecting (0 = not yet).</summary>
        public double Mbps { get; set; }

        /// <summary>Gets why the link ended, once it has.</summary>
        public string? CloseReason { get; private set; }

        /// <summary>
        /// Initiator: watches for the connection ending. The initiator accepts no
        /// streams, so waiting for one only returns when the connection closes.
        /// </summary>
        public void StartCloseWatch()
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await using var unexpected = await QuicTransportHost.AcceptStream(Quic, _closing.Token).ConfigureAwait(false);
                    MarkClosed("the friend opened a stream it shouldn't have");
                }
                catch (IOException ex)
                {
                    MarkClosed(ex.Message);
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or InvalidOperationException)
                {
                    MarkClosed("closed");
                }
                catch (Exception ex)
                {
                    // Whatever happened, a watch that stops means the link is no longer trusted.
                    MarkClosed("the link failed: " + ex.GetType().Name);
                }
            });
        }

        /// <summary>Records that the link ended.</summary>
        public void MarkClosed(string reason)
        {
            if (Interlocked.CompareExchange(ref _closeStarted, 1, 0) == 0)
            {
                CloseReason = reason;
                _closed.TrySetResult();
                try
                {
                    _closing.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // Already disposed.
                }
            }
        }

        /// <summary>Counts bytes received.</summary>
        internal void AddIn(long bytes) => Interlocked.Add(ref _bytesIn, bytes);

        /// <summary>Counts bytes sent.</summary>
        internal void AddOut(long bytes) => Interlocked.Add(ref _bytesOut, bytes);

        /// <summary>Notes a stream opening (+1) or ending (-1).</summary>
        internal void StreamDelta(int delta) => Interlocked.Add(ref _activeStreams, delta);

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            MarkClosed(CloseReason ?? "closed");
            Http?.Dispose();
            await QuicTransportHost.Close(Quic).ConfigureAwait(false);
            _closing.Dispose();
        }

        private HttpClient CreateHttpClient(Func<string?>? federationToken)
        {
            var sockets = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                UseProxy = false,
                AutomaticDecompression = DecompressionMethods.None,
                ConnectTimeout = TimeSpan.FromSeconds(10),
                ConnectCallback = async (_, cancellationToken) =>
                {
                    if (IsClosed)
                    {
                        throw new HttpRequestException("The direct link is closed.");
                    }

                    try
                    {
                        var stream = await QuicTransportHost.OpenStream(Quic, cancellationToken).ConfigureAwait(false);
                        return new CountingStream(stream, this);
                    }
                    catch (IOException ex)
                    {
                        MarkClosed(ex.Message);
                        throw new HttpRequestException("The direct link is closed: " + ex.Message, ex);
                    }
                }
            };
            return new HttpClient(new OneRequestPerStreamHandler(sockets, federationToken))
            {
                BaseAddress = new Uri("http://federation-direct-link/"),
                Timeout = Timeout.InfiniteTimeSpan
            };
        }

        /// <summary>Each request gets its own QUIC stream; the friend closes it after one answer.</summary>
        private sealed class OneRequestPerStreamHandler : DelegatingHandler
        {
            private readonly Func<string?>? _token;

            public OneRequestPerStreamHandler(HttpMessageHandler inner, Func<string?>? token)
                : base(inner)
            {
                _token = token;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                request.Headers.ConnectionClose = true;
                if (_token?.Invoke() is { Length: > 0 } token)
                {
                    request.Headers.Remove(FederationTokenAuth.Header);
                    request.Headers.TryAddWithoutValidation(FederationTokenAuth.Header, token);
                }

                request.Version = HttpVersion.Version11;
                request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
                return base.SendAsync(request, cancellationToken);
            }
        }

        /// <summary>A stream wrapper that counts traffic on the link.</summary>
        internal sealed class CountingStream : Stream
        {
            private readonly Stream _inner;
            private readonly DirectLinkConnection _owner;
            private int _done;

            public CountingStream(Stream inner, DirectLinkConnection owner)
            {
                _inner = inner;
                _owner = owner;
                owner.StreamDelta(1);
            }

            public override bool CanRead => _inner.CanRead;

            public override bool CanSeek => false;

            public override bool CanWrite => _inner.CanWrite;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Flush() => _inner.Flush();

            public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

            public override int Read(byte[] buffer, int offset, int count)
            {
                var n = _inner.Read(buffer, offset, count);
                _owner.AddIn(n);
                return n;
            }

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                var n = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                _owner.AddIn(n);
                return n;
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
                => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

            public override void Write(byte[] buffer, int offset, int count)
            {
                _inner.Write(buffer, offset, count);
                _owner.AddOut(count);
            }

            public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
                _owner.AddOut(buffer.Length);
            }

            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
                => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override async ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _done, 1) == 0)
                {
                    _owner.StreamDelta(-1);
                }

                await _inner.DisposeAsync().ConfigureAwait(false);
                await base.DisposeAsync().ConfigureAwait(false);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing && Interlocked.Exchange(ref _done, 1) == 0)
                {
                    _owner.StreamDelta(-1);
                    _inner.Dispose();
                }

                base.Dispose(disposing);
            }
        }
    }
}
