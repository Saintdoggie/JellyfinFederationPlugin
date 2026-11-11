using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;
using Jellyfin.Plugin.Federation.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

public class LinkCodeTests
{
    // RFC 6238 appendix B, SHA-1, last six digits of the eight-digit values.
    [Theory]
    [InlineData(59L, "287082")]
    [InlineData(1111111109L, "081804")]
    [InlineData(1111111111L, "050471")]
    [InlineData(1234567890L, "005924")]
    [InlineData(2000000000L, "279037")]
    public void MatchesTheRfcTestVectors(long unixSeconds, string expected)
    {
        var secret = Encoding.ASCII.GetBytes("12345678901234567890");
        Assert.Equal(expected, LinkCode.At(secret, unixSeconds / LinkCode.StepSeconds));
    }

    [Fact]
    public void AcceptsTheCurrentAndThePreviousCode_Only()
    {
        var secret = LinkCode.NewSecret();
        var now = new DateTime(2026, 11, 11, 4, 0, 10, DateTimeKind.Utc);
        var step = LinkCode.Step(now);

        Assert.True(LinkCode.Matches(secret, LinkCode.At(secret, step), now));
        Assert.True(LinkCode.Matches(secret, LinkCode.At(secret, step - 1), now));
        Assert.False(LinkCode.Matches(secret, LinkCode.At(secret, step - 2), now));
        Assert.False(LinkCode.Matches(secret, LinkCode.At(secret, step + 1), now));
        Assert.True(LinkCode.Matches(secret, LinkCode.At(secret, step).Insert(3, " "), now));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("12a456")]
    [InlineData("١٢٣٤٥٦")]
    public void RejectsAnythingThatIsNotSixDigits(string? typed)
        => Assert.Null(LinkCode.Normalize(typed));

    [Fact]
    public void SecondsLeft_CountsDownWithinAStep()
    {
        Assert.Equal(30, LinkCode.SecondsLeft(new DateTime(2026, 11, 11, 4, 0, 0, DateTimeKind.Utc)));
        Assert.Equal(1, LinkCode.SecondsLeft(new DateTime(2026, 11, 11, 4, 0, 29, DateTimeKind.Utc)));
    }
}

[Collection("PluginInstance")]
public sealed class LinkedServerServiceTests
{
    private static readonly DateTime Now = new(2026, 11, 11, 4, 0, 5, DateTimeKind.Utc);

