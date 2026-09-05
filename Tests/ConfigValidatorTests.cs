using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Federation.Configuration;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

public class ConfigValidatorTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a/b")]
    [InlineData("a:b")]
    [InlineData("/")]
    [InlineData(":")]
    [InlineData("Movies/2024")]
    [InlineData("Movies: Directors Cut")]
    public void InvalidMappingNames_Rejected(string? name)
        => Assert.False(ConfigValidator.IsValidMappingName(name));

    [Theory]
    [InlineData("Movies")]
    [InlineData("Kids TV")]
    [InlineData("4K (HDR)")]
    [InlineData("Tom's Stuff")]
    public void ValidMappingNames_Accepted(string name)
        => Assert.True(ConfigValidator.IsValidMappingName(name));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("notaurl")]
    [InlineData("ftp://server")]
    [InlineData("//server/path")]
    [InlineData("server:8096")]
    [InlineData("/relative/path")]
    [InlineData("https://user:password@server.example")]
    [InlineData("https://server.example?token=secret")]
    [InlineData("https://server.example/#token=secret")]
    public void InvalidServerUrls_Rejected(string? url)
        => Assert.False(ConfigValidator.IsValidServerUrl(url));

    [Theory]
    [InlineData("http://server:8096")]
    [InlineData("https://example.com/")]
    [InlineData("http://192.168.1.10:8096")]
    [InlineData("https://example.com/jellyfin")]
    public void ValidServerUrls_Accepted(string url)
        => Assert.True(ConfigValidator.IsValidServerUrl(url));

    [Fact]
    public void Validate_RejectsDuplicateMappingNames_CaseInsensitive()
    {
        var config = new PluginConfiguration
        {
            LibraryMappings = new List<LibraryMapping>
            {
                new() { LocalLibraryName = "Movies" },
                new() { LocalLibraryName = "movies" }
            }
        };

        var errors = ConfigValidator.Validate(config);

        Assert.Contains(errors, e => e.Contains("Duplicate", System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_RejectsRefreshIntervalBelowOne()
    {
        var config = new PluginConfiguration { RefreshIntervalHours = 0 };

        var errors = ConfigValidator.Validate(config);

        Assert.Contains(errors, e => e.Contains("RefreshIntervalHours", System.StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RejectsInvalidMappingNameAndServerUrl()
    {
        var config = new PluginConfiguration
        {
            RemoteServers = new List<RemoteServer>
            {
                new() { Name = "Bad", Url = "notaurl" }
            },
            LibraryMappings = new List<LibraryMapping>
            {
                new() { LocalLibraryName = "Bad/Name" }
            }
        };

        var errors = ConfigValidator.Validate(config);

        Assert.Contains(errors, e => e.Contains("invalid URL", System.StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, e => e.Contains("invalid library name", System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_RejectsInvalidLocalServerUrl()
    {
        var config = new PluginConfiguration { ServerUrl = "notaurl" };

        var errors = ConfigValidator.Validate(config);

        Assert.Contains(errors, e => e.Contains("ServerUrl", System.StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("http://127.0.0.1:8096")]
    [InlineData("http://10.0.0.5:8096")]
    [InlineData("http://192.168.1.10:8096")]
    [InlineData("http://172.16.0.2:8096")]
    [InlineData("http://169.254.1.1:8096")]
    [InlineData("http://100.64.1.20:8096")]
    [InlineData("http://100.127.0.1:8096")]
    public void PrivateOrLoopbackAndCgnatHosts_AreFlagged(string url)
        => Assert.True(ConfigValidator.IsPrivateOrLoopbackHost(url));

    [Theory]
    [InlineData("https://friend.example.com")]
    [InlineData("https://name.tail12345.ts.net")]
    [InlineData("https://1-2-3-4.hash.plex.direct:32400")]
    [InlineData("http://8.8.8.8:8096")]
    public void PublicHosts_AreNotFlaggedAsPrivate(string url)
        => Assert.False(ConfigValidator.IsPrivateOrLoopbackHost(url));

    [Fact]
    public void Validate_AcceptsValidConfiguration()
    {
        var config = new PluginConfiguration
        {
            ServerUrl = "http://localhost:8096",
            RefreshIntervalHours = 6,
            RemoteServers = new List<RemoteServer>
            {
                new() { Name = "Friend", Url = "https://friend.example.com" }
            },
            LibraryMappings = new List<LibraryMapping>
            {
                new() { LocalLibraryName = "Shared Movies" },
                new() { LocalLibraryName = "Shared Shows" }
            }
        };

        var errors = ConfigValidator.Validate(config);

        Assert.Empty(errors);
    }
}
