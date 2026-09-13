using System.Net;
using System.Net.Http.Json;
using FederationCompanion;

namespace FederationCompanion.Tests;

public sealed class CompanionConnectionTests
{
    private static CompanionState State(string name) => new() { PublicUrl = "https://" + name + ".example", ServerName = name, ServerBaseUrl = "http://local-plex", ServerAccessToken = "private-test-plex" };

    [Fact]
    public async Task RequestAcceptAndPoll_ConnectBothDirections_WithoutChoosingLibraries()
    {
        var a = State("alice"); var b = State("bob");
        using var offline = new HttpClient(new Handler(_ => throw new Exception("Receiving/accepting must not call an offered URL.")));
        var bob = new CompanionConnections(b, offline);
        using var wire = new HttpClient(new Handler(async request =>
        {
            Assert.Equal("bob.example", request.RequestUri!.Host);
            if (request.Method == HttpMethod.Post)
            {
                await bob.ReceiveAsync((await request.Content!.ReadFromJsonAsync<CompanionInvite>())!);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            var id = request.RequestUri.AbsolutePath.Split('/')[4];
            var reply = bob.Result(id, request.Headers.GetValues("X-Companion-Request").Single());
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(reply) };
        }));
        var alice = new CompanionConnections(a, wire);
        await alice.InviteAsync(b.PublicUrl!, default);
        await alice.InviteAsync(b.PublicUrl!, default); // Network retry is idempotent.
        Assert.Single(a.Peers); Assert.Single(b.CompanionRequests); Assert.Empty(b.Peers); Assert.Empty(a.ImportPeers);
        Assert.Null(bob.Result(b.CompanionRequests[0].Id, "wrong-secret"));
        Assert.Null(bob.Result(b.CompanionRequests[0].Id, b.CompanionRequests[0].Secret)!.Offer);
        await alice.PollAsync(default); Assert.True(a.Peers[0].PendingConnection);
        await bob.AcceptAsync(b.CompanionRequests[0].Id);
        await bob.AcceptAsync(b.CompanionRequests[0].Id);
        await alice.PollAsync(default);
        Assert.Single(a.ImportPeers); Assert.Single(b.ImportPeers); Assert.Single(b.Peers);
        foreach (var state in new[] { a, b })
        {
            var imported = state.ImportPeers.Single();
            Assert.Equal("Plex", imported.SourceKind); Assert.True(imported.ReturnSharePending);
            Assert.Empty(imported.SelectedLibraryIds!); Assert.False(state.Peers.Single().PendingConnection);
            Assert.NotEqual("private-test-plex", imported.Token);
            Assert.Equal(state.Peers.Single().Id, imported.CompanionPeerId);
        }
        Assert.Equal(b.Peers[0].AccessToken, a.ImportPeers[0].Token);
        Assert.Equal(a.Peers[0].AccessToken, b.ImportPeers[0].Token);
        await alice.PollAsync(default); Assert.Single(a.ImportPeers);
        b.Peers.Clear(); Assert.Equal("revoked", bob.Result(b.CompanionRequests[0].Id, b.CompanionRequests[0].Secret)!.Status);
    }

    [Fact]
    public async Task IncomingRequest_RejectsChangedIdentityAndPrivateTargets()
    {
        var state = State("bob");
        using var http = new HttpClient(new Handler(_ => throw new Exception("No network expected.")));
        var connections = new CompanionConnections(state, http);
        var invite = new CompanionInvite(Guid.NewGuid().ToString("N"), CompanionSecrets.Create(), new("https://alice.example", CompanionSecrets.Create(), Guid.NewGuid().ToString(), "Alice"));
        await connections.ReceiveAsync(invite);
        await Assert.ThrowsAsync<InvalidOperationException>(() => connections.ReceiveAsync(invite with { Offer = invite.Offer with { Url = "https://different.example" } }));
        await Assert.ThrowsAsync<ArgumentException>(() => connections.ReceiveAsync(invite with { Id = Guid.NewGuid().ToString(), Offer = invite.Offer with { Url = "https://127.0.0.1" } }));
        Assert.Empty(state.ImportPeers);
        await connections.RejectAsync(invite.Id);
        Assert.Equal("declined", connections.Result(invite.Id, invite.Secret)!.Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => connections.AcceptAsync(invite.Id));
        Assert.Empty(state.Peers);
    }

    [Fact]
    public async Task CancelRevokesPendingPeer_AndCannotBeUndoneByPolling()
    {
        var state = State("alice");
        using var wire = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))));
        var connections = new CompanionConnections(state, wire);
        await connections.InviteAsync("https://bob.example", default);
        await connections.CancelAsync(state.CompanionRequests.Single().Id);
        await connections.PollAsync(default);
        Assert.Empty(state.Peers);
        Assert.Empty(state.ImportPeers);
        Assert.Equal("cancelled", state.CompanionRequests.Single().Status);
    }

    [Fact]
    public async Task PlexImportsUseGuardedClient_ForCatalogTokenAndMedia()
    {
        var paths = new List<string>();
        using var ordinary = new HttpClient(new Handler(_ => throw new Exception("Plex must use the public-address guarded client.")));
        using var guarded = new HttpClient(new Handler(request =>
        {
            Assert.DoesNotContain("DirectStream", request.RequestUri!.AbsolutePath);
            paths.Add(request.RequestUri.AbsolutePath);
            var json = request.RequestUri.AbsolutePath.Contains("PlaybackToken") ? "{\"token\":\"short-lived\"}"
                : request.RequestUri.AbsolutePath.Contains("DirectStream") ? "video" : "{\"Items\":[]}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }));
        using var media = new HttpClient(new Handler(request =>
        {
            Assert.Contains("DirectStream", request.RequestUri!.AbsolutePath);
            paths.Add(request.RequestUri.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("video") });
        }));
        var service = new JellyfinImportService(ordinary, ordinary, guarded, media);
        var peer = new JellyfinImportPeer { SourceKind = "Plex", Url = "https://friend.example", Token = "scoped-test-token" };
        await service.GetLibrariesAsync(peer, default);
        await service.GetItemsAsync(peer, "1", "Movie", default);
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.Request.Method = "GET"; context.Response.Body = new MemoryStream();
        await service.RelayStreamAsync(peer, Guid.NewGuid().ToString("N"), context.Request, context.Response, default);
        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal(4, paths.Count);
        Assert.Contains(paths, path => path.Contains("DirectStream"));
    }

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("::ffff:192.168.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("2002:c0a8:0001::1", false)]
    [InlineData("1.1.1.1", true)]
    [InlineData("2606:4700:4700::1111", true)]
    public void CompanionConnection_DnsAddressesMustBePublic(string address, bool expected)
        => Assert.Equal(expected, PublicPeerConnection.IsPublic(IPAddress.Parse(address)));

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> action) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => action(request); }
}