    private sealed class Handler : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string? Body, string? OwnerKey)> Calls { get; } = new();

        public Func<string, string?, (HttpStatusCode, string)> Answer { get; set; } = (_, _) => (HttpStatusCode.OK, "{\"ok\":true}");

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            request.Headers.TryGetValues(RemoteServerClient.OwnerKeyHeader, out var keys);
            Calls.Add((request.Method, request.RequestUri!.AbsolutePath, body, keys?.FirstOrDefault()));
            var (status, text) = Answer(request.RequestUri!.AbsolutePath, body);
            return new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
        }
    }

    private static RemoteServer Friend(string name, string url = "https://friend.example.com") => new()
    {
        Id = Guid.NewGuid().ToString(),
        Name = name,
        Url = url,
        Kind = ServerKind.Jellyfin,
        Enabled = true,
        ApiKey = FederationTokenAuth.GenerateToken(),
        IssuedApiKey = FederationTokenAuth.GenerateToken(),
        FederationId = Guid.NewGuid().ToString()
    };

    private static (LinkedServerService Service, Handler Handler) NewService()
    {
        var handler = new Handler();
        var factory = new Mock<IRemoteServerClientFactory>();
        factory.Setup(f => f.GetClient(It.IsAny<RemoteServer>()))
            .Returns<RemoteServer>(s => new RemoteServerClient(s, NullLogger.Instance, new HttpClient(handler) { BaseAddress = new Uri(s.Url) }));
        var service = new LinkedServerService(NullLogger<LinkedServerService>.Instance, factory.Object)
        {
            UtcNow = () => Now,
            AfterImport = (_, _) => Task.CompletedTask
        };
        return (service, handler);
    }

    [Theory]
    [InlineData("https://friend.example.com", true)]
    [InlineData("http://friend.example.com", false)]
    [InlineData("http://192.168.1.20:8096", true)]
    [InlineData("http://100.77.1.2:8096", true)]
    [InlineData("http://jellyfin:8096", true)]
    [InlineData("http://media.lan:8096", true)]
    [InlineData("http://203.0.113.9:8096", false)]
    [InlineData("ftp://friend.example.com", false)]
    public void KeysOnlyTravelOverHttpsOrPrivateAddresses(string url, bool ok)
        => Assert.Equal(ok, LinkedServerService.TransportIsPrivate(url));

    [Fact]
    public void OnlyADirectFullyConnectedJellyfinFriendCanBeLinked()
    {
        Assert.Null(LinkedServerService.CannotLink(Friend("A")));
        Assert.NotNull(LinkedServerService.CannotLink(null));
        var plex = Friend("P"); plex.Kind = ServerKind.Plex;
        Assert.NotNull(LinkedServerService.CannotLink(plex));
        var through = Friend("T"); through.ViaLinkedServerId = "x";
        Assert.NotNull(LinkedServerService.CannotLink(through));
        var paired = Friend("Q"); paired.Paired = true;
        Assert.NotNull(LinkedServerService.CannotLink(paired));
        var off = Friend("O"); off.Enabled = false;
        Assert.NotNull(LinkedServerService.CannotLink(off));
        var plain = Friend("H", "http://friend.example.com");
        Assert.Contains("https", LinkedServerService.CannotLink(plain)!);
    }

    [Fact]
    public async Task BothCodesMustBeAccepted_ThenTheLinkIsMadeWithTwoFreshKeys()
    {
        using var plugin = new RealPluginInstance();
        var friend = Friend("B");
        Plugin.Instance!.Configuration.RemoteServers.Add(friend);
        var (service, handler) = NewService();
        handler.Answer = (path, _) => path.EndsWith("/Friends", StringComparison.Ordinal) ? (HttpStatusCode.OK, "[]") : (HttpStatusCode.OK, "{\"ok\":true}");

        // Without clicking Link here first, neither a peer's entry nor ours counts.
        Assert.Equal("not-started", service.PeerVerify(friend, new LinkVerifyBody { Code = "123456", Key = FederationTokenAuth.GenerateToken() }).Error);
        Assert.False((await service.EnterCodeAsync(friend.Id, "123456", CancellationToken.None)).Ok);

        Assert.True((await service.StartAsync(friend.Id, CancellationToken.None)).Ok);
        Assert.Contains(handler.Calls, c => c.Path.EndsWith("/Peer/Linked/Request", StringComparison.Ordinal));
        var status = Assert.Single(service.Status());
        Assert.Matches("^[0-9]{6}$", status.Code);
        Assert.False(status.Linked);

        // Our admin types the friend's code; the friend accepts it.
        var mine = await service.EnterCodeAsync(friend.Id, "654321", CancellationToken.None);
        Assert.True(mine.Ok);
        Assert.False(friend.Linked);
        var sent = handler.Calls.Last(c => c.Path.EndsWith("/Peer/Linked/Verify", StringComparison.Ordinal));
        Assert.Contains("654321", sent.Body);

        // The friend's admin types our code.
        var theirKey = FederationTokenAuth.GenerateToken();
        var answer = service.PeerVerify(friend, new LinkVerifyBody { Code = status.Code, Key = theirKey });
        Assert.True(answer.Ok);
        Assert.True(answer.Linked);
        Assert.True(friend.Linked);
        Assert.Equal(theirKey, friend.LinkKey);
        Assert.Equal(64, friend.IssuedLinkKey.Length);
        Assert.Contains(friend.IssuedLinkKey, sent.Body);
        Assert.NotEqual(friend.ApiKey, friend.IssuedLinkKey);
        Assert.NotEqual(friend.IssuedApiKey, friend.IssuedLinkKey);
        Assert.True(LinkedServerService.IsLinkedCaller(friend, friend.IssuedLinkKey));
        Assert.False(LinkedServerService.IsLinkedCaller(friend, friend.IssuedApiKey));
        Assert.False(LinkedServerService.IsLinkedCaller(friend, null));
    }

    [Fact]
    public async Task FiveWrongCodes_StopTheLink()
    {
        using var plugin = new RealPluginInstance();
        var friend = Friend("B");
        Plugin.Instance!.Configuration.RemoteServers.Add(friend);
        var (service, _) = NewService();
        await service.StartAsync(friend.Id, CancellationToken.None);
        var right = service.Status().Single().Code!;
        var wrong = right == "000000" ? "111111" : "000000";

        for (var i = 1; i < LinkedServerService.MaxAttempts; i++)
        {
            Assert.Equal("wrong-code", service.PeerVerify(friend, new LinkVerifyBody { Code = wrong, Key = FederationTokenAuth.GenerateToken() }).Error);
        }

        Assert.Equal("too-many", service.PeerVerify(friend, new LinkVerifyBody { Code = wrong, Key = FederationTokenAuth.GenerateToken() }).Error);
        Assert.Equal("not-started", service.PeerVerify(friend, new LinkVerifyBody { Code = right, Key = FederationTokenAuth.GenerateToken() }).Error);
        Assert.False(friend.Linked);
    }

    [Fact]
    public async Task AStartedLinkExpires()
    {
        using var plugin = new RealPluginInstance();
        var friend = Friend("B");
        Plugin.Instance!.Configuration.RemoteServers.Add(friend);
        var (service, _) = NewService();
        var clock = Now;
        service.UtcNow = () => clock;
        await service.StartAsync(friend.Id, CancellationToken.None);
        var code = service.Status().Single().Code!;

        clock = Now + LinkedServerService.PendingLifetime + TimeSpan.FromSeconds(1);
        Assert.Equal("not-started", service.PeerVerify(friend, new LinkVerifyBody { Code = code, Key = FederationTokenAuth.GenerateToken() }).Error);
    }

    [Fact]
    public void Export_GivesOwnDirectFriendsAndTheirLibraries_NeverTheCallerChainsOrPairedFriends()
    {
        using var plugin = new RealPluginInstance();
        var config = Plugin.Instance!.Configuration;
        var linked = Friend("Linked"); linked.Linked = true;
        var c = Friend("C");
        var through = Friend("Through"); through.ViaLinkedServerId = linked.Id;
        var paired = Friend("Paired"); paired.Paired = true;
        var off = Friend("Off"); off.Enabled = false;
        var plex = Friend("Plex"); plex.Kind = ServerKind.Plex;
        config.RemoteServers.AddRange(new[] { linked, c, through, paired, off, plex });
        config.LibraryMappings.Add(new LibraryMapping
        {
            LocalLibraryName = "Shows", MediaType = "Series", Enabled = true,
            RemoteLibrarySources = new List<RemoteLibrarySource> { new() { ServerId = c.Id, RemoteLibraryId = "lib-tv", RemoteLibraryName = "TV" } }
        });

        var export = LinkedServerService.Export(linked);

        var only = Assert.Single(export);
        Assert.Equal(c.FederationId, only.FederationId);
        Assert.Equal(c.ApiKey, only.Key);
        Assert.Equal("lib-tv", Assert.Single(only.Libraries!).Id);
        Assert.Equal("Series", only.Libraries![0].MediaType);
    }

    [Fact]
    public async Task Import_AddsUpdatesAndRemovesFriendsThroughTheLink_DirectFriendsWin()
    {
        using var plugin = new RealPluginInstance();
        var config = Plugin.Instance!.Configuration;
        config.LocalFederationId = "self";
        var linked = Friend("Linked"); linked.Linked = true;
        var direct = Friend("Direct");
        config.RemoteServers.AddRange(new[] { linked, direct });
        var (service, _) = NewService();

        var c = new LinkedFriend { FederationId = "fed-c", Name = "C", Url = "https://c.example.com", Key = new string('a', 64), Libraries = new() { new LinkedLibrary { Id = "m1", Name = "Films", MediaType = "Movie" } } };
        var asDirect = new LinkedFriend { FederationId = direct.FederationId, Name = "Direct", Url = direct.Url, Key = new string('b', 64) };
        var self = new LinkedFriend { FederationId = "self", Name = "Me", Url = "https://me.example.com", Key = new string('c', 64) };

        Assert.Equal(1, await service.ApplyAsync(linked, new[] { c, asDirect, self }, CancellationToken.None));
        var row = Assert.Single(config.RemoteServers, s => s.IsThroughLink);
        Assert.Equal(linked.Id, row.ViaLinkedServerId);
        Assert.Equal("fed-c", row.FederationId);
        Assert.Equal(c.Key, row.ApiKey);
        Assert.Empty(row.IssuedApiKey);
        var mapping = Assert.Single(config.LibraryMappings);
        Assert.Equal("Movie", mapping.MediaType);
        Assert.Contains(mapping.RemoteLibrarySources, s => s.ServerId == row.Id && s.RemoteLibraryId == "m1");
        Assert.Equal(direct.ApiKey, config.RemoteServers.Single(s => s.Id == direct.Id).ApiKey);

        // Same list again: nothing changes. A new key: updated in place.
        Assert.Equal(0, await service.ApplyAsync(linked, new[] { c }, CancellationToken.None));
        c.Key = new string('d', 64);
        Assert.Equal(1, await service.ApplyAsync(linked, new[] { c }, CancellationToken.None));
        Assert.Equal(c.Key, config.RemoteServers.Single(s => s.Id == row.Id).ApiKey);

        // Gone from the linked server: gone here, with its library.
        Assert.Equal(1, await service.ApplyAsync(linked, Array.Empty<LinkedFriend>(), CancellationToken.None));
        Assert.DoesNotContain(config.RemoteServers, s => s.IsThroughLink);
        Assert.DoesNotContain(mapping.RemoteLibrarySources, s => s.ServerId == row.Id);
    }

    [Fact]
    public async Task Unlink_TellsTheOtherServerAndDropsTheFriendsThroughIt_ButKeepsTheFriendship()
    {
        using var plugin = new RealPluginInstance();
        var config = Plugin.Instance!.Configuration;
        var linked = Friend("Linked"); linked.Linked = true; linked.LinkKey = new string('e', 64); linked.IssuedLinkKey = new string('f', 64);
        config.RemoteServers.Add(linked);
        var (service, handler) = NewService();
        await service.ApplyAsync(linked, new[] { new LinkedFriend { FederationId = "fed-c", Name = "C", Url = "https://c.example.com", Key = new string('a', 64) } }, CancellationToken.None);

        var (ok, _) = await service.UnlinkAsync(linked.Id, CancellationToken.None);

        Assert.True(ok);
        var call = Assert.Single(handler.Calls, c => c.Path.EndsWith("/Peer/Linked/Remove", StringComparison.Ordinal));
        Assert.Equal(new string('e', 64), call.OwnerKey);
        Assert.False(linked.Linked);
        Assert.Empty(linked.LinkKey);
        Assert.Empty(linked.IssuedLinkKey);
        Assert.Contains(linked, config.RemoteServers);
        Assert.DoesNotContain(config.RemoteServers, s => s.IsThroughLink);
    }

    [Fact]
    public async Task AFriendThroughTheLink_IsSentOnlyConversionControl_NeverFriendshipMessages()
    {
        var handler = new Handler();
        var through = Friend("C"); through.ViaLinkedServerId = "linked";
        var client = new RemoteServerClient(through, NullLogger.Instance, new HttpClient(handler) { BaseAddress = new Uri(through.Url) });

        Assert.Equal(0, await client.PostPeerAsync("Viewers", new { }));
        Assert.Equal(0, await client.PostPeerAsync("Party/Announce", new { }));
        Assert.Equal(0, (await client.PostPeerForJsonAsync<LinkAnswer>("DirectLink/Offer", new { })).Status);
        Assert.False(await client.NotifyCatalogChangedAsync());
        Assert.Equal(0, (await client.SendLinkAsync<LinkAnswer>(HttpMethod.Get, "Friends", null, "x")).Status);
        Assert.Empty(handler.Calls);

        Assert.Equal(200, await client.PostPeerAsync("PlayControl", new { }));
        Assert.Single(handler.Calls);
    }
}
