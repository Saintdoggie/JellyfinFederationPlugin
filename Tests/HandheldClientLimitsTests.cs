using System;
using Jellyfin.Plugin.Federation.Services;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

public class HandheldClientLimitsTests
{
    [Fact]
    public void HandheldRequestWithoutLinkCap_KeepsOriginalQualityLimits()
    {
        var requested = RemoteMediaPath.ParseQuery("?MediaSourceId=abc&VideoCodec=h264&VideoBitrate=80000000&AudioBitrate=256000&MaxStreamingBitrate=80000000&MaxWidth=3840&MaxHeight=2160");
        var remote = Guid.NewGuid();

        var query = RemoteMediaPath.FilterQuery(requested, (k, v) => FederationRemoteTranscodeFilter.RewriteQueryValue(k, v, remote, null, requested));

        Assert.Contains("MaxStreamingBitrate=80000000", query, StringComparison.Ordinal);
        Assert.Contains("VideoBitrate=80000000", query, StringComparison.Ordinal);
        Assert.Contains("MaxWidth=3840", query, StringComparison.Ordinal);
        Assert.Contains("MaxHeight=2160", query, StringComparison.Ordinal);
    }

    [Fact]
    public void HandheldRequestWithFastLink_IsNotHeldToThirteenMbpsOr1080p()
    {
        var requested = RemoteMediaPath.ParseQuery("?VideoBitrate=80000000&AudioBitrate=256000&MaxStreamingBitrate=80000000&MaxWidth=3840&MaxHeight=2160");
        var remote = Guid.NewGuid();

        var query = RemoteMediaPath.FilterQuery(requested, (k, v) => FederationRemoteTranscodeFilter.RewriteQueryValue(k, v, remote, 100, requested));

        Assert.Contains("MaxStreamingBitrate=80000000", query, StringComparison.Ordinal);
        Assert.Contains("VideoBitrate=80000000", query, StringComparison.Ordinal);
        Assert.Contains("MaxWidth=3840", query, StringComparison.Ordinal);
        Assert.Contains("MaxHeight=2160", query, StringComparison.Ordinal);
    }

    [Fact]
    public void HandheldRequestWithSlowLink_UsesMeasuredCapacity()
    {
        var requested = RemoteMediaPath.ParseQuery("?VideoBitrate=33477855&AudioBitrate=256000&MaxStreamingBitrate=33477855");
        var remote = Guid.NewGuid();

        var query = RemoteMediaPath.FilterQuery(requested, (k, v) => FederationRemoteTranscodeFilter.RewriteQueryValue(k, v, remote, 8, requested));

        Assert.Contains("MaxStreamingBitrate=7600000", query, StringComparison.Ordinal);
        Assert.Contains("VideoBitrate=7344000", query, StringComparison.Ordinal);
        Assert.DoesNotContain("MaxWidth", query, StringComparison.Ordinal);
        Assert.DoesNotContain("MaxHeight", query, StringComparison.Ordinal);
    }

    [Fact]
    public void AppsOwnResolutionLimit_IsAlwaysKept()
    {
        var requested = RemoteMediaPath.ParseQuery("?MaxHeight=720&MaxWidth=1280");
        var remote = Guid.NewGuid();

        var query = RemoteMediaPath.FilterQuery(requested, (k, v) => FederationRemoteTranscodeFilter.RewriteQueryValue(k, v, remote, 100, requested));

        Assert.Contains("MaxHeight=720", query, StringComparison.Ordinal);
        Assert.Contains("MaxWidth=1280", query, StringComparison.Ordinal);
    }
}
