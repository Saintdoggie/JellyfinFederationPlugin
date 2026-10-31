using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Services.DirectLink;
using Xunit;
using Xunit.Abstractions;

namespace Jellyfin.Plugin.Federation.Tests;

/// <summary>
/// The direct-link relay fetches the player's next segments alongside the one it
/// is on (live, 2026-10-22: one download to friend-a stopped near 110 Mbps, four
/// at once reached 178), and serves each once to the exact address the playlist
/// listed.
/// </summary>
public class SegmentReadAheadTests
{
    private const string Gateway = "/Plugins/Federation/Peer/Play/TOK/videos/abc/";
    private readonly ITestOutputHelper _output;

    public SegmentReadAheadTests(ITestOutputHelper output) => _output = output;

    private static string Segment(int i) => Gateway + "hls1/main/" + i + ".ts?PlaySessionId=1&VideoCodec=h264,hevc&runtimeTicks=" + (i * 30000000L);

    private static string PlaylistText(int count, bool relative = false)
    {
        var sb = new StringBuilder("#EXTM3U\n#EXT-X-PLAYLIST-TYPE:VOD\n#EXT-X-TARGETDURATION:3\n");
        for (var i = 0; i < count; i++)
        {
            sb.Append("#EXTINF:3.0,\n").Append(relative ? Segment(i).Substring(Gateway.Length) : "http://10.0.0.5:8096" + Segment(i)).Append('\n');
        }

        return sb.Append("#EXT-X-ENDLIST\n").ToString();
    }

