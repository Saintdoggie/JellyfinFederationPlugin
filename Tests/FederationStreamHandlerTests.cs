using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;
using Jellyfin.Plugin.Federation.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

/// <summary>
/// Regression coverage for the "seeking is broken / playback stalls" bug in Proxy
/// mode: <see cref="FederationStreamHandler.HandleProxyAsync"/> was reporting
/// <c>Content-Length</c> as <c>rangeStart + remote's partial length</c> instead of
/// just the remote's partial length, overstating it by exactly the seek offset on
/// every non-zero-start Range request - which is every seek, and every buffer-ahead
/// read during ordinary playback. Clients then waited for bytes that would never
/// arrive.
/// </summary>
[Collection("PluginInstance")]
public class FederationStreamHandlerTests : IDisposable
{
    private readonly RealPluginInstance _plugin;
    private readonly FederationStreamHandler _handler;

    public FederationStreamHandlerTests()
    {
        _plugin = new RealPluginInstance();
        _plugin.Configuration.RemoteServers.Add(new RemoteServer
        {
            Id = "serverA",
            Name = "Friend",
            Url = "http://friend.example:8096",
            ApiKey = "secret-key",
            Enabled = true
        });

        var cache = new FederationItemCache(NullLogger<FederationItemCache>.Instance);
        var bandwidthMonitor = new WanBandwidthMonitor(NullLogger<WanBandwidthMonitor>.Instance, Moq.Mock.Of<IRemoteServerClientFactory>());
        var federationManager = new FederationLibraryManager(
            Moq.Mock.Of<MediaBrowser.Controller.Library.ILibraryManager>(),
            NullLogger<FederationLibraryManager>.Instance,
            Moq.Mock.Of<IRemoteServerClientFactory>(),
            cache,
            bandwidthMonitor);

        _handler = new FederationStreamHandler(NullLogger<FederationStreamHandler>.Instance, federationManager);
    }

    public void Dispose()
    {
        FederationStreamHandler.HttpClientOverride = null;
        _plugin.Dispose();
    }

    private static (HttpRequest Request, HttpResponse Response, MemoryStream Body) MakeContext(string? rangeHeader)
    {
        var context = new DefaultHttpContext();
        if (rangeHeader != null)
        {
            context.Request.Headers["Range"] = rangeHeader;
        }

        var body = new MemoryStream();
        context.Response.Body = body;
        return (context.Request, context.Response, body);
    }

    [Fact]
    public async Task Seek_MidFileRangeRequest_ContentLengthMatchesTheActualPartialBodySize()
    {
        // A 6,000,000-byte file; the client seeks and asks for everything from byte
        // 1,000,000 onward. The remote correctly returns a 206 with a Content-Length
        // of 5,000,000 (the partial length), which is exactly what this proxy must
        // also report to the client - not 6,000,000 (1,000,000 + 5,000,000), which
        // is what the bug reported.
        var remoteBytes = new byte[5_000_000];
        new Random(42).NextBytes(remoteBytes);

        FederationStreamHandler.HttpClientOverride = new HttpClient(new FakeHandler(req =>
        {
            Assert.Equal("bytes=1000000-", string.Join(",", req.Headers.GetValues("Range")));

            var resp = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(remoteBytes)
            };
            resp.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(1_000_000, 5_999_999, 6_000_000);
            return resp;
        }));

        var (request, response, body) = MakeContext("bytes=1000000-");

        await _handler.HandleProxyAsync("serverA", Guid.NewGuid().ToString("N"), request, response, CancellationToken.None);

