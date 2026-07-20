using Jellyfin.Plugin.Federation.Providers;
using Jellyfin.Plugin.Federation.Resolvers;
using Jellyfin.Plugin.Federation.Services;
using Jellyfin.Plugin.Federation.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Federation.Configuration
{
    /// <summary>
    /// Registers federation services with the Jellyfin DI container.
    /// Discovered by Jellyfin via <see cref="IPluginServiceRegistrator"/>.
    /// </summary>
    public class PluginServiceRegistrator : IPluginServiceRegistrator
    {
        /// <inheritdoc />
        public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
        {
            serviceCollection.AddSingleton<IRemoteServerClientFactory, RemoteServerClientFactory>();
            serviceCollection.AddSingleton<FederationItemCache>();
            serviceCollection.AddSingleton<FederationLibraryManager>();
            serviceCollection.AddSingleton<FederationSyncService>();
            serviceCollection.AddSingleton<FederationItemPersistenceService>();
            serviceCollection.AddSingleton<LibraryProvisioningService>();
            serviceCollection.AddSingleton<FederationStreamHandler>();
            serviceCollection.AddSingleton<FederationItemResolver>();
            serviceCollection.AddSingleton<FederationImageProvider>();
            serviceCollection.AddSingleton<FederationMetadataProvider>();
            serviceCollection.AddSingleton<FederationMediaSourceProvider>();
            serviceCollection.AddSingleton<FederationRefreshTask>();
            serviceCollection.AddHostedService<FederationEntryPoint>();
        }
    }
}
