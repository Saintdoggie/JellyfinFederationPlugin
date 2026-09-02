using System.Collections.Generic;
using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

/// <summary>
/// Covers the "prefer higher quality" advisor's pure decision logic
/// (<see cref="FederationQualityAdvisorService.IsUpgrade"/> and
/// <see cref="FederationQualityAdvisorService.BestVideoStream"/>), extracted as
/// internal static methods specifically so they're testable without a real
/// <c>BaseItem</c>/library mock. The full <c>FindUpgrades</c> scan (which needs a
/// live library enumeration) is exercised the same way the rest of this plugin's
/// live-server behavior is - not a unit test.
/// </summary>
public class FederationQualityAdvisorServiceTests
{
    [Fact]
    public void IsUpgrade_HigherRemoteResolution_IsUpgrade()
    {
        Assert.True(FederationQualityAdvisorService.IsUpgrade(localHeight: 1080, localBitrate: 8_000_000, remoteHeight: 2160, remoteBitrate: 4_000_000));
    }

    [Fact]
    public void IsUpgrade_LowerRemoteResolution_NotUpgrade()
    {
        Assert.False(FederationQualityAdvisorService.IsUpgrade(localHeight: 2160, localBitrate: 4_000_000, remoteHeight: 1080, remoteBitrate: 20_000_000));
    }

    [Fact]
    public void IsUpgrade_SameResolutionMeaningfullyHigherBitrate_IsUpgrade()
    {
        // 1.15x threshold - 10,000,000 vs 8,000,000 is a 1.25x jump.
        Assert.True(FederationQualityAdvisorService.IsUpgrade(localHeight: 1080, localBitrate: 8_000_000, remoteHeight: 1080, remoteBitrate: 10_000_000));
    }

    [Fact]
    public void IsUpgrade_SameResolutionTrivialBitrateDifference_NotUpgrade()
    {
        // Ordinary re-encode noise (5% higher) should not flag - avoids
        // constantly nagging over a difference nobody would notice.
        Assert.False(FederationQualityAdvisorService.IsUpgrade(localHeight: 1080, localBitrate: 8_000_000, remoteHeight: 1080, remoteBitrate: 8_400_000));
    }

    [Fact]
    public void IsUpgrade_SameResolutionSameBitrate_NotUpgrade()
    {
        Assert.False(FederationQualityAdvisorService.IsUpgrade(localHeight: 1080, localBitrate: 8_000_000, remoteHeight: 1080, remoteBitrate: 8_000_000));
    }

    [Fact]
    public void IsUpgrade_UnknownLocalBitrate_NotUpgrade()
    {
        // Same resolution, local bitrate unknown (0) - nothing to compare, so
        // this must never be treated as an upgrade just because remote has a
        // reported bitrate and local doesn't.
        Assert.False(FederationQualityAdvisorService.IsUpgrade(localHeight: 1080, localBitrate: 0, remoteHeight: 1080, remoteBitrate: 10_000_000));
    }

    [Fact]
    public void IsUpgrade_UnknownRemoteBitrate_NotUpgrade()
    {
        Assert.False(FederationQualityAdvisorService.IsUpgrade(localHeight: 1080, localBitrate: 8_000_000, remoteHeight: 1080, remoteBitrate: 0));
    }

    [Fact]
    public void BestVideoStream_NullStreams_ReturnsZero()
    {
        var (height, bitrate) = FederationQualityAdvisorService.BestVideoStream(null);

        Assert.Equal(0, height);
        Assert.Equal(0, bitrate);
    }

    [Fact]
    public void BestVideoStream_IgnoresNonVideoStreams()
    {
        var streams = new List<MediaStream>
        {
            new MediaStream { Type = MediaStreamType.Audio, BitRate = 5_000_000 },
            new MediaStream { Type = MediaStreamType.Subtitle }
        };

        var (height, bitrate) = FederationQualityAdvisorService.BestVideoStream(streams);

        Assert.Equal(0, height);
        Assert.Equal(0, bitrate);
    }

    [Fact]
    public void BestVideoStream_MultipleVideoStreams_PicksHighestResolution()
    {
        var streams = new List<MediaStream>
        {
            new MediaStream { Type = MediaStreamType.Video, Height = 1080, BitRate = 20_000_000 },
            new MediaStream { Type = MediaStreamType.Video, Height = 2160, BitRate = 15_000_000 },
            new MediaStream { Type = MediaStreamType.Audio, BitRate = 5_000_000 }
        };

        var (height, bitrate) = FederationQualityAdvisorService.BestVideoStream(streams);

        Assert.Equal(2160, height);
        Assert.Equal(15_000_000, bitrate);
    }

    [Fact]
    public void BestVideoStream_SameResolutionTiesOnBitrate()
    {
        var streams = new List<MediaStream>
        {
            new MediaStream { Type = MediaStreamType.Video, Height = 1080, BitRate = 5_000_000 },
            new MediaStream { Type = MediaStreamType.Video, Height = 1080, BitRate = 12_000_000 }
        };

        var (height, bitrate) = FederationQualityAdvisorService.BestVideoStream(streams);

        Assert.Equal(1080, height);
        Assert.Equal(12_000_000, bitrate);
    }
}
