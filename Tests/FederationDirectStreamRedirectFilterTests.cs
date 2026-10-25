using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

/// <summary>
/// Native apps (Findroid and similar) direct-play by asking this server for
/// /Videos/{id}/stream?static=true, which Jellyfin serves for an Http source by
/// relaying the friend's file over this server's upload. These pin the redirect
/// that sends such a request straight to the friend's server, and every case where
/// it must not happen.
/// </summary>
public class FederationDirectStreamRedirectFilterTests
{
    private const string PeerUrl = "https://peer.example/Plugins/Federation/DirectStream/abc?token=item-tok";

    private static MediaSourceInfo Direct(string path = PeerUrl) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Path = path,
        Protocol = MediaProtocol.Http,
        IsRemote = true,
    };

    [Fact]
    public void RedirectTarget_PeerDirectStreamSameScheme_IsThePath()
        => Assert.Equal(PeerUrl, FederationDirectStreamRedirectFilter.RedirectTarget(Direct(), "https"));

    [Fact]
    public void RedirectTarget_NeverForLoopbackProxyOrOtherSources()
    {
        Assert.Null(FederationDirectStreamRedirectFilter.RedirectTarget(null, "https"));
        Assert.Null(FederationDirectStreamRedirectFilter.RedirectTarget(
            new MediaSourceInfo { Path = "http://127.0.0.1:8096/Plugins/Federation/Stream?x", Protocol = MediaProtocol.Http, IsRemote = false }, "http"));
        var notRemote = Direct();
        notRemote.IsRemote = false;
        Assert.Null(FederationDirectStreamRedirectFilter.RedirectTarget(notRemote, "https"));
        Assert.Null(FederationDirectStreamRedirectFilter.RedirectTarget(Direct("https://cdn.example/movie.mkv"), "https"));
        var file = Direct();
        file.Protocol = MediaProtocol.File;
        Assert.Null(FederationDirectStreamRedirectFilter.RedirectTarget(file, "https"));
    }

    [Fact]
    public void RedirectTarget_SchemeChange_IsRefused()
    {
        // ExoPlayer does not follow cross-protocol redirects by default.
        Assert.Null(FederationDirectStreamRedirectFilter.RedirectTarget(Direct(), "http"));
        Assert.Null(FederationDirectStreamRedirectFilter.RedirectTarget(Direct("http://peer.example/Plugins/Federation/DirectStream/abc?token=t"), "https"));
    }

    [Fact]
    public void RequestScheme_UsesTrustedProxyMiddlewareResult_NotRawHeaders()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "http";
        context.Request.Headers["X-Forwarded-Proto"] = "https";
        Assert.Equal("http", FederationDirectStreamRedirectFilter.RequestScheme(context.Request));
        context.Request.Scheme = "https";
        context.Request.Headers["X-Forwarded-Proto"] = "http";
        Assert.Equal("https", FederationDirectStreamRedirectFilter.RequestScheme(context.Request));
    }

    [Fact]
    public async Task AuthenticatedStaticRequest_ForFederatedDirectSource_Redirects()
    {
        var harness = new Harness();

        var (result, nextCalled) = await harness.RunAsync();

        Assert.False(nextCalled);
        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Equal(PeerUrl, redirect.Url);
        Assert.False(redirect.Permanent);
        Assert.Equal("private, no-store", harness.Response.Headers.CacheControl.ToString());
        Assert.Equal("no-referrer", harness.Response.Headers["Referrer-Policy"].ToString());
    }

    [Fact]
    public async Task AnonymousRequest_IsLeftToJellyfin()
    {
        var harness = new Harness { Authenticated = false };

        var (result, nextCalled) = await harness.RunAsync();

        Assert.True(nextCalled);
        Assert.Null(result);
        harness.MediaSources.Verify(m => m.GetPlaybackMediaSources(It.IsAny<BaseItem>(), It.IsAny<User>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UserWhoCannotSeeTheItem_IsLeftToJellyfin()
    {
        var harness = new Harness { UserCanSeeItem = false };

        Assert.True((await harness.RunAsync()).NextCalled);
    }

    [Fact]
    public async Task TranscodeOrLiveStreamOrOtherAction_IsLeftToJellyfin()
    {
        Assert.True((await new Harness { Static = false }.RunAsync()).NextCalled);
        Assert.True((await new Harness { LiveStreamId = "live" }.RunAsync()).NextCalled);
        Assert.True((await new Harness { Action = "GetAdditionalPart" }.RunAsync()).NextCalled);
    }

    [Fact]
    public async Task LocalItem_IsLeftToJellyfin()
        => Assert.True((await new Harness { Federated = false }.RunAsync()).NextCalled);

    [Fact]
    public async Task RequestedSourceId_PicksThatSource()
    {
        var harness = new Harness();
        var proxied = new MediaSourceInfo { Id = "proxied", Path = "http://127.0.0.1:8096/Plugins/Federation/Stream?x", Protocol = MediaProtocol.Http };
        harness.Sources.Add(proxied);
        harness.MediaSourceId = "proxied";

        Assert.True((await harness.RunAsync()).NextCalled);
    }

    private sealed class Harness
    {
        public bool Authenticated { get; set; } = true;

        public bool UserCanSeeItem { get; set; } = true;

        public bool Federated { get; set; } = true;

        public bool Static { get; set; } = true;

        public string? LiveStreamId { get; set; }

        public string? MediaSourceId { get; set; }

        public string Action { get; set; } = "GetVideoStream";

        public List<MediaSourceInfo> Sources { get; } = new() { Direct() };

        public Mock<IMediaSourceManager> MediaSources { get; } = new();

        public HttpResponse Response { get; private set; } = null!;

        public async Task<(IActionResult? Result, bool NextCalled)> RunAsync()
        {
            var item = new Movie { Id = Guid.NewGuid() };
            if (Federated)
            {
                item.ProviderIds = new Dictionary<string, string> { ["FederationKey"] = "Movies/imdb/tt1" };
            }

            var user = new User("alice", "auth", "reset");
            var library = new Mock<ILibraryManager>();
            library.Setup(l => l.GetItemById(item.Id)).Returns(item);
            library.Setup(l => l.GetItemById<BaseItem>(item.Id, It.IsAny<User>())).Returns(UserCanSeeItem ? item : null);

            var authorization = new Mock<IAuthorizationContext>();
            var info = new AuthorizationInfo { IsAuthenticated = Authenticated };
            if (Authenticated)
            {
                info.User = user;
            }

            authorization.Setup(a => a.GetAuthorizationInfo(It.IsAny<HttpContext>())).ReturnsAsync(info);
            MediaSources.Setup(m => m.GetPlaybackMediaSources(item, user, false, false, It.IsAny<CancellationToken>())).ReturnsAsync(Sources);

            var filter = new FederationDirectStreamRedirectFilter(
                authorization.Object,
                Mock.Of<IUserManager>(),
                library.Object,
                MediaSources.Object,
                NullLogger<FederationDirectStreamRedirectFilter>.Instance);

            var http = new DefaultHttpContext();
            Response = http.Response;
            http.Request.Scheme = "https";
            var descriptor = new ActionDescriptor
            {
                RouteValues = new Dictionary<string, string?> { ["controller"] = "Videos", ["action"] = Action }
            };
            var arguments = new Dictionary<string, object?>
            {
                ["itemId"] = item.Id,
                ["static"] = Static,
                ["liveStreamId"] = LiveStreamId,
                ["mediaSourceId"] = MediaSourceId,
            };
            var context = new ActionExecutingContext(
                new ActionContext(http, new RouteData(), descriptor),
                new List<IFilterMetadata>(),
                arguments,
                new object());

            var nextCalled = false;
            await filter.OnActionExecutionAsync(context, () =>
            {
                nextCalled = true;
                return Task.FromResult(new ActionExecutedContext(context, new List<IFilterMetadata>(), new object()));
            });

            return (context.Result, nextCalled);
        }
    }
}
