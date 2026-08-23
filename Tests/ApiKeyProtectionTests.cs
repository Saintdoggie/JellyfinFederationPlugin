using System;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.Federation.Configuration;
using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

/// <summary>
/// Covers encryption-at-rest for the federation API keys/tokens this plugin
/// stores: <see cref="ApiKeyProtector"/> directly, and the two points in
/// <see cref="Plugin"/> that wire it in - decrypting a just-loaded config in
/// place, and encrypting a deep clone (never the live config) before it's
/// written to disk.
/// </summary>
[Collection("PluginInstance")]
public class ApiKeyProtectionTests : IDisposable
{
    private readonly Plugin? _original;

    public ApiKeyProtectionTests()
    {
        _original = Plugin.Instance;

        // ApiKeyProtector's key ring is a static, process-lifetime singleton (see
        // its own doc comment) - some other test in this collection has almost
        // certainly already initialized it by the time this runs, and Initialize
        // is a documented no-op after the first call. Calling it again here with
        // a fresh directory is just belt-and-suspenders for a test running in
        // isolation.
        ApiKeyProtector.Initialize(Path.Combine(Path.GetTempPath(), "federation-tests-keys-" + Guid.NewGuid().ToString("N")));
    }

    public void Dispose()
    {
        var field = typeof(Plugin).GetField("<Instance>k__BackingField", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?? throw new InvalidOperationException("Could not locate Plugin.Instance backing field.");
        field.SetValue(null, _original);
    }

    [Fact]
    public void Protect_ThenUnprotect_RoundTripsToTheOriginalPlaintext()
    {
        var ciphertext = ApiKeyProtector.Protect("a-real-federation-token");

        Assert.NotEqual("a-real-federation-token", ciphertext);
        Assert.Equal("a-real-federation-token", ApiKeyProtector.Unprotect(ciphertext));
    }

    [Fact]
    public void Protect_EmptyOrNull_ReturnsEmptyWithoutEncrypting()
    {
        Assert.Equal(string.Empty, ApiKeyProtector.Protect(string.Empty));
        Assert.Equal(string.Empty, ApiKeyProtector.Protect(null));
    }

    [Fact]
    public void Unprotect_ValueThatIsNotCiphertext_IsReturnedUnchanged()
    {
        // The exact scenario an upgrading install hits: a key saved before this
        // feature existed is plain text, not a DataProtection payload - it must
        // keep working rather than the plugin treating it as corrupt.
        Assert.Equal("plain-old-key-from-before-encryption-existed", ApiKeyProtector.Unprotect("plain-old-key-from-before-encryption-existed"));
    }

    [Fact]
    public void SaveConfiguration_EncryptsKeysOnDiskWithoutMutatingTheLiveConfiguration()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "federation-tests-" + Guid.NewGuid().ToString("N"));
        var xml = new Mock<IXmlSerializer>();
        xml.Setup(x => x.DeserializeFromFile(It.IsAny<Type>(), It.IsAny<string>())).Throws(new FileNotFoundException());

        PluginConfiguration? savedToDisk = null;
        xml.Setup(x => x.SerializeToFile(It.IsAny<object>(), It.IsAny<string>()))
            .Callback<object, string>((obj, _) => savedToDisk = (PluginConfiguration)obj);

        var libraryManagerMock = new Mock<ILibraryManager>();
        var appPaths = new FakeApplicationPaths(tempDir);
        var webClientInjector = new WebClientInjector(appPaths, NullLogger<WebClientInjector>.Instance);
        var provisioning = new LibraryProvisioningService(libraryManagerMock.Object, NullLogger<LibraryProvisioningService>.Instance);
        using var serviceProvider = new ServiceCollection().BuildServiceProvider();

        var plugin = new Plugin(appPaths, xml.Object, NullLogger<Plugin>.Instance, libraryManagerMock.Object, webClientInjector, provisioning, serviceProvider);

        plugin.Configuration.RemoteServers.Add(new RemoteServer { Id = "friend-1", Name = "Bob", Url = "http://bob.example", ApiKey = "friend-secret", IssuedApiKey = "issued-secret" });
        plugin.Configuration.InternalRelayApiKey = "relay-secret";

        plugin.SaveConfiguration();

        Assert.NotNull(savedToDisk);
        var savedServer = Assert.Single(savedToDisk!.RemoteServers);
        Assert.NotEqual("friend-secret", savedServer.ApiKey);
        Assert.NotEqual("issued-secret", savedServer.IssuedApiKey);
        Assert.NotEqual("relay-secret", savedToDisk.InternalRelayApiKey);
        Assert.Equal("friend-secret", ApiKeyProtector.Unprotect(savedServer.ApiKey));
        Assert.Equal("issued-secret", ApiKeyProtector.Unprotect(savedServer.IssuedApiKey));

