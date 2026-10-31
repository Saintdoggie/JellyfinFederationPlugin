using System;
using System.IO;
using Jellyfin.Plugin.Federation.Services;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

/// <summary>
/// A friend converting one of its titles for a viewer here hands over that
/// play's ffmpeg log: only that title's logs from the play's start, the end of a
/// long one, and never a key or token.
/// </summary>
public class FriendConversionLogsTests : IDisposable
{
    private static readonly Guid Item = Guid.Parse("d0015145401a25bf93aa7ba674ae20f9");
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fed-logs-" + Guid.NewGuid().ToString("N"));

    public FriendConversionLogsTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Write(string name, string text, DateTime writtenUtc)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, text);
        File.SetCreationTimeUtc(path, writtenUtc);
        File.SetLastWriteTimeUtc(path, writtenUtc);
        return path;
    }

    [Fact]
    public void Collect_ThisTitlesLogsSinceThePlayStarted_OldestFirst()
    {
        var start = new DateTime(2026, 10, 20, 15, 42, 0, DateTimeKind.Utc);
        Write($"FFmpeg.Transcode-2026-10-20_10-00-00_{Item:N}_aaaaaaaa.log", "yesterday's play", start.AddHours(-5));
        Write($"FFmpeg.Transcode-2026-10-20_15-42-05_{Item:N}_bbbbbbbb.log", "first start", start.AddSeconds(5));
        Write($"FFmpeg.Transcode-2026-10-20_15-43-00_{Item:N}_cccccccc.log", "after a seek", start.AddMinutes(1));
        Write($"FFmpeg.Transcode-2026-10-20_15-42-30_{Guid.NewGuid():N}_dddddddd.log", "another title", start.AddSeconds(30));
        Write("log_20261020.log", "server log", start.AddSeconds(10));

        var text = FriendConversionLogs.Collect(_dir, Item, start);

        Assert.NotNull(text);
        Assert.DoesNotContain("yesterday's play", text);
        Assert.DoesNotContain("another title", text);
        Assert.DoesNotContain("server log", text);
        Assert.True(text!.IndexOf("first start", StringComparison.Ordinal) < text.IndexOf("after a seek", StringComparison.Ordinal));
        Assert.Null(FriendConversionLogs.Collect(_dir, Guid.NewGuid(), start));
    }

    [Fact]
    public void Collect_KeepsTheEndOfAHugeLog()
    {
        var start = DateTime.UtcNow;
        Write($"FFmpeg.Transcode-x_{Item:N}_e.log", new string('a', FriendConversionLogs.MaxBytes) + "THE END", start);
        var text = FriendConversionLogs.Collect(_dir, Item, start)!;
        Assert.True(text.Length <= FriendConversionLogs.MaxBytes);
        Assert.EndsWith("THE END\n", text);
    }

    [Theory]
    [InlineData("-i \"https://f.example/Plugins/Federation/DirectStream/x?token=ABC123DEF\" -map 0", "ABC123DEF")]
    [InlineData("/videos/x/stream?api_key=0123456789abcdef&Static=true", "0123456789abcdef")]
    [InlineData("?ApiKey=c197348ca646438985fb1c42bc4eaa55&x=1", "c197348ca646438985fb1c42bc4eaa55")]
    [InlineData("{\"Token\":\"secretvalue\",\"Path\":\"/media/a.mkv\"}", "secretvalue")]
    [InlineData("X-Emby-Token: abcdefabcdef", "abcdefabcdef")]
    public void Redact_BlanksKeysAndTokens(string line, string secret)
    {
        var redacted = FriendConversionLogs.Redact(line);
        Assert.DoesNotContain(secret, redacted);
        Assert.Contains("REDACTED", redacted);
    }

    [Fact]
    public void Redact_KeepsTheUsefulParts()
    {
        const string line = "Stream #0:0: Video: hevc (Main 10), yuv420p10le; -c:v hevc_amf -b:v 39616000 /media/movies/Wreck-It Ralph (2012).mkv speed=2.31x";
        Assert.Equal(line, FriendConversionLogs.Redact(line));
    }

    [Fact]
    public void DescribeSource_IsSingleLineAndBounded()
    {
        var header = FriendConversionLogs.DescribeSource("friend\r\nForged: yes" + new string('x', 100), "session\r\nnext");
        Assert.DoesNotContain('\r', header);
        Assert.DoesNotContain('\n', header);
        Assert.Contains("friendForged: yes", header);
        Assert.Contains("sessionnext", header);
        Assert.True(header.Length < 220);
    }

    [Fact]
    public void LocalFileName_LooksLikeJellyfinsOwn_WithTheFriend()
    {
        var name = FriendConversionLogs.LocalFileName(new DateTime(2026, 10, 20, 10, 42, 19), Item, "friend-a", "cf92ce0dd0f04bd59eb81ab9aac8ebcc");
        Assert.Equal($"FFmpeg.Transcode-2026-10-20_10-42-19_{Item:N}_friend-a-cf92ce0d.log", name);
        Assert.DoesNotContain("/", FriendConversionLogs.LocalFileName(DateTime.Now, Item, "../../etc", "../x"));
    }
}
