using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

public class NativeClientCapabilitiesTests
{
    [Fact]
    public void Identify_UsesSessionThenStandardHeadersWithoutReturningToken()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "MediaBrowser Client=\"Findroid\", Device=\"Pixel 9\", Token=\"secret\"";
        context.Request.Headers["X-Emby-Client"] = "Ignored when session exists";
        var info = new AuthorizationInfo { Client = "Jellyfin for Android", Device = "Galaxy S24" };
        Assert.Equal(("Jellyfin for Android", "Galaxy S24"), NativeClientCapabilities.Identify(context.Request, info));

        context.Request.Headers.Remove("X-Emby-Client");
        var headerOnly = NativeClientCapabilities.Identify(context.Request, null);
        Assert.Equal(("Findroid", "Pixel 9"), headerOnly);
        Assert.DoesNotContain("secret", headerOnly.Client + headerOnly.Device);
    }

    [Theory]
    [InlineData("Jellyfin for Android", false)]
    [InlineData("Jellyfin Android TV", false)]
    [InlineData("Android", false)]
    [InlineData("Jellyfin Web", false)]
    [InlineData("Firefox", false)]
    [InlineData("Findroid", true)]
    [InlineData("Swiftfin", true)]
    [InlineData("Jellyfin Roku", true)]
    [InlineData("Kodi", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void RawHttpDirectPlay_UsesKnownTransportBehavior(string? client, bool expected)
    {
        Assert.Equal(expected, NativeClientCapabilities.SupportsRawHttpDirectPlay(new DefaultHttpContext().Request, client, "device"));
    }

    [Fact]
    public void BrowserUserAgentCannotUseRawDirectPlayEvenWithNativeClientName()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.UserAgent = "Mozilla/5.0";
        Assert.False(NativeClientCapabilities.SupportsRawHttpDirectPlay(context.Request, "Swiftfin", "iPhone"));
    }
}
