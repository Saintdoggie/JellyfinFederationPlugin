using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;
using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using PlaybackInfoResponse = MediaBrowser.Model.MediaInfo.PlaybackInfoResponse;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

public class SoundHelpPlanTests
{
    internal static DeviceProfile Browser() => new()
    {
        DirectPlayProfiles = new[]
        {
            new DirectPlayProfile { Type = DlnaProfileType.Video, Container = "mp4,m4v", VideoCodec = "h264", AudioCodec = "aac,mp3" },
            new DirectPlayProfile { Type = DlnaProfileType.Video, Container = "webm", VideoCodec = "vp9", AudioCodec = "opus" }
        }
    };

    internal static List<MediaStream> Streams(string video, string audio, string? language = "eng") => new()
    {
        new MediaStream { Type = MediaStreamType.Video, Index = 0, Codec = video },
        new MediaStream { Type = MediaStreamType.Audio, Index = 1, Codec = audio, Language = language, IsDefault = true }
    };

    [Fact]
    public void DtsInABrowser_PictureCopiedSoundConverted_IntoMp4()
    {
        var plan = DevicePlayFit.PlanSoundHelp(Browser(), Streams("h264", "dts"), null, "eng");
        Assert.NotNull(plan);
        Assert.True(plan!.ConvertSound);
        Assert.Equal("mp4", plan.Wrapper);
        Assert.Equal(1, plan.Audio.Index);
    }

    [Fact]
    public void AacInMatroska_OnlyTheWrapperChanges()
    {
        var plan = DevicePlayFit.PlanSoundHelp(Browser(), Streams("h264", "aac"), null, "eng");
        Assert.NotNull(plan);
        Assert.False(plan!.ConvertSound);
        Assert.Equal("mp4", plan.Wrapper);
    }

    [Fact]
    public void PictureTheDeviceCannotPlay_IsNotHelped()
        => Assert.Null(DevicePlayFit.PlanSoundHelp(Browser(), Streams("hevc", "dts"), null, "eng"));

    [Fact]
    public void ADeviceThatSaidNothing_IsNotHelped()
        => Assert.Null(DevicePlayFit.PlanSoundHelp(new DeviceProfile(), Streams("h264", "dts"), null, "eng"));

    [Fact]
    public void DtsIsNeverCopiedIntoMp4_EvenWhenTheDeviceListsIt()
    {
        var tv = new DeviceProfile
        {
            DirectPlayProfiles = new[]
            {
                new DirectPlayProfile { Type = DlnaProfileType.Video, Container = "mp4", VideoCodec = "h264", AudioCodec = "aac,dts" },
                new DirectPlayProfile { Type = DlnaProfileType.Video, Container = "ts", VideoCodec = "h264", AudioCodec = "aac,dts" }
            }
        };
        var plan = DevicePlayFit.PlanSoundHelp(tv, Streams("h264", "dts"), null, "eng");
        Assert.NotNull(plan);
        Assert.False(plan!.ConvertSound);
        Assert.Equal("ts", plan.Wrapper);
    }

    [Fact]
    public void TheTrackThePlayerAskedFor_IsUsed()
    {
        var streams = Streams("h264", "aac");
        streams.Add(new MediaStream { Type = MediaStreamType.Audio, Index = 2, Codec = "dts", Language = "ger" });
        var plan = DevicePlayFit.PlanSoundHelp(Browser(), streams, 2, "eng");
        Assert.Equal(2, plan!.Audio.Index);
        Assert.True(plan.ConvertSound);
    }
}

[Collection("PluginInstance")]
public sealed class SoundHelpServiceTests
{
    private static RemoteServer Linked() => new()
    {
        Id = Guid.NewGuid().ToString(),
        Name = "Home",
        Url = "https://home.example.com",
        Kind = ServerKind.Jellyfin,
        Enabled = true,
        Linked = true,
        LinkKey = FederationTokenAuth.GenerateToken(),
        ApiKey = FederationTokenAuth.GenerateToken()
    };

