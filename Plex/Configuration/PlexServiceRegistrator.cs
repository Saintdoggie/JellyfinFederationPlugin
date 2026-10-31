using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Jellyfin.Plugin.Federation.Configuration
{
    /// <summary>Registers Plex support only when its optional assembly is installed.</summary>
    public sealed class PlexServiceRegistrator : IPluginServiceRegistrator
    {
        public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
        {
            serviceCollection.AddSingleton<IExternalCatalogProvider, PlexCatalogProvider>();
            serviceCollection.AddSingleton<IFederationWebPageProvider, PlexWebPage>();
            serviceCollection.RemoveAll<IPlexCompanionIntegration>();
            serviceCollection.AddSingleton<IPlexCompanionIntegration, PlexCompanionService>();
            serviceCollection.AddSingleton<PlexMetadataSourceService>();
            serviceCollection.AddSingleton<PlexLibraryImportService>();
            serviceCollection.AddSingleton<PlexStrmExportService>();
            serviceCollection.RemoveAll<IFederationPostSyncExport>();
            serviceCollection.AddSingleton<IFederationPostSyncExport>(sp => sp.GetRequiredService<PlexStrmExportService>());
        }
    }
}
