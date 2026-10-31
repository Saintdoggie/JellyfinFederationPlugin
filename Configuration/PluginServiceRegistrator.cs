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
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

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
            RegisterCapturingLoggers(serviceCollection);
            serviceCollection.AddSingleton<IRemoteServerClientFactory>(sp =>
                new RemoteServerClientFactory(sp.GetRequiredService<FederationCapturingLoggerFactory>()));
            serviceCollection.AddSingleton<FederationItemCache>();
            serviceCollection.AddSingleton<WanBandwidthMonitor>();
            serviceCollection.AddSingleton<PlaybackRouteMetrics>();
            serviceCollection.AddSingleton<FederationBandwidthAllocator>();
            serviceCollection.AddSingleton<AdaptiveSourceRanking>();
            serviceCollection.AddSingleton<AdaptivePreparationGate>();
            serviceCollection.AddSingleton<FederationLibraryManager>();
            serviceCollection.AddSingleton<FederationArtworkService>();
            serviceCollection.AddSingleton<RemoteAccessControlService>();
            serviceCollection.AddSingleton<FederationVisibilityService>();
            serviceCollection.AddSingleton<FederationRemoteTranscodeGateway>();
            serviceCollection.AddSingleton<RemoteTranscodeSessionRegistry>();
            serviceCollection.AddSingleton<FriendDataLimits>();
            serviceCollection.AddSingleton<RemoteTranscodeHandoffs>();
            serviceCollection.AddHostedService<RemoteTranscodeSessionBridge>();
            serviceCollection.AddSingleton<FederationSyncService>();
            serviceCollection.AddSingleton<FederationChangeNotifier>();
            serviceCollection.AddSingleton<FederationItemPersistenceService>();
            serviceCollection.AddSingleton<FederationAvailabilityService>();
            serviceCollection.AddSingleton<FederationLogBuffer>();
            serviceCollection.AddSingleton<AvailabilityScheduleStore>();
            serviceCollection.AddSingleton<LibraryProvisioningService>();
            serviceCollection.AddSingleton<FederationStreamHandler>();
            serviceCollection.AddSingleton<FederationDownloadService>();
            serviceCollection.AddSingleton<FederationDeviceDownloadService>();
            serviceCollection.AddSingleton<FederationFriendFavoritesService>();
            serviceCollection.AddSingleton<FederationFriendPopularService>();
            serviceCollection.AddSingleton<PartyItemMapper>();
            serviceCollection.AddSingleton<FederatedPartyService>();
            serviceCollection.AddHostedService(sp => sp.GetRequiredService<FederatedPartyService>());
            serviceCollection.AddSingleton<FederationPlaybackTokenService>();
            serviceCollection.AddSingleton<FederationUserSessionTokenService>();
            serviceCollection.AddSingleton<FederationPeerAccessService>();
            serviceCollection.AddSingleton<FederationNowWatchingService>();
            serviceCollection.AddSingleton<FriendHardwareCache>();
            serviceCollection.AddSingleton<TranscodePlacementService>();
            serviceCollection.AddSingleton<FederationViewerReporter>();
            serviceCollection.AddHostedService(sp => sp.GetRequiredService<FederationViewerReporter>());
            serviceCollection.AddSingleton<FederationRemoteViewerService>();
            serviceCollection.AddHostedService(sp => sp.GetRequiredService<FederationRemoteViewerService>());
            serviceCollection.AddSingleton<FederationQualityAdvisorService>();
            serviceCollection.TryAddSingleton<IFederationPostSyncExport, NoopFederationPostSyncExport>();
            serviceCollection.TryAddSingleton<IPlexCompanionIntegration, UnavailablePlexCompanionIntegration>();
            serviceCollection.AddSingleton<IProcessRunner, ProcessRunner>();
            serviceCollection.AddSingleton<TailscaleService>();
            serviceCollection.AddSingleton<Services.DirectLink.DirectLinkManager>();
            serviceCollection.AddHostedService(sp => sp.GetRequiredService<Services.DirectLink.DirectLinkManager>());
            serviceCollection.AddSingleton<Services.Pairing.PairingService>();
            serviceCollection.AddHostedService(sp => sp.GetRequiredService<Services.Pairing.PairingService>());
            serviceCollection.AddSingleton<FunFactService>();

            // Optional assemblies add their own IExternalCatalogProvider registrations.
            serviceCollection.AddSingleton<ExternalCatalogRegistry>();

            // Scoped, not Singleton: needs IAuthenticationManager, which Jellyfin
            // registers scoped. FederationSyncService (a singleton) resolves it
            // through a short-lived DI scope rather than a direct constructor
            // dependency - see DiscoverFriendsOfFriendsAsync there.
            serviceCollection.AddScoped<FederationFriendService>();
            serviceCollection.AddSingleton<FederationImageProvider>();
            serviceCollection.AddTransient<FederationMediaSourceNameFilter>();
            serviceCollection.Configure<Microsoft.AspNetCore.Mvc.MvcOptions>(options => options.Filters.AddService<FederationMediaSourceNameFilter>());
            serviceCollection.AddTransient<FederationDirectStreamRedirectFilter>();
            serviceCollection.Configure<Microsoft.AspNetCore.Mvc.MvcOptions>(options => options.Filters.AddService<FederationDirectStreamRedirectFilter>());
            serviceCollection.AddTransient<FederationRemoteTranscodeFilter>();
            serviceCollection.Configure<Microsoft.AspNetCore.Mvc.MvcOptions>(options => options.Filters.AddService<FederationRemoteTranscodeFilter>());
            serviceCollection.AddTransient<FederationDeviceDownloadFilter>();
            serviceCollection.Configure<Microsoft.AspNetCore.Mvc.MvcOptions>(options => options.Filters.AddService<FederationDeviceDownloadFilter>());
            serviceCollection.AddTransient<FederationCanDownloadFilter>();
            serviceCollection.Configure<Microsoft.AspNetCore.Mvc.MvcOptions>(options => options.Filters.AddService<FederationCanDownloadFilter>());
            serviceCollection.AddTransient<FederatedPartyFilter>();
            serviceCollection.Configure<Microsoft.AspNetCore.Mvc.MvcOptions>(options => options.Filters.AddService<FederatedPartyFilter>());
            serviceCollection.AddTransient<FederationLocalOnlyItemsFilter>();
            serviceCollection.Configure<Microsoft.AspNetCore.Mvc.MvcOptions>(options => options.Filters.AddService<FederationLocalOnlyItemsFilter>());
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

        /// <summary>
        /// Gives this plugin's own classes an ILogger&lt;T&gt; that also feeds the
        /// settings page's Log tab (see <see cref="FederationCapturingLoggerFactory"/>).
        /// Closed registrations only, one per plugin type that asks for a
        /// logger, so Jellyfin's own loggers are left untouched.
        /// </summary>
        internal static void RegisterCapturingLoggers(IServiceCollection serviceCollection)
        {
            serviceCollection.AddSingleton<FederationCapturingLoggerFactory>(sp => new FederationCapturingLoggerFactory(
                sp.GetRequiredService<ILoggerFactory>(),
                sp.GetRequiredService<FederationLogBuffer>()));

            System.Type[] types;
            try
            {
                types = typeof(PluginServiceRegistrator).Assembly.GetTypes();
            }
            catch (System.Reflection.ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t != null).ToArray()!;
            }

            var categories = types
                .Where(t => t.IsClass && !t.IsAbstract && !t.ContainsGenericParameters)
                .SelectMany(t => t.GetConstructors())
                .SelectMany(c => c.GetParameters())
                .Select(p => p.ParameterType)
                .Where(t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(ILogger<>))
                .Select(t => t.GetGenericArguments()[0])
                .Distinct();

            foreach (var category in categories)
            {
                serviceCollection.AddSingleton(
                    typeof(ILogger<>).MakeGenericType(category),
                    sp => System.Activator.CreateInstance(
                        typeof(Logger<>).MakeGenericType(category),
                        sp.GetRequiredService<FederationCapturingLoggerFactory>())!);
            }
        }
    }
}
