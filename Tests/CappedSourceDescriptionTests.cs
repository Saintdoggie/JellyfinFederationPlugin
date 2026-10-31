using System.Collections.Generic;
using System.Reflection;
using Jellyfin.Plugin.Federation.Configuration;
using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

public class CappedSourceDescriptionTests
{
    [Fact]
    public void CappedDirect_KeepsAllAudioAndTextSubtitles_ButNotImageSubtitles()
    {
        var method = typeof(FederationMediaSourceProvider).GetMethod("DescribeDeliveredSource", BindingFlags.NonPublic | BindingFlags.Static)!;
        var remote = new MediaSourceInfo
        {
            MediaStreams = new List<MediaStream>
            {
                new() { Type = MediaStreamType.Video, Index = 0, Codec = "hevc", Height = 1080, Width = 1920 },
                new() { Type = MediaStreamType.Audio, Index = 1, Codec = "truehd", Language = "eng", IsDefault = true, Channels = 8 },
                new() { Type = MediaStreamType.Audio, Index = 2, Codec = "dts", Language = "jpn", Channels = 6 },
                new() { Type = MediaStreamType.Subtitle, Index = 3, Codec = "ass", Language = "eng" },
                new() { Type = MediaStreamType.Subtitle, Index = 4, Codec = "pgssub" }
            }
        };

        var result = Assert.IsType<MediaSourceInfo>(method.Invoke(null, new object[]
        {
            new RemoteServer { StreamingMode = StreamingMode.Direct },
            "http://friend.example/video?capMbps=4&maxHeight=720",
            remote,
            "Movie"
        }));

        Assert.Equal(4, result.MediaStreams.Count);
        Assert.Contains(result.MediaStreams, s => s.Type == MediaStreamType.Audio && s.Index == 1 && s.Codec == "aac" && s.Language == "eng" && s.IsDefault);
        Assert.Contains(result.MediaStreams, s => s.Type == MediaStreamType.Audio && s.Index == 2 && s.Codec == "aac" && s.Language == "jpn");
        Assert.Contains(result.MediaStreams, s => s.Type == MediaStreamType.Subtitle && s.Index == 3 && s.Codec == "ass");
        Assert.DoesNotContain(result.MediaStreams, s => s.Index == 4);
    }
}
