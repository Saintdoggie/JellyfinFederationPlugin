using System;
using System.Linq;
using Jellyfin.Plugin.Federation.Services;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

public class IntegrationCatalogTests
{
    private const string Catalog = """
        {
          "schemaVersion": 1,
          "integrations": [{
            "id": "org.jellyfin.federation.plex",
            "type": "addin",
            "name": "Plex",
            "summary": "Plex catalog support",
            "publisher": "Federation maintainers",
            "builtinProviderId": "Plex",
            "installGuideUrl": "https://github.com/Saintdoggie/JellyfinFederationPlugin/blob/master/Plex/INSTALL.md",
            "minHostVersion": "0.0.201",
            "maxHostVersion": "0.0.207",
            "jellyfinAbi": "12.0.0.0",
            "platforms": ["linux-x64", "win-x64"],
            "capabilities": ["catalog.read", "network.outbound"]
          }]
        }
        """;

    [Fact]
    public void CompatibleEntry_AppearsWithItsInstallState()
    {
        var entries = IntegrationCatalogService.ParseCompatible(Catalog, new Version("0.0.207.0"), new Version("12.2.0.0"), "linux-x64", true);
        var entry = Assert.Single(entries);
        Assert.Equal("org.jellyfin.federation.plex", entry.Id);
        Assert.True(entry.Installed);
        Assert.DoesNotContain(entry.Capabilities, c => c.Contains("script", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("0.0.206", "12.2.0.0", "linux-arm64")]
    [InlineData("0.0.208", "12.2.0.0", "linux-x64")]
    [InlineData("0.0.207", "10.11.0.0", "linux-x64")]
    public void IncompatibleEntry_IsHidden(string host, string jellyfin, string platform)
    {
        Assert.Empty(IntegrationCatalogService.ParseCompatible(Catalog, new Version(host), new Version(jellyfin), platform, false));
    }

    [Fact]
    public void UnsafeOrUnknownEntries_AreRejected()
    {
        var unsafeCatalog = Catalog.Replace("Plex catalog support", "<script>alert(1)</script>", StringComparison.Ordinal);
        Assert.Empty(IntegrationCatalogService.ParseCompatible(unsafeCatalog, new Version("0.0.207"), new Version("12.2.0.0"), "linux-x64", false));

        var wrongProvider = Catalog.Replace("\"builtinProviderId\": \"Plex\"", "\"builtinProviderId\": \"Unknown\"", StringComparison.Ordinal);
        Assert.Empty(IntegrationCatalogService.ParseCompatible(wrongProvider, new Version("0.0.207"), new Version("12.2.0.0"), "linux-x64", false));
    }

    [Fact]
    public void DeclarativeEntry_CannotHaveAnInstallGuide()
    {
        var declarative = Catalog
            .Replace("\"type\": \"addin\"", "\"type\": \"declarative\"", StringComparison.Ordinal)
            .Replace("\"builtinProviderId\": \"Plex\"", "\"builtinProviderId\": \"Companion\"", StringComparison.Ordinal);
        Assert.Empty(IntegrationCatalogService.ParseCompatible(declarative, new Version("0.0.207"), new Version("12.2.0.0"), "linux-x64", false));
    }
}
