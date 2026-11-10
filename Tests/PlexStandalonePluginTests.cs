using System;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.FederationPlex;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

/// <summary>
/// The standalone "Jellyfin Federation Plex" plugin loads the add-in into the core
/// plugin's load context. These pin when it may, what it says when it may not, and
/// that it never depends on the core at load time.
/// </summary>
public class PlexStandalonePluginTests
{
    private static readonly Version V207 = new(0, 0, 207, 0);

    [Fact]
    public void StartsOnlyBesideAMatchingCore()
    {
        var ok = PlexAddInHost.Decide(V207, false, V207);
        Assert.Equal(PlexHostState.Loaded, ok.State);
        Assert.Equal("0.0.207", ok.CoreVersion);
        Assert.Equal("0.0.207", ok.OwnVersion);

        // The fourth number is a build detail, not a different release.
        Assert.Equal(PlexHostState.Loaded, PlexAddInHost.Decide(new Version(0, 0, 207, 9), false, V207).State);
    }

    [Fact]
    public void MissingCoreIsExplainedNotFatal()
    {
        var status = PlexAddInHost.Decide(null, false, V207);
        Assert.Equal(PlexHostState.CoreMissing, status.State);
        Assert.Contains("not installed", status.Message, StringComparison.Ordinal);
        Assert.Null(status.CoreVersion);
    }

    [Theory]
    [InlineData(206)]
    [InlineData(208)]
    public void ADifferentCoreVersionIsRefusedWithBothVersionsNamed(int build)
    {
        var status = PlexAddInHost.Decide(new Version(0, 0, build, 0), false, V207);
        Assert.Equal(PlexHostState.VersionMismatch, status.State);
        Assert.Contains("0.0." + build, status.Message, StringComparison.Ordinal);
        Assert.Contains("0.0.207", status.Message, StringComparison.Ordinal);
        Assert.Contains("kept", status.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AHandCopiedAddInWinsAndThePluginStaysIdle()
    {
        // Two copies would register every Plex service twice.
        var status = PlexAddInHost.Decide(V207, true, V207);
        Assert.Equal(PlexHostState.ManualCopyActive, status.State);
        Assert.Contains("Jellyfin.Plugin.Federation.Plex.dll", status.Message, StringComparison.Ordinal);

        // Even at a mismatched version the advice is about the manual copy first.
        Assert.Equal(PlexHostState.ManualCopyActive, PlexAddInHost.Decide(new Version(0, 0, 206), true, V207).State);
    }

    [Fact]
    public void ThePluginAssemblyNeverReferencesTheCoreOrTheAddIn()
    {
        // Jellyfin verifies every type of a plugin inside its own isolated load
        // context before running any of its code; one such reference and the
        // plugin could not load from its own folder.
        var referenced = typeof(PlexAddInHost).Assembly.GetReferencedAssemblies().Select(a => a.Name).ToList();
        Assert.DoesNotContain(PlexAddInHost.CoreAssemblyName, referenced);
        Assert.DoesNotContain(PlexAddInHost.AddInAssemblyName, referenced);
    }

    [Fact]
    public void ThePackageCarriesTheAddInAtItsOwnVersion()
    {
        var host = typeof(PlexAddInHost).Assembly;
        using var stream = host.GetManifestResourceStream(PlexAddInHost.AddInResource);
        Assert.NotNull(stream);
        using var pe = new PEReader(stream!);
        var reader = pe.GetMetadataReader();
        var definition = reader.GetAssemblyDefinition();
        Assert.Equal(PlexAddInHost.AddInAssemblyName, reader.GetString(definition.Name));
        Assert.Equal(host.GetName().Version, definition.Version);
        Assert.Equal(typeof(Jellyfin.Plugin.Federation.Plugin).Assembly.GetName().Version, definition.Version);

        // The types the stand-ins forward to exist in the add-in under these names.
        var names = reader.TypeDefinitions.Select(h => reader.GetTypeDefinition(h))
            .Select(t => reader.GetString(t.Namespace) + "." + reader.GetString(t.Name)).ToHashSet(StringComparer.Ordinal);
        Assert.Contains(PlexImageProviderProxy.TargetType, names);
        Assert.Contains(PlexMetadataProviderProxy.TargetType, names);
        Assert.NotNull(host.GetManifestResourceStream("Jellyfin.Plugin.FederationPlex.Web.status.html"));
    }

    [Fact]
    public async Task WithoutARunningAddInTheProviderStandInsOfferNothing()
    {
        PlexAddInHost.ResetForTests();
        var services = new ServiceCollection().BuildServiceProvider();
        var images = new PlexImageProviderProxy(services);
        var movie = new Movie();
        Assert.False(images.Supports(movie));
        Assert.Empty(images.GetSupportedImages(movie));
        Assert.Empty(await images.GetImages(movie, CancellationToken.None));
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await images.GetImageResponse("plexmeta://x", CancellationToken.None)).StatusCode);
        Assert.Equal("Plex (not running)", images.Name);

        var metadata = new PlexMetadataProviderProxy(services);
        Assert.False((await metadata.GetMetadata(new MovieInfo(), CancellationToken.None)).HasMetadata);
        Assert.False((await metadata.GetMetadata(new EpisodeInfo(), CancellationToken.None)).HasMetadata);
        Assert.Empty(await metadata.GetSearchResults(new SeriesInfo(), CancellationToken.None));
        Assert.Equal("Plex (not running)", metadata.Name);
        Assert.Equal(PlexHostState.NotStarted, PlexAddInHost.Status.State);
    }

    [Fact]
    public void PluginIdentityMatchesItsMetadataFile()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JellyfinFederationPlugin.csproj")))
        {
            dir = dir.Parent;
        }

        var meta = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(dir!.FullName, "PlexPlugin", "meta.json"))).RootElement;
        var core = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(dir.FullName, "meta.json"))).RootElement;
        Assert.Equal("eae4fa49-1541-4813-af83-5802c7f6a674", meta.GetProperty("guid").GetString());
        Assert.NotEqual(core.GetProperty("guid").GetString(), meta.GetProperty("guid").GetString());
        Assert.Equal(core.GetProperty("version").GetString(), meta.GetProperty("version").GetString());
        Assert.Equal(core.GetProperty("targetAbi").GetString(), meta.GetProperty("targetAbi").GetString());
        Assert.Equal("Jellyfin Federation Plex", meta.GetProperty("name").GetString());
    }
}
