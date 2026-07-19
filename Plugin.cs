using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Federation.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.DependencyInjection;
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
        public override Guid Id => Guid.Parse("12345678-1234-1234-1234-123456789abc");

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
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.redirectPage.html"
            };
        }

        /// <summary>
        /// Gets the configuration page URL.
        /// </summary>
        public string GetConfigurationPageUrl() => "/Plugins/Federation/ConfigPage";

        /// <summary>
        /// Registers federation services with the Jellyfin DI container.
        /// </summary>
        public static void RegisterServices(IServiceCollection serviceCollection)
        {
            serviceCollection.AddSingleton<Services.IRemoteServerClientFactory, Services.RemoteServerClientFactory>();
            serviceCollection.AddSingleton<Services.FederationItemCache>();
            serviceCollection.AddSingleton<Services.FederationLibraryManager>();
            serviceCollection.AddSingleton<Services.FederationSyncService>();
            serviceCollection.AddSingleton<Services.LibraryProvisioningService>();
            serviceCollection.AddSingleton<Services.FederationStreamHandler>();
            serviceCollection.AddSingleton<Resolvers.FederationItemResolver>();
            serviceCollection.AddSingleton<Providers.FederationImageProvider>();
            serviceCollection.AddSingleton<Providers.FederationMetadataProvider>();
            serviceCollection.AddSingleton<Tasks.FederationRefreshTask>();
            serviceCollection.AddSingleton<FederationEntryPoint>();
        }
    }
}
