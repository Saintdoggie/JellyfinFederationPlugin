using System;
using System.IO;
using Jellyfin.Plugin.Federation.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Jellyfin.Plugin.Federation.Tests;

/// <summary>
/// Constructs a real <see cref="Plugin"/> (via its actual constructor, so every
/// BasePlugin field is properly initialized - unlike <see cref="PluginInstanceRestorer"/>'s
/// FormatterServices.GetUninitializedObject shortcut) with a mocked IXmlSerializer
/// that never touches disk, so tests can freely read and mutate
/// <c>Plugin.Instance.Configuration</c> and call <c>SaveConfiguration()</c>.
/// Must share the "PluginInstance" xUnit collection with any other test class that
/// touches the static Plugin.Instance field, to avoid cross-test races.
/// </summary>
internal sealed class RealPluginInstance : IDisposable
{
    private readonly Plugin? _original;

    public RealPluginInstance()
    {
        _original = Plugin.Instance;

        var tempDir = Path.Combine(Path.GetTempPath(), "federation-tests-" + Guid.NewGuid().ToString("N"));

        var xml = new Mock<IXmlSerializer>();

        // Simulates "no config file on disk yet" so BasePlugin<T> falls back to a
        // fresh default PluginConfiguration - the same path a first-ever run takes.
        xml.Setup(x => x.DeserializeFromFile(It.IsAny<Type>(), It.IsAny<string>()))
            .Throws(new FileNotFoundException());

        // SaveConfiguration() calls this; a no-op keeps tests from writing real files.
        xml.Setup(x => x.SerializeToFile(It.IsAny<object>(), It.IsAny<string>()));

        // Plugin's constructor sets the static Plugin.Instance itself.
        _ = new Plugin(new FakeApplicationPaths(tempDir), xml.Object, NullLogger<Plugin>.Instance);
    }

    public PluginConfiguration Configuration => Plugin.Instance!.Configuration;

    public void Dispose()
    {
        var field = typeof(Plugin).GetField("<Instance>k__BackingField", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?? throw new InvalidOperationException("Could not locate Plugin.Instance backing field.");
        field.SetValue(null, _original);
    }

    /// <summary>
    /// BasePlugin{T}'s own constructor Path.Combine()s several of these paths (e.g.
    /// to derive DataFolderPath from PluginsPath), so every path needs *some*
    /// non-null value up front, not just the ones Configuration load/save touch. A
    /// plain fake is simpler and more obviously correct here than trying to
    /// enumerate and Moq-setup each property via reflection.
    /// </summary>
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
