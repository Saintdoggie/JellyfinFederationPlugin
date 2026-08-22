using Jellyfin.Plugin.Federation.Middleware;
using Jellyfin.Plugin.Federation.Providers;
using Jellyfin.Plugin.Federation.Services;
using Jellyfin.Plugin.Federation.Tasks;
using MediaBrowser.Controller;
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
            serviceCollection.AddSingleton<FederationLibraryManager>();
            serviceCollection.AddSingleton<RemoteAccessControlService>();
            serviceCollection.AddSingleton<FederationSyncService>();
            serviceCollection.AddSingleton<FederationItemPersistenceService>();
            serviceCollection.AddSingleton<LibraryProvisioningService>();
            serviceCollection.AddSingleton<FederationStreamHandler>();
            serviceCollection.AddSingleton<FederationDownloadService>();
            serviceCollection.AddSingleton<FederationPlaybackTokenService>();
            serviceCollection.AddSingleton<FederationUserSessionTokenService>();
            serviceCollection.AddSingleton<FederationPeerAccessService>();

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

            serviceCollection.AddHostedService<FederationEntryPoint>();
        }
    }
}
