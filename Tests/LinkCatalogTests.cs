using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;
using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

/// <summary>Per-link catalogue direction (0.0.222): <see cref="LinkCatalog"/> and where it filters.</summary>
public class LinkCatalogRuleTests
{
    private static RemoteServer Linked(LinkCatalogDirection mine, LinkCatalogDirection? peer)
        => new() { Id = "l", Name = "VPS", Linked = true, LinkCatalog = mine, LinkCatalogPeer = peer };

    [Theory]
    [InlineData(LinkCatalogDirection.BothWays, LinkCatalogDirection.BothWays)]
    [InlineData(LinkCatalogDirection.OnlyMineGoes, LinkCatalogDirection.OnlyTheirsComes)]
    [InlineData(LinkCatalogDirection.OnlyTheirsComes, LinkCatalogDirection.OnlyMineGoes)]
    public void Mirror_TurnsTheValueToTheOtherSide(LinkCatalogDirection mine, LinkCatalogDirection theirs)
    {
        Assert.Equal(theirs, LinkCatalog.Mirror(mine));
        Assert.Equal(mine, LinkCatalog.Mirror(LinkCatalog.Mirror(mine)));
    }

    [Theory]
    // mine, peer (mirrored to this side), receives here, sends there
    [InlineData(LinkCatalogDirection.BothWays, null, true, true)]
    [InlineData(LinkCatalogDirection.BothWays, LinkCatalogDirection.BothWays, true, true)]
    [InlineData(LinkCatalogDirection.OnlyMineGoes, null, false, true)]
    [InlineData(LinkCatalogDirection.OnlyMineGoes, LinkCatalogDirection.OnlyMineGoes, false, true)]
    [InlineData(LinkCatalogDirection.OnlyTheirsComes, LinkCatalogDirection.OnlyTheirsComes, true, false)]
    // Disagreement: the narrower applies.
    [InlineData(LinkCatalogDirection.BothWays, LinkCatalogDirection.OnlyMineGoes, false, true)]
    [InlineData(LinkCatalogDirection.OnlyMineGoes, LinkCatalogDirection.BothWays, false, true)]
    [InlineData(LinkCatalogDirection.BothWays, LinkCatalogDirection.OnlyTheirsComes, true, false)]
    [InlineData(LinkCatalogDirection.OnlyMineGoes, LinkCatalogDirection.OnlyTheirsComes, false, false)]
    public void NarrowerWins(LinkCatalogDirection mine, LinkCatalogDirection? peer, bool receives, bool sends)
    {
        var server = Linked(mine, peer);
        Assert.Equal(receives, LinkCatalog.ReceivesHere(server));
        Assert.Equal(sends, LinkCatalog.SendsThere(server));
        Assert.Equal(!receives, LinkCatalog.IsHiddenLinkedSource(server));
        Assert.False(string.IsNullOrEmpty(LinkCatalog.Describe(server)));
    }

    [Fact]
    public void OnlyLinkedServersAreEverHidden()
    {
        Assert.False(LinkCatalog.IsHiddenLinkedSource((RemoteServer?)null));
        var friend = new RemoteServer { Id = "f", LinkCatalog = LinkCatalogDirection.OnlyMineGoes, LinkCatalogPeer = LinkCatalogDirection.OnlyMineGoes };
        Assert.False(LinkCatalog.IsHiddenLinkedSource(friend));
        Assert.True(LinkCatalog.ReceivesHere(friend));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(-1, false)]
    public void OnlyTheThreeValuesAreAccepted(int value, bool ok) => Assert.Equal(ok, LinkCatalog.IsValid(value));

    [Fact]
    public void ConfigurationRoundTrip_KeepsTheValuesAsNumbers()
    {
        var server = Linked(LinkCatalogDirection.OnlyMineGoes, LinkCatalogDirection.OnlyMineGoes);
        var json = System.Text.Json.JsonSerializer.Serialize(server);
        Assert.Contains("\"LinkCatalog\":1", json, StringComparison.Ordinal);
        var xml = new System.Xml.Serialization.XmlSerializer(typeof(RemoteServer));
        using var writer = new StringWriter();
        xml.Serialize(writer, server);
        var back = (RemoteServer)xml.Deserialize(new StringReader(writer.ToString()))!;
        Assert.Equal(LinkCatalogDirection.OnlyMineGoes, back.LinkCatalog);
        Assert.Equal(LinkCatalogDirection.OnlyMineGoes, back.LinkCatalogPeer);

        // A 0.0.213 link has neither field: both ways, as it behaved.
        var old = (RemoteServer)xml.Deserialize(new StringReader("<RemoteServer><Id>x</Id><Linked>true</Linked></RemoteServer>"))!;
        Assert.Equal(LinkCatalogDirection.BothWays, old.LinkCatalog);
        Assert.Null(old.LinkCatalogPeer);
        Assert.True(LinkCatalog.ReceivesHere(old));
    }