    private static byte[] Body(string target) => Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat(target, 2000)));

    private static async Task<byte[]> ServeAsync(SegmentReadAhead.Entry entry)
    {
        using (entry)
        {
            Assert.True(await entry.WaitHeadAsync(CancellationToken.None));
            using var copy = new MemoryStream();
            await entry.ServeAsync(copy, CancellationToken.None);
            return copy.ToArray();
        }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), "timed out");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task NextSegments_AreFetchedAhead_AndServedOnce()
    {
        using var readAhead = new SegmentReadAhead(now: () => DateTime.UtcNow);
        var fetched = new ConcurrentBag<string>();
        readAhead.RememberPlaylist("f1", Gateway + "main.m3u8?PlaySessionId=1", PlaylistText(10));

        readAhead.After("f1", Segment(0), 3, (target, _) =>
        {
            fetched.Add(target);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Body(target)) });
        });

        await WaitUntil(() => fetched.Count == 3);
        Assert.Equal(new[] { Segment(1), Segment(2), Segment(3) }, fetched.OrderBy(t => t.Length).ThenBy(t => t, StringComparer.Ordinal).ToArray());

        var one = readAhead.Take("f1", Segment(1));
        Assert.NotNull(one);
        Assert.Equal(Body(Segment(1)), await ServeAsync(one!));

        // Once only, and only for the friend it was fetched from.
        Assert.Null(readAhead.Take("f1", Segment(1)));
        Assert.Null(readAhead.Take("f2", Segment(2)));
        Assert.Null(readAhead.Take("f1", Segment(4)));
    }

    [Fact]
    public async Task PlayerAddress_MatchesThePlaylistLine_ThoughEscapedDifferently_AndRelativeLinesResolve()
    {
        using var readAhead = new SegmentReadAhead(now: () => DateTime.UtcNow);
        readAhead.RememberPlaylist("f1", Gateway + "main.m3u8?PlaySessionId=1", PlaylistText(5, relative: true));
        readAhead.After("f1", Segment(0).Replace(",", "%2C", StringComparison.Ordinal), 1, (target, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Body(target)) }));

        await WaitUntil(() => readAhead.Pending == 1);
        var next = readAhead.Take("f1", Segment(1).Replace(",", "%2C", StringComparison.Ordinal));
        Assert.NotNull(next);
        Assert.Equal(Body(Segment(1)), await ServeAsync(next!));
    }

    [Fact]
    public async Task AddressesNotInAPlaylist_StartNothing()
    {
        using var readAhead = new SegmentReadAhead(now: () => DateTime.UtcNow);
        var calls = 0;
        readAhead.RememberPlaylist("f1", Gateway + "main.m3u8", PlaylistText(10));
        Func<string, CancellationToken, Task<HttpResponseMessage>> fetch = (_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        };

        readAhead.After("f1", Gateway + "hls1/main/0.ts?PlaySessionId=other", 3, fetch);
        readAhead.After("f2", Segment(0), 3, fetch);
        readAhead.After("f1", Segment(0), 0, fetch);

        // Nested playlists and lines off the friend's gateway are never listed.
        readAhead.RememberPlaylist("f1", Gateway + "master.m3u8", "#EXTM3U\nhttp://x" + Gateway + "main.m3u8\nhttp://evil.example/System/Info\n");
        readAhead.After("f1", Gateway + "main.m3u8", 3, fetch);
        await Task.Delay(100);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task AnOversizedPlaylist_GetsNoReadAhead()
    {
        using var readAhead = new SegmentReadAhead(now: () => DateTime.UtcNow);
        var calls = 0;
        readAhead.RememberPlaylist("f1", Gateway + "main.m3u8", PlaylistText(30001));
        readAhead.After("f1", Segment(0), 3, (_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        await Task.Delay(50);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task ASeek_DropsWhatWasFetchedForTheOldPosition()
    {
        using var readAhead = new SegmentReadAhead(now: () => DateTime.UtcNow);
        var cancelled = new ConcurrentBag<string>();
        readAhead.RememberPlaylist("f1", Gateway + "main.m3u8", PlaylistText(100));
        Func<string, CancellationToken, Task<HttpResponseMessage>> slow = async (target, token) =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            catch (OperationCanceledException)
            {
                cancelled.Add(target);
                throw;
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        };

        readAhead.After("f1", Segment(10), 3, slow);
        await WaitUntil(() => readAhead.Pending == 3);
        readAhead.After("f1", Segment(60), 3, slow);

        await WaitUntil(() => cancelled.Count == 3);
        Assert.Equal(new[] { Segment(11), Segment(12), Segment(13) }.OrderBy(t => t), cancelled.OrderBy(t => t));
        Assert.Equal(3, readAhead.Pending);
        Assert.Null(readAhead.Take("f1", Segment(11)));
    }

    [Fact]
    public async Task AFriendError_IsNotHeld_SoThePlayerAsksTheUsualWay()
    {
        using var readAhead = new SegmentReadAhead(now: () => DateTime.UtcNow);
        readAhead.RememberPlaylist("f1", Gateway + "main.m3u8", PlaylistText(5));
        readAhead.After("f1", Segment(0), 2, (target, _) => target == Segment(1)
            ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))
            : Task.FromException<HttpResponseMessage>(new HttpRequestException("link dropped")));

        await WaitUntil(() => readAhead.Pending == 0);
        Assert.Null(readAhead.Take("f1", Segment(1)));
        Assert.Null(readAhead.Take("f1", Segment(2)));
        Assert.Equal(0, readAhead.HeldBytes);
    }

    [Theory]
    [InlineData(1024, 512)]
    [InlineData(512, 1024)]
    public async Task ContentLengthMismatch_IsDroppedSoThePlayerFallsBack(int declared, int actual)
    {
        using var readAhead = new SegmentReadAhead(now: () => DateTime.UtcNow);
        readAhead.RememberPlaylist("f1", Gateway + "main.m3u8", PlaylistText(2));
        readAhead.After("f1", Segment(0), 1, (_, _) =>
        {
            var content = new ByteArrayContent(new byte[actual]);
            content.Headers.ContentLength = declared;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });

        await WaitUntil(() => readAhead.Pending == 0);
        Assert.Null(readAhead.Take("f1", Segment(1)));
        Assert.Equal(0, readAhead.HeldBytes);
    }

    [Fact]
    public async Task ALinkDropMidSegment_ReachesThePlayer_AsAnIOError()
    {
        using var readAhead = new SegmentReadAhead(now: () => DateTime.UtcNow);
        var gate = new TaskCompletionSource();
        readAhead.RememberPlaylist("f1", Gateway + "main.m3u8", PlaylistText(5));
        readAhead.After("f1", Segment(0), 1, (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new BreakingStream(gate.Task))
        }));

        await WaitUntil(() => readAhead.HeldBytes > 0);
        using var entry = readAhead.Take("f1", Segment(1))!;
        Assert.True(await entry.WaitHeadAsync(CancellationToken.None));
        gate.SetResult();
        await Assert.ThrowsAnyAsync<IOException>(() => entry.ServeAsync(Stream.Null, CancellationToken.None));
    }

    [Fact]
    public async Task MemoryIsCapped_UnclaimedFetchesPause_UntilThePlayerClaimsThem()
    {
        const int Size = 4 * 1024 * 1024;
        using var readAhead = new SegmentReadAhead(maxBytes: 3 * 1024 * 1024, now: () => DateTime.UtcNow);
        readAhead.RememberPlaylist("f1", Gateway + "main.m3u8", PlaylistText(5));
        readAhead.After("f1", Segment(0), 2, (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[Size])
        }));

        await Task.Delay(300);
        Assert.InRange(readAhead.HeldBytes, 1, 3 * 1024 * 1024);

        // Claimed segments stream straight through, so both finish.
        var first = await ServeAsync(readAhead.Take("f1", Segment(1))!);
        var second = await ServeAsync(readAhead.Take("f1", Segment(2))!);
        Assert.Equal(Size, first.Length);
        Assert.Equal(Size, second.Length);
        Assert.Equal(0, readAhead.HeldBytes);
    }

    [Fact]
    public async Task UnclaimedSegments_GoAfterAMinute()
    {
        var now = DateTime.UtcNow;
        using var readAhead = new SegmentReadAhead(now: () => now);
        readAhead.RememberPlaylist("f1", Gateway + "main.m3u8", PlaylistText(5));
        readAhead.After("f1", Segment(0), 2, (target, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Body(target)) }));
        await WaitUntil(() => readAhead.HeldBytes == Body(Segment(1)).Length + Body(Segment(2)).Length);

        now += TimeSpan.FromSeconds(30);
        readAhead.Sweep();
        Assert.Equal(2, readAhead.Pending);

        now += SegmentReadAhead.Keep;
        readAhead.Sweep();
        Assert.Equal(0, readAhead.Pending);
        Assert.Equal(0, readAhead.HeldBytes);
    }

    [Fact]
    public async Task ClaimedProducer_CannotExceedGlobalMemoryBudget()
    {
        const int MaxBytes = 256 * 1024;
        const int EntryBudget = MaxBytes / 24;
        using var readAhead = new SegmentReadAhead(maxBytes: MaxBytes, now: () => DateTime.UtcNow);
        readAhead.RememberPlaylist("f1", Gateway + "main.m3u8?PlaySessionId=1", PlaylistText(2));
        var body = new ClaimOnSecondReadStream(readAhead, "f1", Segment(1), EntryBudget);
        readAhead.After("f1", Segment(0), 1, (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(body),
        }));

        // The stream claims its entry before returning the second chunk. The first
        // chunk already fills the budget, so the producer must wait for ServeAsync
        // to drain it rather than treating a claimed entry as exempt.
        using var entry = await body.Claimed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(await entry.WaitHeadAsync(CancellationToken.None));
        using var copy = new MemoryStream();
        await entry.ServeAsync(copy, CancellationToken.None);

        Assert.Equal(2 * EntryBudget, copy.Length);
        Assert.True(body.BytesAtEnd.HasValue);
        Assert.InRange(body.BytesAtEnd.Value, 0, EntryBudget);
        Assert.Equal(0, readAhead.HeldBytes);
    }

    [Fact]
    public async Task EncodedQueryDelimiters_DoNotAliasDistinctSegments()
    {
        using var readAhead = new SegmentReadAhead(now: () => DateTime.UtcNow);
        var prefix = Gateway + "hls1/main/shared.ts?VideoCodec=";
        var encodedValue = prefix + "h264%26AudioCodec%3Daac";
        var separateParameters = prefix + "h264&AudioCodec=aac";
        var playlist = "#EXTM3U\n"
            + "http://10.0.0.5:8096" + Segment(0) + "\n"
            + "http://10.0.0.5:8096" + encodedValue + "\n"
            + "http://10.0.0.5:8096" + separateParameters + "\n";
        readAhead.RememberPlaylist("f1", Gateway + "main.m3u8?PlaySessionId=1", playlist);
        var fetched = new ConcurrentBag<string>();
        readAhead.After("f1", Segment(0), 2, (target, _) =>
        {
            fetched.Add(target);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Body(target)),
            });
        });

        // In the decoded whole-target key, %26 and %3D become real delimiters and
        // these different requests collide. Component-wise canonicalization keeps
        // them distinct while the existing comma-escaping equivalence still works.
        Assert.Equal(2, readAhead.Pending);
        await WaitUntil(() => fetched.Count == 2);
        Assert.Equal(Body(encodedValue), await ServeAsync(readAhead.Take("f1", encodedValue)!));
        Assert.Equal(Body(separateParameters), await ServeAsync(readAhead.Take("f1", separateParameters)!));
    }

    /// <summary>
    /// The live cap: each download from the friend is limited on its own, so a
    /// player fetching one segment at a time gets only that. With read-ahead the
    /// next segments are already arriving, and playback gets several downloads' speed.
    /// </summary>
    [Fact]
    public async Task APlayerFetchingOneAtATime_GetsMoreThanOneDownloadsSpeed()
    {
        const int Segments = 12;
        const int SegmentBytes = 1024 * 1024;
        const int PerDownloadBytesPerSecond = 8 * 1024 * 1024;

        async Task<double> PlayAsync(int depth)
        {
            using var readAhead = new SegmentReadAhead(now: () => DateTime.UtcNow);
            readAhead.RememberPlaylist("f1", Gateway + "main.m3u8", PlaylistText(Segments));
            Func<string, CancellationToken, Task<HttpResponseMessage>> fetch = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new ThrottledStream(SegmentBytes, PerDownloadBytesPerSecond))
            });

            var clock = Stopwatch.StartNew();
            for (var i = 0; i < Segments; i++)
            {
                if (readAhead.Take("f1", Segment(i)) is { } held)
                {
                    await ServeAsync(held);
                    readAhead.After("f1", Segment(i), depth, fetch);
                }
                else
                {
                    using var response = await fetch(Segment(i), CancellationToken.None);
                    readAhead.After("f1", Segment(i), depth, fetch);
                    await (await response.Content.ReadAsStreamAsync()).CopyToAsync(Stream.Null);
                }
            }

            return Segments * (double)SegmentBytes * 8 / 1e6 / clock.Elapsed.TotalSeconds;
        }

        var without = await PlayAsync(0);
        var with = await PlayAsync(3);
        _output.WriteLine($"one at a time: {without:F0} Mbps without read-ahead, {with:F0} Mbps with 3 ahead");
        Assert.True(with > without * 1.8, $"read-ahead {with:F0} Mbps vs {without:F0} Mbps");
    }

    /// <summary>Claims its read-ahead entry on the second read and records the global bytes held at EOF.</summary>
    private sealed class ClaimOnSecondReadStream : Stream
    {
        private readonly SegmentReadAhead _owner;
        private readonly string _serverId;
        private readonly string _target;
        private readonly int _chunkSize;
        private int _reads;

        public ClaimOnSecondReadStream(SegmentReadAhead owner, string serverId, string target, int chunkSize)
        {
            _owner = owner;
            _serverId = serverId;
            _target = target;
            _chunkSize = chunkSize;
        }

        public TaskCompletionSource<SegmentReadAhead.Entry> Claimed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public long? BytesAtEnd { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => 2 * _chunkSize;

        public override long Position { get; set; }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_reads == 2)
            {
                BytesAtEnd = _owner.HeldBytes;
                return ValueTask.FromResult(0);
            }

            _reads++;
            if (_reads == 2)
            {
                Claimed.SetResult(_owner.Take(_serverId, _target)!);
            }

            buffer.Span.Clear();
            return ValueTask.FromResult(_chunkSize);
        }

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Sends a fixed number of bytes no faster than a set rate.</summary>
    private sealed class ThrottledStream : Stream
    {
        private readonly long _length;
        private readonly double _bytesPerSecond;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private long _position;

        public ThrottledStream(long length, double bytesPerSecond)
        {
            _length = length;
            _bytesPerSecond = bytesPerSecond;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => _length;

        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var n = (int)Math.Min(Math.Min(buffer.Length, 64 * 1024), _length - _position);
            if (n <= 0)
            {
                return 0;
            }

            _position += n;
            var due = TimeSpan.FromSeconds(_position / _bytesPerSecond) - _clock.Elapsed;
            if (due > TimeSpan.Zero)
            {
                await Task.Delay(due, cancellationToken);
            }

            return n;
        }

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Sends one chunk, then fails like a dropped link once released.</summary>
    private sealed class BreakingStream : Stream
    {
        private readonly Task _release;
        private bool _sent;

        public BreakingStream(Task release) => _release = release;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_sent)
            {
                _sent = true;
                return Math.Min(buffer.Length, 1000);
            }

            await _release.WaitAsync(cancellationToken);
            throw new IOException("link dropped");
        }

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