        // The live, in-memory config is untouched - every other call site in the
        // plugin keeps seeing plaintext for the rest of the process's life.
        Assert.Equal("friend-secret", plugin.Configuration.RemoteServers[0].ApiKey);
        Assert.Equal("issued-secret", plugin.Configuration.RemoteServers[0].IssuedApiKey);
        Assert.Equal("relay-secret", plugin.Configuration.InternalRelayApiKey);
    }

    [Fact]
    public void Constructor_LoadsAnEncryptedConfiguration_DecryptsKeysInPlace()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "federation-tests-" + Guid.NewGuid().ToString("N"));
        var xml = new Mock<IXmlSerializer>();

        var onDisk = new PluginConfiguration
        {
            RemoteServers = new List<RemoteServer>
            {
                new RemoteServer { Id = "friend-1", Name = "Bob", Url = "http://bob.example", ApiKey = ApiKeyProtector.Protect("friend-secret") }
            }
        };
        xml.Setup(x => x.DeserializeFromFile(It.IsAny<Type>(), It.IsAny<string>())).Returns(onDisk);
        xml.Setup(x => x.SerializeToFile(It.IsAny<object>(), It.IsAny<string>()));

        var libraryManagerMock = new Mock<ILibraryManager>();
        var appPaths = new FakeApplicationPaths(tempDir);
        var webClientInjector = new WebClientInjector(appPaths, NullLogger<WebClientInjector>.Instance);
        var provisioning = new LibraryProvisioningService(libraryManagerMock.Object, NullLogger<LibraryProvisioningService>.Instance);
        using var serviceProvider = new ServiceCollection().BuildServiceProvider();

        var plugin = new Plugin(appPaths, xml.Object, NullLogger<Plugin>.Instance, libraryManagerMock.Object, webClientInjector, provisioning, serviceProvider);

        Assert.Equal("friend-secret", plugin.Configuration.RemoteServers[0].ApiKey);
    }

    [Fact]
    public void Constructor_LoadsAPreEncryptionPlaintextConfiguration_KeepsItUsable()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "federation-tests-" + Guid.NewGuid().ToString("N"));
        var xml = new Mock<IXmlSerializer>();

        var onDisk = new PluginConfiguration
        {
            RemoteServers = new List<RemoteServer>
            {
                new RemoteServer { Id = "friend-1", Name = "Bob", Url = "http://bob.example", ApiKey = "plaintext-from-before-this-feature" }
            }
        };
        xml.Setup(x => x.DeserializeFromFile(It.IsAny<Type>(), It.IsAny<string>())).Returns(onDisk);
        xml.Setup(x => x.SerializeToFile(It.IsAny<object>(), It.IsAny<string>()));

        var libraryManagerMock = new Mock<ILibraryManager>();
        var appPaths = new FakeApplicationPaths(tempDir);
        var webClientInjector = new WebClientInjector(appPaths, NullLogger<WebClientInjector>.Instance);
        var provisioning = new LibraryProvisioningService(libraryManagerMock.Object, NullLogger<LibraryProvisioningService>.Instance);
        using var serviceProvider = new ServiceCollection().BuildServiceProvider();

        var plugin = new Plugin(appPaths, xml.Object, NullLogger<Plugin>.Instance, libraryManagerMock.Object, webClientInjector, provisioning, serviceProvider);

        Assert.Equal("plaintext-from-before-this-feature", plugin.Configuration.RemoteServers[0].ApiKey);
    }

    private sealed class FakeApplicationPaths : IApplicationPaths
    {
        private readonly string _root;

        public FakeApplicationPaths(string root)
        {
            _root = root;
        }

        public string ProgramDataPath => _root;

        public string WebPath => _root;

        public string ProgramSystemPath => _root;

        public string DataPath => _root;

        public string ImageCachePath => _root;

        public string PluginsPath => _root;

        public string PluginConfigurationsPath => _root;

        public string LogDirectoryPath => _root;

        public string ConfigurationDirectoryPath => _root;

        public string SystemConfigurationFilePath => Path.Combine(_root, "system.xml");

        public string CachePath => _root;

        public string TempDirectory => _root;

        public string VirtualDataPath => _root;

        public string TrickplayPath => _root;

        public string BackupPath => _root;

        public void MakeSanityCheckOrThrow()
        {
        }

        public void CreateAndCheckMarker(string path, string markerName, bool throwOnMismatch = true)
        {
        }
    }
}