    [Fact]
    public void TheConfigSaveCopiesTheFieldsFromTheStoredServerOnly()
    {
        // The direction must change only through its own endpoint (source check:
        // the main Save keeps the stored values and resets them for new rows).
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "Configuration", "FederationPluginController.cs"));
        Assert.Contains("server.LinkCatalog = oldServer.LinkCatalog;", source, StringComparison.Ordinal);
        Assert.Contains("server.LinkCatalogPeer = oldServer.LinkCatalogPeer;", source, StringComparison.Ordinal);
        Assert.Contains("server.LinkCatalog = LinkCatalogDirection.BothWays;", source, StringComparison.Ordinal);
    }

    internal static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JellyfinFederationPlugin.csproj")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}

[Collection("PluginInstance")]
public sealed class LinkCatalogServiceTests
{
    private sealed class Handler : HttpMessageHandler
    {
        public List<(string Path, string? Body, string? OwnerKey)> Calls { get; } = new();

        public Func<string, (HttpStatusCode, string)> Answer { get; set; } = _ => (HttpStatusCode.OK, "{\"ok\":true}");

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            request.Headers.TryGetValues(RemoteServerClient.OwnerKeyHeader, out var keys);
            Calls.Add((request.RequestUri!.AbsolutePath, body, keys?.FirstOrDefault()));
            var (status, text) = Answer(request.RequestUri!.AbsolutePath);
            return new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
        }
    }

    private static RemoteServer LinkedServer() => new()
    {
        Id = Guid.NewGuid().ToString(),
        Name = "VPS",
        Url = "https://vps.example.com",
        Kind = ServerKind.Jellyfin,
        Enabled = true,
        ApiKey = FederationTokenAuth.GenerateToken(),
        IssuedApiKey = FederationTokenAuth.GenerateToken(),
        FederationId = Guid.NewGuid().ToString(),
        Linked = true,
        LinkKey = new string('e', 64),
        IssuedLinkKey = new string('f', 64)
    };

    private static (LinkedServerService Service, Handler Handler, List<(string Id, bool ReceivedBefore)> Changes) NewService()
    {
        var handler = new Handler();
        var factory = new Mock<IRemoteServerClientFactory>();
        factory.Setup(f => f.GetClient(It.IsAny<RemoteServer>()))
            .Returns<RemoteServer>(s => new RemoteServerClient(s, NullLogger.Instance, new HttpClient(handler) { BaseAddress = new Uri(s.Url) }));
        var changes = new List<(string, bool)>();
        var service = new LinkedServerService(NullLogger<LinkedServerService>.Instance, factory.Object)
        {
            AfterImport = (_, _) => Task.CompletedTask,
            AfterDirection = (s, before) =>
            {
                changes.Add((s.Id, before));
                return Task.CompletedTask;
            }
        };
        return (service, handler, changes);
    }

    [Fact]
    public async Task SetDirection_TellsTheOtherServer_WithTheLinkKey_AndBothAgree()
    {
        using var plugin = new RealPluginInstance();
        var vps = LinkedServer();
        plugin.Configuration.RemoteServers.Add(vps);
        var (service, handler, changes) = NewService();

        // Home: only home's titles go to the VPS.
        var (ok, message) = await service.SetDirectionAsync(vps.Id, (int)LinkCatalogDirection.OnlyMineGoes, CancellationToken.None);

        Assert.True(ok, message);
        var call = Assert.Single(handler.Calls, c => c.Path.EndsWith("/Peer/Linked/Direction", StringComparison.Ordinal));
        Assert.Equal(new string('e', 64), call.OwnerKey);
        Assert.Contains("\"direction\":1", call.Body!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(LinkCatalogDirection.OnlyMineGoes, vps.LinkCatalog);
        Assert.Equal(LinkCatalogDirection.OnlyMineGoes, vps.LinkCatalogPeer);
        Assert.True(LinkCatalog.IsHiddenLinkedSource(vps));
        Assert.Equal(new[] { (vps.Id, true) }, changes);

        // The same value again changes nothing that is shown.
        await service.SetDirectionAsync(vps.Id, (int)LinkCatalogDirection.OnlyMineGoes, CancellationToken.None);
        Assert.Single(changes);
    }

    [Fact]
    public async Task SetDirection_OtherServerTooOld_SavesHere_SaysUpdate_AndTheNarrowerApplies()
    {
        using var plugin = new RealPluginInstance();
        var vps = LinkedServer();
        vps.LinkCatalogPeer = LinkCatalogDirection.OnlyMineGoes;
        plugin.Configuration.RemoteServers.Add(vps);
        var (service, handler, _) = NewService();
        handler.Answer = _ => (HttpStatusCode.NotFound, string.Empty);

        var (ok, message) = await service.SetDirectionAsync(vps.Id, (int)LinkCatalogDirection.BothWays, CancellationToken.None);

        Assert.True(ok);
        Assert.Contains("Update the other server", message, StringComparison.Ordinal);
        Assert.Equal(LinkCatalogDirection.BothWays, vps.LinkCatalog);
        Assert.True(LinkCatalog.IsHiddenLinkedSource(vps));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(-1)]
    public async Task SetDirection_RejectsUnknownValuesAndUnlinkedServers(int value)
    {
        using var plugin = new RealPluginInstance();
        var vps = LinkedServer();
        var friend = LinkedServer();
        friend.Linked = false;
        plugin.Configuration.RemoteServers.Add(vps);
        plugin.Configuration.RemoteServers.Add(friend);
        var (service, handler, _) = NewService();

        Assert.False((await service.SetDirectionAsync(vps.Id, value, CancellationToken.None)).Ok);
        Assert.False((await service.SetDirectionAsync(friend.Id, 1, CancellationToken.None)).Ok);
        Assert.False((await service.SetDirectionAsync("nope", 1, CancellationToken.None)).Ok);
        Assert.Empty(handler.Calls);
        Assert.Equal(LinkCatalogDirection.BothWays, vps.LinkCatalog);
    }

    [Fact]
    public async Task PeerSetDirection_StoresTheMirroredValue()
    {
        using var plugin = new RealPluginInstance();
        var home = LinkedServer();
        plugin.Configuration.RemoteServers.Add(home);
        var (service, _, changes) = NewService();

        // Home said "only mine goes": on the VPS that is "only theirs comes".
        var answer = await service.PeerSetDirectionAsync(home, new LinkDirectionBody { Direction = 1 });

        Assert.True(answer.Ok);
        Assert.Equal(LinkCatalogDirection.OnlyTheirsComes, home.LinkCatalog);
        Assert.Equal(LinkCatalogDirection.OnlyTheirsComes, home.LinkCatalogPeer);
        Assert.False(LinkCatalog.IsHiddenLinkedSource(home));
        Assert.False(LinkCatalog.SendsThere(home));
        Assert.Empty(changes);

        Assert.False((await service.PeerSetDirectionAsync(home, new LinkDirectionBody { Direction = 7 })).Ok);
        Assert.False((await service.PeerSetDirectionAsync(home, null)).Ok);
        Assert.Equal(LinkCatalogDirection.OnlyTheirsComes, home.LinkCatalog);
    }

    [Fact]
    public async Task StatusRefresh_NotesThePeersValue_AndHidesWhenItIsNarrower()
    {
        using var plugin = new RealPluginInstance();
        var vps = LinkedServer();
        plugin.Configuration.RemoteServers.Add(vps);
        var (service, handler, changes) = NewService();

        // The VPS says (from its side) "only theirs comes" = home's titles come to it, nothing goes back.
        handler.Answer = path => path.EndsWith("/Status", StringComparison.Ordinal)
            ? (HttpStatusCode.OK, "{\"version\":\"0.0.217.0\",\"direction\":2,\"capabilities\":[\"link-direction\"]}")
            : (HttpStatusCode.OK, "[]");

        var status = await service.RefreshStatusAsync(vps, CancellationToken.None);

        Assert.NotNull(status);
        Assert.Equal(LinkCatalogDirection.OnlyMineGoes, vps.LinkCatalogPeer);
        Assert.True(LinkCatalog.IsHiddenLinkedSource(vps));
        Assert.Equal(new[] { (vps.Id, true) }, changes);

        // An older server (no Status route) changes nothing.
        var old = LinkedServer();
        plugin.Configuration.RemoteServers.Add(old);
        handler.Answer = _ => (HttpStatusCode.NotFound, string.Empty);
        Assert.Null(await service.RefreshStatusAsync(old, CancellationToken.None));
        Assert.Equal(404, service.PeerStatus[old.Id].HttpStatus);
        Assert.Null(old.LinkCatalogPeer);
    }

    [Fact]
    public async Task Unlink_ForgetsTheDirection()
    {
        using var plugin = new RealPluginInstance();
        var vps = LinkedServer();
        vps.LinkCatalog = LinkCatalogDirection.OnlyMineGoes;
        vps.LinkCatalogPeer = LinkCatalogDirection.OnlyMineGoes;
        plugin.Configuration.RemoteServers.Add(vps);
        var (service, _, _) = NewService();

        await service.UnlinkAsync(vps.Id, CancellationToken.None);

        Assert.Equal(LinkCatalogDirection.BothWays, vps.LinkCatalog);
        Assert.Null(vps.LinkCatalogPeer);
        Assert.False(LinkCatalog.IsHiddenLinkedSource(vps));
    }

    [Fact]
    public void Export_LendsFriends_WhateverTheDirection()
    {
        // Decision: the direction is about the linked server's own titles; the
        // friends it lends are other servers and are lent either way.
        using var plugin = new RealPluginInstance();
        var vps = LinkedServer();
        vps.LinkCatalog = LinkCatalogDirection.OnlyTheirsComes;
        vps.LinkCatalogPeer = LinkCatalogDirection.OnlyTheirsComes;
        var friend = LinkedServer();
        friend.Linked = false;
        friend.Name = "Friend";
        plugin.Configuration.RemoteServers.Add(vps);
        plugin.Configuration.RemoteServers.Add(friend);

        Assert.Single(LinkedServerService.Export(vps));
    }

    [Fact]
    public void Status_ShowsTheDirection()
    {
        using var plugin = new RealPluginInstance();
        var vps = LinkedServer();
        vps.LinkCatalog = LinkCatalogDirection.OnlyMineGoes;
        plugin.Configuration.RemoteServers.Add(vps);
        var (service, _, _) = NewService();

        var row = Assert.Single(service.Status());
        Assert.Equal(LinkCatalogDirection.OnlyMineGoes, row.Direction);
        Assert.False(row.ReceivesHere);
        Assert.True(row.SendsThere);
        Assert.Contains("Only this server's titles go to VPS", row.DirectionText, StringComparison.Ordinal);

        var own = LinkedServerService.OwnStatus(vps);
        Assert.Equal(1, own.Direction);
        Assert.Contains("link-direction", own.Capabilities);
    }

    private static FederationItemCache NewCache() => new(NullLogger<FederationItemCache>.Instance);

    private static FederationLibraryManager NewManager(FederationItemCache cache, Mock<ILibraryManager> lm)
    {
        var clientFactory = new Mock<IRemoteServerClientFactory>();
        var bandwidth = new WanBandwidthMonitor(NullLogger<WanBandwidthMonitor>.Instance, clientFactory.Object);
        return new FederationLibraryManager(lm.Object, NullLogger<FederationLibraryManager>.Instance, clientFactory.Object, cache, bandwidth, Mock.Of<MediaBrowser.Controller.Persistence.IMediaStreamRepository>());
    }

    private static FederationSyncService NewSync(FederationItemCache cache)
    {
        var lm = new Mock<ILibraryManager>();
        var clientFactory = new Mock<IRemoteServerClientFactory>();
        var bandwidth = new WanBandwidthMonitor(NullLogger<WanBandwidthMonitor>.Instance, clientFactory.Object);
        var libraryManager = NewManager(cache, lm);
        var persistence = new FederationItemPersistenceService(lm.Object, NullLogger<FederationItemPersistenceService>.Instance, libraryManager, Mock.Of<MediaBrowser.Controller.Persistence.IItemPersistenceService>());
        return new FederationSyncService(NullLogger<FederationSyncService>.Instance, libraryManager, clientFactory.Object, cache, persistence, bandwidth, new Mock<IServiceProvider>().Object, new ExternalCatalogRegistry(Array.Empty<IExternalCatalogProvider>()));
    }

    private static void Seed(FederationItemCache cache, string mapping, string serverId, string imdb)
    {
        var remoteId = Guid.NewGuid();
        cache.UpsertByProviderId(mapping, "imdb", imdb, new BaseItemDto { Id = remoteId, Name = imdb }, serverId, remoteId, 0, "Movie");
    }

    [Fact]
    public void Prune_RemovesAHiddenLinkedServersTitles_KeepsOtherCopiesOfTheSameTitle()
    {
        using var plugin = new RealPluginInstance();
        var vps = LinkedServer();
        vps.Id = "vps";
        vps.LinkCatalog = LinkCatalogDirection.OnlyMineGoes;
        plugin.Configuration.RemoteServers = new List<RemoteServer> { vps, new RemoteServer { Id = "friend", Enabled = true } };
        plugin.Configuration.LibraryMappings = new List<LibraryMapping>
        {
            new()
            {
                LocalLibraryName = "Movies",
                Enabled = true,
                RemoteLibrarySources = new List<RemoteLibrarySource>
                {
                    new() { ServerId = "vps", RemoteLibraryId = "a" },
                    new() { ServerId = "friend", RemoteLibraryId = "b" }
                }
            },
            new()
            {
                // Its only source is the hidden server: still swept.
                LocalLibraryName = "Shows",
                Enabled = true,
                RemoteLibrarySources = new List<RemoteLibrarySource> { new() { ServerId = "vps", RemoteLibraryId = "c" } }
            }
        };

        var cache = NewCache();
        Seed(cache, "Movies", "vps", "tt1");
        Seed(cache, "Movies", "vps", "tt2");
        Seed(cache, "Movies", "friend", "tt2");
        Seed(cache, "Shows", "vps", "tt9");

        var affected = NewSync(cache).PruneUnselectedServerSources();

        Assert.Equal(new[] { "Movies", "Shows" }, affected.OrderBy(n => n));
        var movies = cache.GetEntriesForMapping("Movies").ToList();
        var left = Assert.Single(movies);
        Assert.All(left.GetSourcesSnapshot(), s => Assert.Equal("friend", s.ServerId));
        Assert.Empty(cache.GetEntriesForMapping("Shows"));

        // Both ways again: nothing more is removed (the next sync brings them back).
        vps.LinkCatalog = LinkCatalogDirection.BothWays;
        Seed(cache, "Movies", "vps", "tt3");
        Assert.Empty(NewSync(cache).PruneUnselectedServerSources());
        Assert.Equal(2, cache.GetEntriesForMapping("Movies").Count());
    }

    [Fact]
    public async Task SyncOfAHiddenLinkedServer_FetchesNothing_AndRemovesItsTitles()
    {
        using var plugin = new RealPluginInstance();
        var vps = LinkedServer();
        vps.Id = "vps";
        vps.LinkCatalog = LinkCatalogDirection.OnlyMineGoes;
        plugin.Configuration.RemoteServers = new List<RemoteServer> { vps };
        plugin.Configuration.LibraryMappings = new List<LibraryMapping>
        {
            new()
            {
                LocalLibraryName = "Movies",
                Enabled = true,
                RemoteServerIds = new List<string> { "vps" },
                RemoteLibrarySources = new List<RemoteLibrarySource> { new() { ServerId = "vps", RemoteLibraryId = "a" } }
            }
        };
        var cache = NewCache();
        Seed(cache, "Movies", "vps", "tt1");

        var result = await NewSync(cache).SyncServerAsync("vps", CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.Empty(cache.GetEntriesForMapping("Movies"));
    }

    [Fact]
    public async Task SupportReport_LeavesOutAHiddenLinkedServersCopies()
    {
        using var plugin = new RealPluginInstance();
        var vps = LinkedServer();
        vps.Id = "vps";
        vps.LinkCatalog = LinkCatalogDirection.OnlyMineGoes;
        plugin.Configuration.RemoteServers = new List<RemoteServer> { vps, new RemoteServer { Id = "friend", Name = "Friend", Enabled = true } };
        var cache = NewCache();
        Seed(cache, "Movies", "vps", "tt1");
        Seed(cache, "Movies", "friend", "tt2");
        var lm = new Mock<ILibraryManager>();
        lm.Setup(l => l.GetItemList(It.IsAny<MediaBrowser.Controller.Entities.InternalItemsQuery>())).Returns(new List<MediaBrowser.Controller.Entities.BaseItem>());
        var reports = new PlaySupportReportService(lm.Object, NewManager(cache, lm), NullLogger<PlaySupportReportService>.Instance);
        Task? run = null;
        reports.Run = work => run = Task.Run(work);

        Assert.True(reports.Start());
        await run!;

        Assert.Equal(1, reports.State().Result!.FilesChecked + reports.State().Result!.Undescribed - reports.State().Result!.Undescribed);
    }
}
