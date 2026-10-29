using System;
using Jellyfin.Plugin.Federation.Services;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

/// <summary>
/// Phone and tablet apps send no resolution limit and a huge bitrate limit; a
/// handed-off conversion must still be one the friend can make in real time.
/// </summary>
public class HandheldClientLimitsTests
{
    [Theory]
    [InlineData("Jellyfin for Android", "Galaxy S24", true)]
    [InlineData("Jellyfin Android", "Pixel 9", true)]
    [InlineData("Litefin", "Pixel 9", true)]
    [InlineData("Findroid", "Galaxy S24", true)]
    [InlineData("Swiftfin", "iPhone", true)]
    [InlineData("Swiftfin", "iPad", true)]
    [InlineData("Streamyfin", "iPhone", true)]
    [InlineData("Swiftfin tvOS", "AppleTV", false)]
    [InlineData("Swiftfin", "Apple TV", false)]
    [InlineData("Jellyfin Android TV", "Shield", false)]
    [InlineData("Jellyfin for Android", "Chromecast with Google TV", false)]
    [InlineData("Jellyfin Web", "Chrome", false)]
    [InlineData("Jellyfin Roku", "Ultra", false)]
    [InlineData(null, "Galaxy S24", false)]
    [InlineData("", "", false)]
    public void IsHandheld_RecognisesPhoneAndTabletAppsOnly(string? client, string? device, bool expected)
        => Assert.Equal(expected, HandheldClientLimits.IsHandheld(client, device));

    [Fact]
    public void Cap_TakesTheLowerOfTheLinkCapAndTheHandheldCeiling()
    {
        Assert.Equal(35, HandheldClientLimits.Cap(35, false));
        Assert.Null(HandheldClientLimits.Cap(null, false));
        Assert.Equal(HandheldClientLimits.CapMbps, HandheldClientLimits.Cap(35, true));
        Assert.Equal(HandheldClientLimits.CapMbps, HandheldClientLimits.Cap(null, true));
        Assert.Equal(HandheldClientLimits.CapMbps, HandheldClientLimits.Cap(0, true));
        Assert.Equal(8, HandheldClientLimits.Cap(8, true));
    }

    [Fact]
    public void FourKRequestFromAnAndroidPhone_IsHeldToTwelveMbpsAnd1080p()
    {
        var requested = RemoteMediaPath.ParseQuery("?MediaSourceId=abc&VideoCodec=h264&VideoBitrate=33477855&AudioBitrate=256000&MaxStreamingBitrate=33477855");
        var remote = Guid.NewGuid();
        var cap = HandheldClientLimits.Cap(35, true);

        var query = RemoteMediaPath.FilterQuery(requested, (k, v) => FederationRemoteTranscodeFilter.RewriteQueryValue(k, v, remote, cap, requested));
        query = HandheldClientLimits.WithResolution(query, requested, true);

        Assert.Contains("MaxStreamingBitrate=12350000", query, StringComparison.Ordinal);
        Assert.Contains("VideoBitrate=12094000", query, StringComparison.Ordinal);
        Assert.Contains("MaxWidth=1920&MaxHeight=1080", query, StringComparison.Ordinal);
    }

    [Fact]
    public void AppsOwnResolutionLimit_IsKept()
    {
        var requested = RemoteMediaPath.ParseQuery("?MaxHeight=720");
        var query = RemoteMediaPath.FilterQuery(requested);

        Assert.Equal(query, HandheldClientLimits.WithResolution(query, requested, true));
    }

    [Fact]
    public void NonHandheldClients_AreUntouched()
    {
        var requested = RemoteMediaPath.ParseQuery("?VideoBitrate=33000000");
        var query = RemoteMediaPath.FilterQuery(requested);

        Assert.Equal(query, HandheldClientLimits.WithResolution(query, requested, false));
        Assert.Equal(query + "&MaxWidth=1920&MaxHeight=1080", HandheldClientLimits.WithResolution(query, requested, true));
        Assert.Equal("?MaxWidth=1920&MaxHeight=1080", HandheldClientLimits.WithResolution(string.Empty, Array.Empty<System.Collections.Generic.KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues>>(), true));
    }
}
