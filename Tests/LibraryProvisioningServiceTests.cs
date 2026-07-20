using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;
using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

public class LibraryProvisioningServiceTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _federationRoot;
    private readonly Mock<ILibraryManager> _lm;
    private readonly LibraryProvisioningService _service;
    private readonly PluginInstanceRestorer _pluginRestorer;

    public LibraryProvisioningServiceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "federation-tests-" + Guid.NewGuid().ToString("N"));
        _federationRoot = Path.Combine(_tempRoot, "federation");
        Directory.CreateDirectory(_federationRoot);

        _pluginRestorer = new PluginInstanceRestorer(_tempRoot);

        _lm = new Mock<ILibraryManager>();
        var logger = new Mock<ILogger<LibraryProvisioningService>>();
        _service = new LibraryProvisioningService(_lm.Object, logger.Object);
    }

    public void Dispose()
    {
        _pluginRestorer.Dispose();
        try { Directory.Delete(_tempRoot, true); } catch { }
    }

    [Fact]
    public void SafeFolderName_RejectsEmpty()
    {
        Assert.Throws<ArgumentException>(() => LibraryProvisioningService.SafeFolderName(""));
        Assert.Throws<ArgumentException>(() => LibraryProvisioningService.SafeFolderName("   "));
        Assert.Throws<ArgumentException>(() => LibraryProvisioningService.SafeFolderName(null!));
    }

    [Theory]
    [InlineData("Movies", "Movies")]
    [InlineData("Kids TV", "Kids TV")]
    [InlineData("4K (HDR)", "4K (HDR)")]
    public void SafeFolderName_PreservesValidNames(string input, string expected)
        => Assert.Equal(expected, LibraryProvisioningService.SafeFolderName(input));

    [Fact]
    public void SafeFolderName_ReplacesSeparatorsAndColons()
    {
        var safe = LibraryProvisioningService.SafeFolderName("Bad/Name:2");
        Assert.DoesNotContain("/", safe);
        Assert.DoesNotContain(":", safe);
        Assert.Contains("Bad", safe);
        Assert.Contains("Name", safe);
    }

    [Fact]
    public void IsFederationFolder_ReturnsFalse_WhenLocationsNull()
    {
        var vf = new VirtualFolderInfo { Name = "X", Locations = null };
        Assert.False(LibraryProvisioningService.IsFederationFolder(vf, _federationRoot));
    }

    [Fact]
    public void IsFederationFolder_ReturnsTrue_ForLocationUnderFederationRoot()
    {
        var vf = new VirtualFolderInfo
        {
            Name = "Anime",
            Locations = new[] { Path.Combine(_federationRoot, "Anime") }
        };
        Assert.True(LibraryProvisioningService.IsFederationFolder(vf, _federationRoot));
    }

    [Fact]
    public void IsFederationFolder_ReturnsTrue_ForLegacyFederationUriLocation()
    {
        var vf = new VirtualFolderInfo
        {
            Name = "Anime",
            Locations = new[] { "federation://Anime" }
        };
        Assert.True(LibraryProvisioningService.IsFederationFolder(vf, _federationRoot));
    }

    [Fact]
    public void IsFederationFolder_ReturnsFalse_ForUserLibraryWithUnrelatedPaths()
    {
        var vf = new VirtualFolderInfo
        {
            Name = "Movies",
            Locations = new[] { "/media/movies", "/mnt/nas/movies" }
        };
        Assert.False(LibraryProvisioningService.IsFederationFolder(vf, _federationRoot));
    }

    [Fact]
    public void IsFederationFolder_ReturnsFalse_ForSiblingWithSimilarPrefix()
    {
        // /tmp/.../federation2 should NOT match root /tmp/.../federation
        var sibling = _federationRoot + "2";
        Directory.CreateDirectory(sibling);
        var vf = new VirtualFolderInfo
        {
            Name = "Other",
            Locations = new[] { sibling }
        };
        Assert.False(LibraryProvisioningService.IsFederationFolder(vf, _federationRoot));
    }

    [Fact]
    public void IsUnderFederationRoot_HandlesTrailingSeparators()
    {
        var path = Path.Combine(_federationRoot, "Anime") + Path.DirectorySeparatorChar;
        Assert.True(LibraryProvisioningService.IsUnderFederationRoot(path, _federationRoot));
        Assert.True(LibraryProvisioningService.IsUnderFederationRoot(_federationRoot, _federationRoot));
    }

    [Fact]
    public async Task EnsureLibraryAsync_CreatesNewLibrary_WithRealShadowPath()
    {
        _lm.Setup(x => x.GetVirtualFolders()).Returns(new List<VirtualFolderInfo>());
        _lm.Setup(x => x.AddVirtualFolder(It.IsAny<string>(), It.IsAny<CollectionTypeOptions?>(), It.IsAny<LibraryOptions>(), It.IsAny<bool>()))
            .Returns(Task.CompletedTask)
            .Verifiable();

        var mapping = new LibraryMapping { LocalLibraryName = "Anime", MediaType = "Series", Enabled = true, AutoProvision = true };

        await InvokeEnsureLibraryAsync(mapping);

        _lm.Verify();
        _lm.Verify(
            x => x.AddVirtualFolder(
                "Anime",
                It.IsAny<CollectionTypeOptions?>(),
                It.Is<LibraryOptions>(o => o.PathInfos != null && o.PathInfos.Length == 1 && o.PathInfos[0].Path != null && o.PathInfos[0].Path.StartsWith(_federationRoot, StringComparison.OrdinalIgnoreCase) && !o.PathInfos[0].Path.StartsWith("federation://", StringComparison.OrdinalIgnoreCase)),
                true),
            Times.Once);

        var shadowDir = Path.Combine(_federationRoot, "Anime");
        Assert.True(Directory.Exists(shadowDir));
    }

    [Fact]
    public async Task EnsureLibraryAsync_MergesIntoExistingLibrary_WhenNameCollides()
    {
        var existing = new VirtualFolderInfo
        {
            Name = "Movies",
            Locations = new[] { "/media/movies" }
        };
        _lm.Setup(x => x.GetVirtualFolders()).Returns(new List<VirtualFolderInfo> { existing });

        var mapping = new LibraryMapping { LocalLibraryName = "Movies", MediaType = "Movie", Enabled = true, AutoProvision = true };

        await InvokeEnsureLibraryAsync(mapping);

        _lm.Verify(x => x.AddVirtualFolder(It.IsAny<string>(), It.IsAny<CollectionTypeOptions?>(), It.IsAny<LibraryOptions>(), It.IsAny<bool>()), Times.Never);
        _lm.Verify(
            x => x.AddMediaPath(
                "Movies",
                It.Is<MediaPathInfo>(p => p.Path != null && p.Path.StartsWith(_federationRoot, StringComparison.OrdinalIgnoreCase))),
            Times.Once);
    }

    [Fact]
    public async Task EnsureLibraryAsync_IsIdempotent_WhenAlreadyMerged()
    {
        var shadowPath = Path.Combine(_federationRoot, "Movies");
        Directory.CreateDirectory(shadowPath);
        var existing = new VirtualFolderInfo
        {
            Name = "Movies",
            Locations = new[] { "/media/movies", shadowPath }
        };
        _lm.Setup(x => x.GetVirtualFolders()).Returns(new List<VirtualFolderInfo> { existing });

        var mapping = new LibraryMapping { LocalLibraryName = "Movies", MediaType = "Movie", Enabled = true, AutoProvision = true };

        await InvokeEnsureLibraryAsync(mapping);

        _lm.Verify(x => x.AddVirtualFolder(It.IsAny<string>(), It.IsAny<CollectionTypeOptions?>(), It.IsAny<LibraryOptions>(), It.IsAny<bool>()), Times.Never);
        _lm.Verify(x => x.AddMediaPath(It.IsAny<string>(), It.IsAny<MediaPathInfo>()), Times.Never);
    }

    [Fact]
    public async Task EnsureLibraryAsync_IsIdempotent_WhenStandaloneFederationLibrary()
    {
        var shadowPath = Path.Combine(_federationRoot, "Anime");
        Directory.CreateDirectory(shadowPath);
        var existing = new VirtualFolderInfo
        {
            Name = "Anime",
            Locations = new[] { shadowPath }
        };
        _lm.Setup(x => x.GetVirtualFolders()).Returns(new List<VirtualFolderInfo> { existing });

        var mapping = new LibraryMapping { LocalLibraryName = "Anime", MediaType = "Series", Enabled = true, AutoProvision = true };

        await InvokeEnsureLibraryAsync(mapping);

        _lm.Verify(x => x.AddVirtualFolder(It.IsAny<string>(), It.IsAny<CollectionTypeOptions?>(), It.IsAny<LibraryOptions>(), It.IsAny<bool>()), Times.Never);
        _lm.Verify(x => x.AddMediaPath(It.IsAny<string>(), It.IsAny<MediaPathInfo>()), Times.Never);
    }

    [Fact]
    public async Task EnsureLibraryAsync_MigratesLegacyFederationUriLocation()
    {
        var existing = new VirtualFolderInfo
        {
            Name = "Anime",
            Locations = new[] { "federation://Anime" }
        };
        _lm.Setup(x => x.GetVirtualFolders()).Returns(new List<VirtualFolderInfo> { existing });

        var mapping = new LibraryMapping { LocalLibraryName = "Anime", MediaType = "Series", Enabled = true, AutoProvision = true };

        await InvokeEnsureLibraryAsync(mapping);

        // Should add the real shadow path
        _lm.Verify(
            x => x.AddMediaPath(
                "Anime",
                It.Is<MediaPathInfo>(p => p.Path != null && p.Path.StartsWith(_federationRoot, StringComparison.OrdinalIgnoreCase))),
            Times.Once);
        // Should remove the legacy federation:// location
        _lm.Verify(x => x.RemoveMediaPath("Anime", "federation://Anime"), Times.Once);
        // Should not create a new library
        _lm.Verify(x => x.AddVirtualFolder(It.IsAny<string>(), It.IsAny<CollectionTypeOptions?>(), It.IsAny<LibraryOptions>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task RemoveLibraryAsync_RemovesOnlyMediaPath_WhenMergedIntoUserLibrary()
    {
        var shadowPath = Path.Combine(_federationRoot, "Movies");
        Directory.CreateDirectory(shadowPath);
        var existing = new VirtualFolderInfo
        {
            Name = "Movies",
            Locations = new[] { "/media/movies", shadowPath }
        };
        _lm.Setup(x => x.GetVirtualFolders()).Returns(new List<VirtualFolderInfo> { existing });

        await InvokeRemoveLibraryAsync("Movies");

        _lm.Verify(x => x.RemoveMediaPath("Movies", shadowPath), Times.Once);
        _lm.Verify(x => x.RemoveMediaPath("Movies", "/media/movies"), Times.Never);
        _lm.Verify(x => x.RemoveVirtualFolder(It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task RemoveLibraryAsync_RemovesVirtualFolder_WhenFullyPluginOwned()
    {
        var shadowPath = Path.Combine(_federationRoot, "Anime");
        Directory.CreateDirectory(shadowPath);
        var existing = new VirtualFolderInfo
        {
            Name = "Anime",
            Locations = new[] { shadowPath }
        };
        _lm.Setup(x => x.GetVirtualFolders()).Returns(new List<VirtualFolderInfo> { existing });
        _lm.Setup(x => x.RemoveVirtualFolder(It.IsAny<string>(), It.IsAny<bool>())).Returns(Task.CompletedTask);

        await InvokeRemoveLibraryAsync("Anime");

        _lm.Verify(x => x.RemoveMediaPath(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        _lm.Verify(x => x.RemoveVirtualFolder("Anime", true), Times.Once);
    }

    [Fact]
    public async Task RemoveLibraryAsync_DoesNothing_WhenNotFederationManaged()
    {
        var existing = new VirtualFolderInfo
        {
            Name = "Movies",
            Locations = new[] { "/media/movies" }
        };
        _lm.Setup(x => x.GetVirtualFolders()).Returns(new List<VirtualFolderInfo> { existing });

        await InvokeRemoveLibraryAsync("Movies");

        _lm.Verify(x => x.RemoveMediaPath(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        _lm.Verify(x => x.RemoveVirtualFolder(It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
    }

    private async Task InvokeEnsureLibraryAsync(LibraryMapping mapping)
    {
        var method = typeof(LibraryProvisioningService).GetMethod("EnsureLibraryAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);
        await (Task)method!.Invoke(_service, new object[] { mapping, CancellationToken.None })!;
    }

    private async Task InvokeRemoveLibraryAsync(string name)
    {
        var method = typeof(LibraryProvisioningService).GetMethod("RemoveLibraryAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);
        await (Task)method!.Invoke(_service, new object[] { name })!;
    }
}

/// <summary>
/// Sets Plugin.Instance to a fake whose DataFolderPath points at a temp dir for the
/// duration of a test, then restores the original on dispose.
/// </summary>
internal sealed class PluginInstanceRestorer : IDisposable
{
    private readonly Plugin? _original;
    private readonly Plugin? _fake;
    private static readonly System.Reflection.FieldInfo InstanceField =
        typeof(Plugin).GetField("<Instance>k__BackingField", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
        ?? throw new InvalidOperationException("Could not locate Plugin.Instance backing field.");

    public PluginInstanceRestorer(string dataPath)
    {
        _original = Plugin.Instance;

        // Construct a Plugin via reflection: the real ctor needs IApplicationPaths,
        // IXmlSerializer, ILogger. Instead, allocate an uninitialized instance and
        // set DataFolderPath via reflection so GetFederationRoot() picks it up.
#pragma warning disable SYSLIB0050
        _fake = (Plugin)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(Plugin));
#pragma warning restore SYSLIB0050
        var dataPathField = typeof(MediaBrowser.Common.Plugins.BasePlugin).GetField("<DataFolderPath>k__BackingField", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?? throw new InvalidOperationException("Could not locate BasePlugin.DataFolderPath backing field.");
        dataPathField.SetValue(_fake, dataPath);

        InstanceField.SetValue(null, _fake);
    }

    public void Dispose()
    {
        InstanceField.SetValue(null, _original);
    }
}
