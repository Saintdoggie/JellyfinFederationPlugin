using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Services;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

public class HttpContentReaderTests
{
    [Fact]
    public async Task ReadBytes_RejectsDeclaredAndUndeclaredOversizeBodies()
    {
        using var declared = new ByteArrayContent(new byte[101]);
        using var chunked = new StreamContent(new InfiniteZeroStream());
        chunked.Headers.ContentLength = null;

        await Assert.ThrowsAsync<InvalidDataException>(() => HttpContentReader.ReadBytesAsync(declared, 100, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => HttpContentReader.ReadBytesAsync(chunked, 100, CancellationToken.None));
    }

    [Fact]
    public async Task ReadText_UsesTheSameStreamingLimit()
    {
        using var content = new StreamContent(new InfiniteZeroStream());
        content.Headers.ContentLength = null;
        await Assert.ThrowsAsync<InvalidDataException>(() => HttpContentReader.ReadTextAsync(content, 100, CancellationToken.None));
    }

    [Fact]
    public async Task ReadBytes_ReturnsAnExactlyBoundedBody()
    {
        using var content = new ByteArrayContent(Encoding.UTF8.GetBytes("#EXTM3U"));
        Assert.Equal("#EXTM3U", Encoding.UTF8.GetString(await HttpContentReader.ReadBytesAsync(content, 7, CancellationToken.None)));
    }

    [Theory]
    [InlineData("image/jpeg", true)]
    [InlineData("image/png; charset=binary", true)]
    [InlineData("IMAGE/WEBP", true)]
    [InlineData("image/gif", true)]
    [InlineData("image/avif", true)]
    [InlineData("image/svg+xml", false)]
    [InlineData("text/html", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void BrowseImages_OnlyAllowPassiveRasterContent(string? value, bool expected)
    {
        Assert.Equal(expected, BrowseImagePolicy.TryGetContentType(value, out _));
    }

    private sealed class InfiniteZeroStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            buffer.Clear();
            return buffer.Length;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            buffer.Span.Clear();
            return ValueTask.FromResult(buffer.Length);
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
