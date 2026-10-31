using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Federation.Api;
using Jellyfin.Plugin.Federation.Configuration;
using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

public class PlexAddInBoundaryTests
{
    [Fact]
    public void CoreRegistrations_WithoutAddIn_HaveNoPlexProviderAndUseSafeFallbacks()
    {
        var services = new ServiceCollection();
        new PluginServiceRegistrator().RegisterServices(services, Mock.Of<IServerApplicationHost>());

        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IExternalCatalogProvider));
        Assert.Equal(
            typeof(UnavailablePlexCompanionIntegration),
            Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IPlexCompanionIntegration)).ImplementationType);
        Assert.Equal(
            typeof(NoopFederationPostSyncExport),
            Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IFederationPostSyncExport)).ImplementationType);

        using var provider = services.BuildServiceProvider();
        Assert.IsType<UnavailablePlexCompanionIntegration>(provider.GetRequiredService<IPlexCompanionIntegration>());
        Assert.IsType<NoopFederationPostSyncExport>(provider.GetRequiredService<IFederationPostSyncExport>());
        Assert.Null(provider.GetRequiredService<ExternalCatalogRegistry>().Get(ServerKind.Plex));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AddInRegistrations_ReplaceFallbacksAndProvideThePlexPage(bool addInFirst)
    {
        var services = new ServiceCollection();
        if (addInFirst)
        {
            new PlexServiceRegistrator().RegisterServices(services, Mock.Of<IServerApplicationHost>());
        }

        new PluginServiceRegistrator().RegisterServices(services, Mock.Of<IServerApplicationHost>());
        if (!addInFirst)
        {
            new PlexServiceRegistrator().RegisterServices(services, Mock.Of<IServerApplicationHost>());
        }

        Assert.Equal(typeof(PlexCatalogProvider), Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IExternalCatalogProvider)).ImplementationType);
        Assert.Equal(typeof(PlexCompanionService), Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IPlexCompanionIntegration)).ImplementationType);
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IFederationPostSyncExport));
        Assert.Equal(typeof(PlexWebPage), Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IFederationWebPageProvider)).ImplementationType);
    }

    [Fact]
    public void CoreAssembly_ContainsNoPlexImplementationOrPlexRoutes()
    {
        var core = typeof(Plugin).Assembly;
        var forbiddenTypes = new[]
        {
            "PlexApiClient", "PlexCatalogProvider", "PlexCompanionService", "PlexStrmExportService",
            "PlexMetadataSourceService", "PlexLibraryImportService", "PlexMetadataSetupController", "PlexController", "PlexWebPage"
        };

        Assert.DoesNotContain(core.GetTypes(), type => forbiddenTypes.Contains(type.Name));
        var addIn = typeof(PlexCatalogProvider).Assembly;
        foreach (var typeName in forbiddenTypes)
        {
            Assert.Contains(addIn.GetTypes(), type => type.Name == typeName);
        }

        var coreRoutes = typeof(FederationController).GetMethods()
            .SelectMany(method => method.GetCustomAttributes<HttpMethodAttribute>())
            .SelectMany(attribute => attribute.Template == null ? Array.Empty<string>() : new[] { attribute.Template })
            .ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain("ExternalServers", coreRoutes);
        Assert.DoesNotContain("PlexOffers", coreRoutes);
        Assert.DoesNotContain("Servers/Companion", coreRoutes);
        Assert.DoesNotContain("Servers/{id}/CompanionConnectCode", coreRoutes);

        Assert.False(typeof(MediaBrowser.Common.Plugins.IPlugin).IsAssignableFrom(typeof(PlexWebPage)));
        var page = Assert.Single(new PlexWebPage().GetPages());
        Assert.Equal("FederationPlex", page.Name);
        using var resource = addIn.GetManifestResourceStream(page.EmbeddedResourcePath);
        Assert.NotNull(resource);
    }

    [Fact]
    public void AddInPageEndpoint_RequiresElevationAndServesOnlyNamedEmbeddedPages()
    {
        var method = typeof(FederationAddInPageController).GetMethod(nameof(FederationAddInPageController.GetPage))!;
        Assert.Equal("RequiresElevation", Assert.Single(method.GetCustomAttributes<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>()).Policy);
        var services = new ServiceCollection();
        services.AddSingleton<IFederationWebPageProvider, PlexWebPage>();
        using var provider = services.BuildServiceProvider();
        var controller = new FederationAddInPageController(provider, NullLogger<FederationAddInPageController>.Instance);

        Assert.IsType<Microsoft.AspNetCore.Mvc.NotFoundResult>(controller.GetPage("NotInstalled"));
        var result = Assert.IsType<Microsoft.AspNetCore.Mvc.FileStreamResult>(controller.GetPage("federationplex"));
        using (result.FileStream)
        using (var reader = new StreamReader(result.FileStream))
        {
            Assert.Contains("federationPlexPage", reader.ReadToEnd(), StringComparison.Ordinal);
        }

        using var loader = typeof(Plugin).Assembly.GetManifestResourceStream("Jellyfin.Plugin.Federation.Web.admin.addinPage.html");
        Assert.NotNull(loader);
        using var loaderReader = new StreamReader(loader);
        var markup = loaderReader.ReadToEnd();
        Assert.Contains("MediaBrowser Token=", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("X-Emby-Token", markup, StringComparison.Ordinal);
        Assert.Contains("AddInPages/", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("EmbeddedResourcePath=", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void SanitizedConfigurationSave_PreservesCompanionConnectionMetadata()
    {
        var existing = new RemoteServer
        {
            CompanionUrl = "https://companion.example",
            FederationPluginVersion = "1.2.3"
        };
        var posted = new RemoteServer();

        FederationServerPresentation.PreserveConnectionMetadata(existing, posted);

        Assert.Equal(existing.CompanionUrl, posted.CompanionUrl);
        Assert.Equal(existing.FederationPluginVersion, posted.FederationPluginVersion);
    }

    [Fact]
    public void RatingKeyGuid_AndPersistedPlexConfiguration_RemainCompatible()
    {
        Assert.Equal(Guid.Parse("c9f12ae3-1217-0139-bea7-8da45ac9f2c0"), ExternalItemIds.PlexRatingKeyToGuid("100"));
        Assert.Equal(ExternalItemIds.PlexRatingKeyToGuid("100"), PlexApiClient.RatingKeyToGuid("100"));

        var original = new PluginConfiguration
        {
            EnablePlexStrmExport = true,
            PlexStrmExportPath = "/example/export",
            RemoteServers =
            {
                new RemoteServer
                {
                    Id = "plex-source",
                    Kind = ServerKind.Plex,
                    Url = "https://plex.example",
                    ApiKey = "synthetic-token",
                    AllowedExternalLibraryIds = new() { "1", "2" }
                }
            },
            IncomingPlexOffers =
            {
                new IncomingPlexOffer { Id = "offer", Code = "synthetic-code", ServerName = "Example", CompanionUrl = "https://companion.example" }
            },
            PlexMetadataSource = new PlexMetadataSourceSettings
            {
                Enabled = true,
                ServerUrl = "https://metadata.example",
                Token = "synthetic-metadata-token"
            }
        };

        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var writer = new StringWriter();
        serializer.Serialize(writer, original);
        using var reader = new StringReader(writer.ToString());
        var restored = (PluginConfiguration)serializer.Deserialize(reader)!;
        var server = Assert.Single(restored.RemoteServers);

        Assert.Equal(ServerKind.Plex, server.Kind);
        Assert.Equal("synthetic-token", server.ApiKey);
        Assert.Equal(new[] { "1", "2" }, server.AllowedExternalLibraryIds);
        Assert.Equal("synthetic-code", Assert.Single(restored.IncomingPlexOffers).Code);
        Assert.Equal("synthetic-metadata-token", restored.PlexMetadataSource.Token);
        Assert.True(restored.EnablePlexStrmExport);
        Assert.Equal("/example/export", restored.PlexStrmExportPath);
    }
}

[Collection("PluginInstance")]
public class MissingPlexAddInTests : IDisposable
{
    private readonly RealPluginInstance _plugin = new();

    [Fact]
    public async Task UnavailableCompanion_DoesNotMutateConfiguration()
    {
        var server = new RemoteServer { Id = "existing", Kind = ServerKind.Companion, IssuedApiKey = "existing-token" };
        _plugin.Configuration.RemoteServers.Add(server);
        _plugin.Configuration.IncomingPlexOffers.Add(new IncomingPlexOffer { Id = "pending", Code = "keep" });
        var unavailable = new UnavailablePlexCompanionIntegration();

        Assert.False((await unavailable.ConnectAsync("code", CancellationToken.None)).Success);
        Assert.False(unavailable.ReceiveOffer("code").Success);
        Assert.Empty(unavailable.ListOffers());
        Assert.False((await unavailable.AcceptOfferAsync("pending", CancellationToken.None)).Success);
        Assert.False(unavailable.RejectOffer("pending").Success);
        Assert.False(unavailable.CreateReceiver("name", true, null).Success);
        Assert.False(unavailable.GetConnectCode(server).Success);
        Assert.Null(await unavailable.TryGetVersionAsync(server, CancellationToken.None));
        Assert.Null(unavailable.GetPoolInviteUrl(server));

        Assert.Equal("existing-token", Assert.Single(_plugin.Configuration.RemoteServers).IssuedApiKey);
        Assert.Equal("keep", Assert.Single(_plugin.Configuration.IncomingPlexOffers).Code);
    }

    [Fact]
    public async Task MissingProvider_ImagesFailBeforeCreatingAJellyfinClient()
    {
        var clientFactory = new Mock<IRemoteServerClientFactory>(MockBehavior.Strict);
        var server = new RemoteServer { Id = "plex-source", Kind = ServerKind.Plex, Url = "https://plex.example", Enabled = true };
        _plugin.Configuration.RemoteServers.Add(server);
        var cache = new FederationItemCache(NullLogger<FederationItemCache>.Instance);
        var manager = new FederationLibraryManager(
            Mock.Of<ILibraryManager>(),
            NullLogger<FederationLibraryManager>.Instance,
            clientFactory.Object,
            cache,
            new WanBandwidthMonitor(NullLogger<WanBandwidthMonitor>.Instance, clientFactory.Object),
            Mock.Of<MediaBrowser.Controller.Persistence.IMediaStreamRepository>());
        var remoteId = Guid.NewGuid();
        var entry = cache.UpsertRaw("Movies", server.Id, remoteId, new BaseItemDto { Id = remoteId, Type = BaseItemKind.Movie }, 0, "Movie");
        var item = new MediaBrowser.Controller.Entities.Movies.Movie();
        item.ProviderIds["FederationKey"] = entry.Key;
        var images = new Providers.FederationImageProvider(
            NullLogger<Providers.FederationImageProvider>.Instance,
            manager,
            new ExternalCatalogRegistry(Array.Empty<IExternalCatalogProvider>()));

        Assert.Empty(await images.GetImages(item, CancellationToken.None));
        clientFactory.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task MissingProvider_StreamFailsBeforeCreatingAJellyfinClient()
    {
        var clientFactory = new Mock<IRemoteServerClientFactory>(MockBehavior.Strict);
        var server = new RemoteServer { Id = "plex-source", Kind = ServerKind.Plex, Url = "https://plex.example", ApiKey = "synthetic-token" };
        _plugin.Configuration.RemoteServers.Add(server);
        var cache = new FederationItemCache(NullLogger<FederationItemCache>.Instance);
        var monitor = new WanBandwidthMonitor(NullLogger<WanBandwidthMonitor>.Instance, clientFactory.Object);
        var manager = new FederationLibraryManager(
            Mock.Of<ILibraryManager>(),
            NullLogger<FederationLibraryManager>.Instance,
            clientFactory.Object,
            cache,
            monitor,
            Mock.Of<MediaBrowser.Controller.Persistence.IMediaStreamRepository>());
        var handler = new FederationStreamHandler(
            NullLogger<FederationStreamHandler>.Instance,
            manager,
            new RemoteAccessControlService(NullLogger<RemoteAccessControlService>.Instance, cache),
            clientFactory.Object,
            new ExternalCatalogRegistry(Array.Empty<IExternalCatalogProvider>()),
            monitor);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.BuildDirectStreamUrlAsync(server.Id, Guid.NewGuid().ToString("N"), false, CancellationToken.None));

        Assert.Contains("optional Plex add-in", error.Message, StringComparison.Ordinal);
        clientFactory.VerifyNoOtherCalls();
    }

    public void Dispose() => _plugin.Dispose();
}

public partial class FederationSyncServiceTests
{
    [Fact]
    public async Task RefreshMapping_MissingPlexProvider_PreservesCacheAndNeverUsesJellyfinClient()
    {
        var clientFactory = new Mock<IRemoteServerClientFactory>(MockBehavior.Strict);
        var cache = new FederationItemCache(NullLogger<FederationItemCache>.Instance);
        var remoteId = ExternalItemIds.PlexRatingKeyToGuid("100");
        var original = cache.UpsertRaw(
            "Movies",
            "plex-source",
            remoteId,
            new BaseItemDto { Id = remoteId, Name = "Existing movie", Type = BaseItemKind.Movie },
            0,
            "Movie");
        original.SetNativeId("plex-source", remoteId, "100");

        var monitor = new WanBandwidthMonitor(NullLogger<WanBandwidthMonitor>.Instance, clientFactory.Object);
        var libraryManager = new FederationLibraryManager(
            Mock.Of<ILibraryManager>(),
            NullLogger<FederationLibraryManager>.Instance,
            clientFactory.Object,
            cache,
            monitor,
            Mock.Of<MediaBrowser.Controller.Persistence.IMediaStreamRepository>());
        var persistence = new FederationItemPersistenceService(
            Mock.Of<ILibraryManager>(),
            NullLogger<FederationItemPersistenceService>.Instance,
            libraryManager,
            Mock.Of<MediaBrowser.Controller.Persistence.IItemPersistenceService>());
        var sync = new FederationSyncService(
            NullLogger<FederationSyncService>.Instance,
            libraryManager,
            clientFactory.Object,
            cache,
            persistence,
            monitor,
            Mock.Of<IServiceProvider>(),
            new ExternalCatalogRegistry(Array.Empty<IExternalCatalogProvider>()));
        var server = new RemoteServer { Id = "plex-source", Kind = ServerKind.Plex, Enabled = true, WanCapMode = WanCapMode.Off };
        var mapping = new LibraryMapping
        {
            LocalLibraryName = "Movies",
            MediaType = "Movie",
            Enabled = true,
            RemoteLibrarySources = { new RemoteLibrarySource { ServerId = server.Id, RemoteLibraryId = "1", RemoteLibraryName = "Movies" } }
        };
        var config = new PluginConfiguration { EnableDedup = false, RemoteServers = { server } };
        var begin = typeof(FederationSyncService).GetMethod("BeginStatusRun", BindingFlags.NonPublic | BindingFlags.Instance)!;
        begin.Invoke(sync, new object?[] { new[] { mapping }, config, null });
        var method = typeof(FederationSyncService).GetMethod("RefreshMappingAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

        await (Task)method.Invoke(sync, new object?[] { mapping, config, CancellationToken.None, null })!;

        var kept = Assert.Single(cache.GetEntriesForMapping("Movies"));
        Assert.Equal(original.Key, kept.Key);
        Assert.Equal("100", kept.GetNativeId(Assert.Single(kept.GetSourcesSnapshot())));
        Assert.Contains("optional Plex add-in", sync.Status.Snapshot().Single().Error, StringComparison.Ordinal);
        clientFactory.VerifyNoOtherCalls();
    }
}
