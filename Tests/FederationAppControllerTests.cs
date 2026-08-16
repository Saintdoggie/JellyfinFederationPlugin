using System;
using System.Collections.Generic;
using System.Threading;
using Jellyfin.Plugin.Federation.Api;
using Jellyfin.Plugin.Federation.Configuration;
using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

/// <summary>
/// Covers <see cref="FederationAppController"/>, the standalone local admin UI
/// that replaced the old in-Jellyfin config page (see
/// <c>SECURITY_REVIEW_2026-08-15.md</c> discussion and the "rides Jellyfin's
/// own port instead of a second one" pivot). Actions are called directly, the
/// normal way to unit test an ASP.NET Core controller, rather than standing up
/// a real HTTP pipeline.
/// </summary>
[Collection("PluginInstance")]
public class FederationAppControllerTests : IDisposable
{
    private readonly RealPluginInstance _plugin;
    private readonly FederationAppController _controller;

    public FederationAppControllerTests()
    {
        _plugin = new RealPluginInstance();

        var clientFactory = new Mock<IRemoteServerClientFactory>();
        var cache = new FederationItemCache(NullLogger<FederationItemCache>.Instance);
        var bandwidthMonitor = new WanBandwidthMonitor(NullLogger<WanBandwidthMonitor>.Instance, clientFactory.Object);
        var federationManager = new FederationLibraryManager(Mock.Of<ILibraryManager>(), NullLogger<FederationLibraryManager>.Instance, clientFactory.Object, cache, bandwidthMonitor);
        var persistence = new FederationItemPersistenceService(Mock.Of<ILibraryManager>(), NullLogger<FederationItemPersistenceService>.Instance, federationManager);
        var provisioning = new LibraryProvisioningService(Mock.Of<ILibraryManager>(), NullLogger<LibraryProvisioningService>.Instance);
        var syncService = new FederationSyncService(
            NullLogger<FederationSyncService>.Instance,
            federationManager,
            clientFactory.Object,
            cache,
            persistence,
            bandwidthMonitor,
            Mock.Of<IServiceProvider>());

        var appHost = new Mock<IServerApplicationHost>();
        appHost.SetupGet(h => h.FriendlyName).Returns("This Server");
        var friends = new FederationFriendService(
            NullLogger<FederationFriendService>.Instance,
            Mock.Of<IAuthenticationManager>(),
            appHost.Object,
            federationManager,
            Mock.Of<IHttpContextAccessor>(),
            clientFactory.Object,
            Mock.Of<IUserManager>(),
            Mock.Of<ILibraryManager>());

        var directory = new FederationDirectoryService(NullLogger<FederationDirectoryService>.Instance, Mock.Of<IServiceProvider>());

        _controller = new FederationAppController(
            NullLogger<FederationAppController>.Instance,
            federationManager,
            syncService,
            provisioning,
            clientFactory.Object,
            cache,
            bandwidthMonitor,
            directory,
            friends);
    }

    public void Dispose() => _plugin.Dispose();

    [Fact]
    public void Index_ServesTheEmbeddedStaticPage()
    {
        var result = Assert.IsType<ContentResult>(_controller.Index());

        Assert.Contains("Federation", result.Content, StringComparison.Ordinal);
        Assert.Equal("text/html; charset=utf-8", result.ContentType);
    }

    [Fact]
    public void Styles_And_Script_AreServed()
    {
        Assert.IsType<ContentResult>(_controller.Styles());
        Assert.IsType<ContentResult>(_controller.Script());
    }

    [Fact]
    public void GetStatus_ReflectsCurrentConfiguration()
    {
        _plugin.Configuration.LocalUsername = "test_user";

        var result = Assert.IsType<OkObjectResult>(_controller.GetStatus());

        Assert.Equal("test_user", result.Value!.GetType().GetProperty("username")!.GetValue(result.Value));
    }

    [Fact]
    public void SaveProfile_RejectsInvalidUsername_AcceptsValidOne()
    {
        var bad = _controller.SaveProfile(new FederationAppController.ProfileRequest { Username = "x" });
        Assert.IsType<BadRequestObjectResult>(bad);

        var ok = _controller.SaveProfile(new FederationAppController.ProfileRequest { Username = "new_valid_name" });
        Assert.IsType<OkObjectResult>(ok);
        Assert.Equal("new_valid_name", _plugin.Configuration.LocalUsername);
    }

    [Fact]
    public void AddServer_ThenGetServers_RoundTrips_WithoutLeakingApiKey()
    {
        var addResult = Assert.IsType<OkObjectResult>(_controller.AddServer(new RemoteServer
        {
            Name = "Friend",
            Url = "https://friend.example",
            ApiKey = "super-secret"
        }));
        Assert.NotNull(addResult.Value);

        var listResult = Assert.IsType<OkObjectResult>(_controller.GetServers());
        var servers = Assert.IsAssignableFrom<IEnumerable<object>>(listResult.Value);

        var found = false;
        foreach (var s in servers)
        {
            var type = s.GetType();
            if ((string)type.GetProperty("Name")!.GetValue(s)! == "Friend")
            {
                found = true;
                Assert.True((bool)type.GetProperty("HasApiKey")!.GetValue(s)!);
                Assert.Null(type.GetProperty("ApiKey"));
            }
        }

        Assert.True(found, "Newly added server was not returned by GetServers.");
    }

    [Fact]
    public void DeleteServer_UnknownId_ReturnsNotFound()
    {
        var result = _controller.DeleteServer(Guid.NewGuid().ToString());

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async System.Threading.Tasks.Task AddMapping_RequiresKnownServerAndValidLibraryName()
    {
        var result = await _controller.AddMapping(
            new FederationAppController.CreateMappingRequest { ServerId = "missing", LocalLibraryName = "Movies" },
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }
}