        Assert.Equal(5_000_000, response.ContentLength);
        Assert.Equal(remoteBytes.Length, body.Length);
    }

    [Fact]
    public async Task InitialZeroStartRequest_ContentLengthMatchesFullBody()
    {
        // The one case the bug happened to get right by coincidence (0 + length ==
        // length) - kept as a baseline so a future change can't silently break this
        // path while "fixing" the seek case.
        var remoteBytes = Encoding.UTF8.GetBytes("hello world, this is the whole file");

        FederationStreamHandler.HttpClientOverride = new HttpClient(new FakeHandler(req =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(remoteBytes) };
            return resp;
        }));

        var (request, response, body) = MakeContext(null);

        await _handler.HandleProxyAsync("serverA", Guid.NewGuid().ToString("N"), request, response, CancellationToken.None);

        Assert.Equal(remoteBytes.Length, response.ContentLength);
        Assert.Equal(remoteBytes.Length, body.Length);
    }

    [Fact]
    public async Task ClientCancelsMidStream_AbortsConnectionInsteadOfReturningWithIncompleteBody()
    {
        // Reproduces the "Response Content-Length mismatch: too few bytes written"
        // crash: headers (with a Content-Length covering the whole remaining file)
        // are already sent, then the client's request is cancelled mid-transfer -
        // exactly what happens on every ordinary seek/prefetch range request a
        // video player opens and abandons. Before the fix, HandleProxyAsync just
        // returned in that case, leaving ASP.NET/Kestrel to discover far fewer
        // bytes were written than promised and throw its own fatal exception,
        // resetting the connection instead of closing it cleanly. The fix must
        // instead abort the connection itself so no such mismatch is ever detected.
        // Headers (Content-Length = 1,000,000, the "whole remaining file") get
        // committed as soon as the response arrives; the body stream itself then
        // throws OperationCanceledException on its very first read, standing in
        // for a client that cancels mid-transfer - deterministic and independent
        // of whether any particular in-memory Stream/HttpClient plumbing happens
        // to honor a pre-cancelled CancellationToken.
        FederationStreamHandler.HttpClientOverride = new HttpClient(new FakeHandler(req =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new CancelingContent(1_000_000) }));

        var (request, response, body) = MakeContext(null);

        // DefaultHttpContext.Abort() only does anything if an
        // IHttpRequestLifetimeFeature is registered to receive it - a real
        // Kestrel-hosted request always has one; this bare test double does not
        // unless one is attached, so a spy stands in to observe whether our code
        // actually called HttpContext.Abort().
        var lifetime = new SpyRequestLifetimeFeature();
        response.HttpContext.Features.Set<IHttpRequestLifetimeFeature>(lifetime);

        await _handler.HandleProxyAsync("serverA", Guid.NewGuid().ToString("N"), request, response, CancellationToken.None);

        Assert.True(lifetime.AbortCalled);
    }

    private sealed class SpyRequestLifetimeFeature : IHttpRequestLifetimeFeature
    {
        public bool AbortCalled { get; private set; }

        public CancellationToken RequestAborted { get; set; }

        public void Abort() => AbortCalled = true;
    }

    /// <summary>
    /// HttpContent whose body stream reports a length but throws
    /// <see cref="OperationCanceledException"/> the moment anything tries to read
    /// it - simulating a client that disconnects after headers went out but before
    /// any of the promised body was delivered.
    /// </summary>
    private sealed class CancelingContent : HttpContent
    {
        private readonly long _length;

        public CancelingContent(long length)
        {
            _length = length;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => throw new NotSupportedException();

        protected override bool TryComputeLength(out long length)
        {
            length = _length;
            return true;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new ThrowingStream());

        private sealed class ThrowingStream : Stream
        {
            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Flush()
            {
            }

            public override int Read(byte[] buffer, int offset, int count) => throw new OperationCanceledException("Simulated client cancellation mid-stream");

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
                => throw new OperationCanceledException("Simulated client cancellation mid-stream");

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }

    [Fact]
    public async Task RetryReceivesNonPartialResponse_AbortsInsteadOfCorruptingStream()
    {
        // Reproduces an ultra-review finding: after a stall triggers an internal
        // retry from a non-zero offset, a remote/proxy that ignores the resumed
        // Range header and answers 200 (the whole file from byte 0) must not be
        // spliced onto what was already written - that corrupts the client's
        // decode stream and would eventually overflow the Content-Length already
        // committed on attempt 1.
        var firstChunk = new byte[100];
        new Random(11).NextBytes(firstChunk);
        var wrongChunk = new byte[900];
        new Random(12).NextBytes(wrongChunk);

        var callCount = 0;
        FederationStreamHandler.HttpClientOverride = new HttpClient(new FakeHandler(req =>
        {
            callCount++;
            if (callCount == 1)
            {
                // Answers fully (Content-Length covers the whole 1000-byte file),
                // then stalls after handing back only the first 100 bytes -
                // triggering the internal stall/retry path.
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new PartialThenFailContent(firstChunk, 1000) };
            }

            // The resumed request correctly asks for "bytes=100-", but this buggy
            // remote/proxy ignores it and answers with the whole file from byte 0
            // instead of a 206 starting at offset 100.
            Assert.Equal("bytes=100-", string.Join(",", req.Headers.GetValues("Range")));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(wrongChunk) };
        }));

        var (request, response, body) = MakeContext(null);
        var lifetime = new SpyRequestLifetimeFeature();
        response.HttpContext.Features.Set<IHttpRequestLifetimeFeature>(lifetime);

        await _handler.HandleProxyAsync("serverA", Guid.NewGuid().ToString("N"), request, response, CancellationToken.None);

        Assert.True(lifetime.AbortCalled);
        Assert.Equal(firstChunk, body.ToArray());
    }

    /// <summary>
    /// HttpContent whose body stream hands back a fixed chunk of bytes and then
    /// throws a plain (non-cancellation) IOException on the next read - standing
    /// in for a remote connection that stalls/drops partway through a response.
    /// </summary>
    private sealed class PartialThenFailContent : HttpContent
    {
        private readonly byte[] _data;
        private readonly long _totalLength;

        public PartialThenFailContent(byte[] data, long totalLength)
        {
            _data = data;
            _totalLength = totalLength;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => throw new NotSupportedException();

        protected override bool TryComputeLength(out long length)
        {
            length = _totalLength;
            return true;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new PartialThenFailStream(_data));

        private sealed class PartialThenFailStream : Stream
        {
            private readonly byte[] _data;
            private int _position;

            public PartialThenFailStream(byte[] data)
            {
                _data = data;
            }

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Flush()
            {
            }

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (_position >= _data.Length)
                {
                    throw new IOException("Simulated connection stall");
                }

                var toCopy = Math.Min(_data.Length - _position, buffer.Length);
                _data.AsSpan(_position, toCopy).CopyTo(buffer.Span);
                _position += toCopy;
                return ValueTask.FromResult(toCopy);
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_responder(request));
    }
}