    private static MediaSourceInfo FriendSource(string audio) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Path = "http://127.0.0.1:8096/Plugins/Federation/Stream?serverId=x&itemId=y&sig=secret",
        Protocol = MediaProtocol.Http,
        Container = "mkv",
        SupportsDirectPlay = false,
        SupportsDirectStream = true,
        SupportsTranscoding = true,
        TranscodingUrl = "/videos/x/master.m3u8",
        MediaStreams = SoundHelpPlanTests.Streams("h264", audio)
    };

    private static SoundHelpService NewService(bool helperAvailable = true)
        => new(NullLogger<SoundHelpService>.Instance, new Mock<IRemoteServerClientFactory>().Object, () => "/usr/bin/ffmpeg")
        {
            HelperCheck = (_, _) => Task.FromResult(helperAvailable)
        };

    private static SoundHelpService.SoundHelpRequest Request(Guid user, Guid item, long? start = null)
        => new(user, item, SoundHelpPlanTests.Browser(), null, start, "eng");

    [Fact]
    public async Task ServerThatMayConvert_LeavesPlaysToJellyfin()
    {
        using var plugin = new RealPluginInstance();
        Plugin.Instance!.Configuration.LocalTranscodeRestriction = LocalTranscodeRestriction.AlwaysAllow;
        Plugin.Instance.Configuration.RemoteServers.Add(Linked());
        var response = new PlaybackInfoResponse { MediaSources = new[] { FriendSource("dts") } };
        Assert.Equal(0, await NewService().ApplyAsync(response, Request(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None));
        Assert.Equal("/videos/x/master.m3u8", response.MediaSources[0].TranscodingUrl);
    }

    [Fact]
    public async Task DtsOnAServerThatMayNotConvert_GoesToTheLinkedServer_WithAnAddressThatHoldsNoKey()
    {
        using var plugin = new RealPluginInstance();
        Plugin.Instance!.Configuration.LocalTranscodeRestriction = LocalTranscodeRestriction.BlockAll;
        Plugin.Instance.Configuration.RemoteServers.Add(Linked());
        var service = NewService();
        var (user, item) = (Guid.NewGuid(), Guid.NewGuid());
        var response = new PlaybackInfoResponse { PlaySessionId = "abc", MediaSources = new[] { FriendSource("dts") } };

        Assert.Equal(1, await service.ApplyAsync(response, Request(user, item, 300_000_000), CancellationToken.None));
        var source = response.MediaSources[0];
        Assert.StartsWith("/Plugins/Federation/SoundHelp/", source.TranscodingUrl, StringComparison.Ordinal);
        Assert.Contains("stream.mp4?StartTimeTicks=300000000&PlaySessionId=abc", source.TranscodingUrl, StringComparison.Ordinal);
        Assert.DoesNotContain("sig=", source.TranscodingUrl, StringComparison.Ordinal);
        Assert.DoesNotContain("127.0.0.1", source.TranscodingUrl, StringComparison.Ordinal);
        Assert.False(source.SupportsDirectPlay);
        Assert.False(source.SupportsDirectStream);
        Assert.True(source.SupportsTranscoding);
        Assert.Equal("mp4", source.TranscodingContainer);

        var starting = service.Starting(user, item);
        Assert.NotNull(starting);
        Assert.Contains("Home is converting only the sound", starting!.Value.Reason, StringComparison.Ordinal);
        Assert.Contains("DTS", starting.Value.Reason, StringComparison.Ordinal);
        Assert.True(starting.Value.Seconds >= 1);
        Assert.Null(service.Starting(Guid.NewGuid(), item));
    }

    [Fact]
    public async Task NoLinkedServerThatCanConvert_DtsIsLeftAlone_ButAWrapperChangeStillHappens()
    {
        using var plugin = new RealPluginInstance();
        Plugin.Instance!.Configuration.LocalTranscodeRestriction = LocalTranscodeRestriction.BlockAll;
        Plugin.Instance.Configuration.RemoteServers.Add(Linked());
        var service = NewService(helperAvailable: false);
        var dts = new PlaybackInfoResponse { MediaSources = new[] { FriendSource("dts") } };
        Assert.Equal(0, await service.ApplyAsync(dts, Request(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None));

        var aac = new PlaybackInfoResponse { MediaSources = new[] { FriendSource("aac") } };
        Assert.Equal(1, await service.ApplyAsync(aac, Request(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None));
        Assert.Contains("/SoundHelp/", aac.MediaSources[0].TranscodingUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASourceThatPlaysAsStored_IsLeftAlone()
    {
        using var plugin = new RealPluginInstance();
        Plugin.Instance!.Configuration.LocalTranscodeRestriction = LocalTranscodeRestriction.BlockAll;
        var source = FriendSource("aac");
        source.SupportsDirectPlay = true;
        source.TranscodingUrl = null;
        Assert.Equal(0, await NewService().ApplyAsync(new PlaybackInfoResponse { MediaSources = new[] { source } }, Request(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None));
    }

    [Fact]
    public void Arguments_KeepTheSoundOnThePicture_AfterASeek()
    {
        var plan = DevicePlayFit.PlanSoundHelp(SoundHelpPlanTests.Browser(), SoundHelpPlanTests.Streams("h264", "dts"), null, "eng")!;
        var ticket = new SoundHelpService.Ticket("t", Guid.NewGuid(), Guid.NewGuid(), "/media/film.mkv", new Dictionary<string, string>(), plan, 125_000_000, "h", "Home", DateTime.UtcNow);
        var reader = string.Join(' ', SoundHelpService.ReaderArguments(ticket, 4000));
        Assert.Contains("-copyts -ss 12.500 -i /media/film.mkv", reader, StringComparison.Ordinal);
        Assert.Contains("-map 0:v:0 -c copy -f matroska pipe:1", reader, StringComparison.Ordinal);
        Assert.Contains("-map 0:1 -c copy -f matroska tcp://127.0.0.1:4000", reader, StringComparison.Ordinal);
        Assert.DoesNotContain("aac", reader, StringComparison.Ordinal);

        var writer = string.Join(' ', SoundHelpService.WriterArguments(ticket, 4001));
        Assert.Equal(2, writer.Split("-itsoffset -12.500").Length - 1);
        Assert.Contains("-map 0:v:0 -map 1:a:0 -c copy", writer, StringComparison.Ordinal);
        Assert.Contains("frag_keyframe+empty_moov", writer, StringComparison.Ordinal);
        Assert.DoesNotContain("libx264", writer, StringComparison.Ordinal);

        var encoder = string.Join(' ', SoundHelpEncoder.Arguments());
        Assert.Contains("-map 0:a:0 -vn", encoder, StringComparison.Ordinal);
        Assert.Contains("-c:a aac -ac 2", encoder, StringComparison.Ordinal);
    }
}

[Collection("PluginInstance")]
public sealed class SoundHelpEncoderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sound-help-tests-").FullName;

    public void Dispose()
    {
        // Left for the OS temp cleaner; holds one tiny script.
    }

    // Stands in for ffmpeg: copies its input to its output, so each reply echoes what was sent.
    private string EchoEncoder()
    {
        var path = Path.Combine(_dir, "echo.sh");
        File.WriteAllText(path, "#!/bin/sh\nexec cat\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private static Stream Bytes(string text) => new MemoryStream(Encoding.ASCII.GetBytes(text));

    private static readonly string Play = new('a', 32);

    [Fact]
    public async Task PiecesInOrder_ComeBackConverted_AndEndReturnsTheRest()
    {
        using var plugin = new RealPluginInstance();
        Plugin.Instance!.Configuration.LocalTranscodeRestriction = LocalTranscodeRestriction.AlwaysAllow;
        using var encoder = new SoundHelpEncoder(NullLogger<SoundHelpEncoder>.Instance, EchoEncoder);
        var config = Plugin.Instance.Configuration;

        var first = await encoder.FeedAsync("srv", Play, 0, false, Bytes("one-"), config, CancellationToken.None);
        Assert.Equal(200, first.Status);
        var second = await encoder.FeedAsync("srv", Play, 1, false, Bytes("two-"), config, CancellationToken.None);
        var last = await encoder.FeedAsync("srv", Play, 2, true, Bytes("three"), config, CancellationToken.None);
        Assert.Equal("one-two-three", Encoding.ASCII.GetString(first.Body.Concat(second.Body).Concat(last.Body).ToArray()));
        Assert.Equal(0, encoder.Running);
    }

    [Fact]
    public async Task OutOfOrderUnknownAndOversizedPieces_AreRefused()
    {
        using var plugin = new RealPluginInstance();
        Plugin.Instance!.Configuration.LocalTranscodeRestriction = LocalTranscodeRestriction.AlwaysAllow;
        using var encoder = new SoundHelpEncoder(NullLogger<SoundHelpEncoder>.Instance, EchoEncoder);
        var config = Plugin.Instance.Configuration;

        Assert.Equal(400, (await encoder.FeedAsync("srv", "not-a-play", 0, false, Bytes("x"), config, CancellationToken.None)).Status);
        Assert.Equal(410, (await encoder.FeedAsync("srv", Play, 3, false, Bytes("x"), config, CancellationToken.None)).Status);
        Assert.Equal(200, (await encoder.FeedAsync("srv", Play, 0, false, Bytes("x"), config, CancellationToken.None)).Status);
        Assert.Equal(410, (await encoder.FeedAsync("other", Play, 1, false, Bytes("x"), config, CancellationToken.None)).Status);
        Assert.Equal(409, (await encoder.FeedAsync("srv", Play, 5, false, Bytes("x"), config, CancellationToken.None)).Status);
        var big = new MemoryStream(new byte[SoundHelpEncoder.MaxPieceBytes + 1]);
        Assert.Equal(413, (await encoder.FeedAsync("srv", Play, 1, false, big, config, CancellationToken.None)).Status);
        Assert.Equal(0, encoder.Running);
    }

    [Fact]
    public async Task AServerThatMayNotConvert_OrIsFull_SaysSo()
    {
        using var plugin = new RealPluginInstance();
        var config = Plugin.Instance!.Configuration;
        using var encoder = new SoundHelpEncoder(NullLogger<SoundHelpEncoder>.Instance, EchoEncoder);

        config.LocalTranscodeRestriction = LocalTranscodeRestriction.BlockAll;
        Assert.NotNull(encoder.Unavailable(config));
        Assert.Equal(503, (await encoder.FeedAsync("srv", Play, 0, false, Bytes("x"), config, CancellationToken.None)).Status);

        config.LocalTranscodeRestriction = LocalTranscodeRestriction.AlwaysAllow;
        for (var i = 0; i < SoundHelpEncoder.MaxSessions; i++)
        {
            Assert.Equal(200, (await encoder.FeedAsync("srv", i.ToString("x32"), 0, false, Bytes("x"), config, CancellationToken.None)).Status);
        }

        Assert.Contains("already converting", encoder.Unavailable(config)!, StringComparison.Ordinal);
        Assert.Equal(503, (await encoder.FeedAsync("srv", Play, 0, false, Bytes("x"), config, CancellationToken.None)).Status);
        encoder.Stop("srv", 0.ToString("x32"));
        Assert.Null(encoder.Unavailable(config));
    }

    [Fact]
    public void WithoutFfmpeg_NothingIsOffered()
    {
        using var plugin = new RealPluginInstance();
        Plugin.Instance!.Configuration.LocalTranscodeRestriction = LocalTranscodeRestriction.AlwaysAllow;
        using var encoder = new SoundHelpEncoder(NullLogger<SoundHelpEncoder>.Instance, () => null);
        Assert.Contains("no ffmpeg", encoder.Unavailable(Plugin.Instance.Configuration)!, StringComparison.Ordinal);
    }
}
