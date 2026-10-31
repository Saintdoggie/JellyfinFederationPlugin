using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Jellyfin.Plugin.Federation.Configuration;
using Jellyfin.Plugin.Federation.Services;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

[Collection("PluginInstance")]
public sealed class FriendPrivacyDefaultsTests : IDisposable
{
    private readonly RealPluginInstance _plugin = new();

    public void Dispose() => _plugin.Dispose();

    [Fact]
    public void NewInstall_SharesNeitherPopularTitlesNorViewerNames()
    {
        var config = new PluginConfiguration();
        Assert.False(config.SharePopularWithFriends);
        Assert.False(config.ShareViewerNamesWithFriends);
        Assert.True(config.ShareViewersWithFriends);

        var friend = JsonSerializer.Deserialize<RemoteServer>("{\"Name\":\"Old friend\"}")!;
        Assert.Null(friend.SharePopular);
        Assert.Null(friend.ShareViewerNames);
    }

    [Theory]
    [InlineData(false, null, false)]
    [InlineData(true, null, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    public void PerFriendChoice_OverridesTheDefault(bool byDefault, bool? perFriend, bool expected)
    {
        _plugin.Configuration.SharePopularWithFriends = byDefault;
        _plugin.Configuration.ShareViewerNamesWithFriends = byDefault;
        var friend = new RemoteServer { SharePopular = perFriend, ShareViewerNames = perFriend };

        Assert.Equal(expected, FederationFriendPopularService.SharesPopularWith(friend));
        Assert.Equal(expected, FederationViewerReporter.SharesNamesWith(friend));
    }

    [Fact]
    public void Viewer_WithoutNames_IsShownAsSomeone()
    {
        Assert.Equal("Someone (friendflix)", FederationRemoteViewerService.Label(null, "friendflix"));
        Assert.Equal("Device · via friendflix", FederationRemoteViewerService.DeviceLabel(null, "friendflix"));
    }

    [Fact]
    public void ConversionLog_HoldsOnlyThisSessionsConversion_WithFoldersBlanked()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fed-logs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var item = Guid.NewGuid();
            var started = DateTime.UtcNow.AddMinutes(-5);
            File.WriteAllText(Path.Combine(dir, $"FFmpeg.Transcode-2030-01-01_10-00-00_{item:N}_aaaa1111.log"),
                "{\"Path\":\"D:\\\\Media\\\\Films\\\\Alpha.mkv\"}\n\nffmpeg -i file:\"/srv/media/films/Alpha.mkv\" \"/var/cache/jellyfin/transcodes/0123456789abcdef.m3u8\"\nframe=1\n");
            File.WriteAllText(Path.Combine(dir, $"FFmpeg.Transcode-2030-01-01_10-00-05_{item:N}_bbbb2222.log"),
                "ffmpeg -i file:\"/srv/media/films/Alpha.mkv\" \"/var/cache/jellyfin/transcodes/fedcba9876543210.m3u8\"\nsomeone else\n");

            var mine = FriendConversionLogs.Collect(dir, item, started,
                new[] { "/var/cache/jellyfin/transcodes/0123456789abcdef.m3u8" },
                new[] { "/srv/media/films", "D:\\Media\\Films", "/var/cache/jellyfin/transcodes" });

            Assert.NotNull(mine);
            Assert.Contains("aaaa1111", mine);
            Assert.DoesNotContain("bbbb2222", mine);
            Assert.DoesNotContain("someone else", mine);
            Assert.DoesNotContain("/srv/media", mine);
            Assert.DoesNotContain("Media\\\\Films", mine);
            Assert.Contains("[folder]/Alpha.mkv", mine);
            Assert.Contains("[folder]/0123456789abcdef.m3u8", mine);

            // A session whose conversion isn't known yet gets nothing rather than a guess.
            Assert.Null(FriendConversionLogs.Collect(dir, item, started, Array.Empty<string>(), null));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Registry_RemembersConversionsOnlyForKnownSessions()
    {
        var registry = new RemoteTranscodeSessionRegistry();
        registry.RememberConversion("friend", "unknown", "/t/a.m3u8");
        Assert.False(registry.TryGetForLog("friend", "unknown", out _, out _, out _));

        var item = Guid.NewGuid();
        Assert.True(registry.TryTouch("friend", "Play1", "dev", 0, item));
        registry.RememberConversion("friend", "play1", "/t/a.m3u8");
        registry.RememberConversion("friend", "PLAY1", "/t/a.m3u8");
        registry.RememberConversion("friend", "play1", null);
        registry.RememberConversion("other", "play1", "/t/b.m3u8");

        Assert.True(registry.TryGetForLog("friend", "play1", out var got, out _, out var conversions));
        Assert.Equal(item, got);
        Assert.Equal(new[] { "/t/a.m3u8" }, conversions);
    }

    [Fact]
    public void MainSave_PreservesThePrivacyChoices_AndSpeedTestsCount()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "Configuration", "FederationPluginController.cs")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        Assert.NotNull(dir);
        var src = File.ReadAllText(Path.Combine(dir!, "Configuration", "FederationPluginController.cs"));
        Assert.Contains("config.ShareViewerNamesWithFriends = existing.ShareViewerNamesWithFriends;", src);
        Assert.Contains("server.SharePopular = oldServer.SharePopular;", src);
        Assert.Contains("server.ShareViewerNames = oldServer.ShareViewerNames;", src);
        var shown = FederationServerPresentation.Sanitize(new RemoteServer { SharePopular = true, ShareViewerNames = false }, null);
        Assert.Equal(true, shown.GetType().GetProperty("SharePopular")!.GetValue(shown));
        Assert.Equal(false, shown.GetType().GetProperty("ShareViewerNames")!.GetValue(shown));
        var speedTest = src.Substring(src.IndexOf("public async Task<IActionResult> PeerBitrateTest", StringComparison.Ordinal), 1500);
        Assert.Contains("IsOverCap(caller.FederationId", speedTest);
        Assert.Contains("RelayWithDataLimitAsync(caller", speedTest);
    }
}
