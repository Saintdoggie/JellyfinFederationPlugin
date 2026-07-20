using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Federation.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation
{
    /// <summary>
    /// Jellyfin Federation Plugin - aggregate content from multiple Jellyfin servers.
    /// </summary>
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        private readonly ILogger<Plugin> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="Plugin"/> class.
        /// </summary>
        public Plugin(
            IApplicationPaths applicationPaths,
            IXmlSerializer xmlSerializer,
            ILogger<Plugin> logger)
            : base(applicationPaths, xmlSerializer)
        {
            _logger = logger;
            Instance = this;
            _logger.LogInformation("=== Jellyfin Federation Plugin v{Version} Initialized ===", Version);
        }

        /// <inheritdoc />
        public override string Name => "Jellyfin Federation";

        /// <inheritdoc />
        public override Guid Id => Guid.Parse("495feadb-d27f-46c3-bb9b-0732ae8926fa");

        /// <inheritdoc />
        public override string Description => "Aggregate content from multiple Jellyfin servers into unified virtual libraries.";

        /// <summary>
        /// Gets the plugin singleton instance.
        /// </summary>
        public static Plugin? Instance { get; private set; }

        /// <summary>
        /// Resolves the default cache path inside the plugin data directory.
        /// </summary>
        public string GetDefaultCachePath() => System.IO.Path.Combine(DataFolderPath, "federation-cache.json");

        /// <inheritdoc />
        public IEnumerable<PluginPageInfo> GetPages()
        {
            yield return new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html"
            };
        }
    }
}
