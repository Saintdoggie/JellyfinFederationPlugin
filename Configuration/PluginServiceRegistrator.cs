using System.Linq;
using Jellyfin.Plugin.Federation.Middleware;
using Jellyfin.Plugin.Federation.Providers;
using Jellyfin.Plugin.Federation.Services;
using Jellyfin.Plugin.Federation.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
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
            // Required by FederationMediaSourceProvider to auto-detect this
            // server's public URL for Proxy streaming. AddHttpContextAccessor is
            // idempotent if the host already registered it.
            serviceCollection.AddHttpContextAccessor();
            serviceCollection.AddSingleton<IRemoteServerClientFactory, RemoteServerClientFactory>();
            serviceCollection.AddSingleton<FederationItemCache>();
            serviceCollection.AddSingleton<WanBandwidthMonitor>();
            serviceCollection.AddSingleton<AdaptiveSourceRanking>();
            serviceCollection.AddSingleton<AdaptivePreparationGate>();
            serviceCollection.AddSingleton<FederationLibraryManager>();
            serviceCollection.AddSingleton<FederationArtworkService>();
            serviceCollection.AddSingleton<RemoteAccessControlService>();
            serviceCollection.AddSingleton<FederationSyncService>();
            serviceCollection.AddSingleton<FederationItemPersistenceService>();
            serviceCollection.AddSingleton<FederationAvailabilityService>();
            serviceCollection.AddSingleton<LibraryProvisioningService>();
            serviceCollection.AddSingleton<FederationStreamHandler>();
            serviceCollection.AddSingleton<FederationDownloadService>();
            serviceCollection.AddSingleton<FederationPlaybackTokenService>();
            serviceCollection.AddSingleton<FederationUserSessionTokenService>();
            serviceCollection.AddSingleton<FederationPeerAccessService>();
            serviceCollection.AddSingleton<FederationNowWatchingService>();
            serviceCollection.AddSingleton<FederationQualityAdvisorService>();
            serviceCollection.AddSingleton<PlexStrmExportService>();
            serviceCollection.AddSingleton<IProcessRunner, ProcessRunner>();
            serviceCollection.AddSingleton<TailscaleService>();
            serviceCollection.AddSingleton<FunFactService>();

            // Catalog providers for non-Jellyfin servers. Registering another
            // product means adding its IExternalCatalogProvider here and nowhere
            // else - the registry is resolved by everything that dispatches on
            // server kind. See IExternalCatalogProvider.
            serviceCollection.AddSingleton<IExternalCatalogProvider, PlexCatalogProvider>();
            serviceCollection.AddSingleton<ExternalCatalogRegistry>();

            // Scoped, not Singleton: needs IAuthenticationManager, which Jellyfin
            // registers scoped. FederationSyncService (a singleton) resolves it
            // through a short-lived DI scope rather than a direct constructor
            // dependency - see DiscoverFriendsOfFriendsAsync there.
            serviceCollection.AddScoped<FederationFriendService>();
            serviceCollection.AddSingleton<FederationImageProvider>();
            serviceCollection.AddSingleton<FederationMetadataProvider>();
            serviceCollection.AddSingleton<FederationMediaSourceProvider>();
            serviceCollection.AddSingleton<FederationRefreshTask>();
            serviceCollection.AddSingleton<WebClientInjector>();

            // Serve-time fallback for the file-write injection above: works even
            // on read-only web-root filesystems, and self-heals immediately after
            // a jellyfin-web upgrade replaces index.html. See BadgeScriptInjectionMiddleware.
            serviceCollection.AddSingleton<IStartupFilter, FederationBadgeStartupFilter>();

            // Workaround for a Jellyfin server bug where /web/ConfigurationPage
            // (DashboardController.FileStreamResult) returns corrupted gzip/br bodies
            // (ERR_CONTENT_DECODING_FAILED) while the same HTML served as a ContentResult
            // compresses correctly — see ConfigurationPageCompressionFixMiddleware.
            serviceCollection.AddSingleton<IStartupFilter, ConfigurationPageCompressionFixStartupFilter>();

            DecorateMediaSourceManager(serviceCollection);

            serviceCollection.AddHostedService<FederationEntryPoint>();
            // Must be the same singleton EntryPoint/persistence consult: AddHostedService<T>()
            // would construct a second instance whose probes never reach IsOffline().
            serviceCollection.AddHostedService(sp => sp.GetRequiredService<FederationAvailabilityService>());
        }

        /// <summary>
        /// Wraps Jellyfin's <see cref="IMediaSourceManager"/> with
        /// <see cref="FederationMediaSourceManager"/> (bounded ffmpeg analysis window
        /// for federated sources). Does nothing - leaving Jellyfin's own registration
        /// in place - when that registration is not a plain singleton type mapping,
        /// so an unexpected host layout can never stop the server from starting.
        /// </summary>
        internal static bool DecorateMediaSourceManager(IServiceCollection serviceCollection)
        {
            var original = serviceCollection.LastOrDefault(d => d.ServiceType == typeof(IMediaSourceManager));
            if (original?.ImplementationType == null
                || original.Lifetime != ServiceLifetime.Singleton
                || original.ImplementationType == typeof(FederationMediaSourceManager))
            {
                return false;
            }

            var implementationType = original.ImplementationType;
            serviceCollection.Remove(original);
            serviceCollection.AddSingleton<IMediaSourceManager>(sp =>
                new FederationMediaSourceManager((IMediaSourceManager)ActivatorUtilities.CreateInstance(sp, implementationType)));
            return true;
        }
    }
}
