using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;
using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

public class PlexMetadataSourceTests
{
    [Fact]
    public void IdKeys_PrefersTmdbThenImdbThenTvdb_AndIgnoresCaseAndBlanks()
    {
        var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Imdb"] = "TT0133093",
            ["Tmdb"] = "603",
            ["Tvdb"] = " ",
            ["FederationKey"] = "x",
        };

        Assert.Equal(new[] { "tmdb:603", "imdb:tt0133093" }, PlexMetadataSourceService.IdKeys(ids).ToArray());
    }

    [Fact]
    public void IdKeys_NullIds_YieldsNothing() => Assert.Empty(PlexMetadataSourceService.IdKeys(null));

    [Theory]
    [InlineData("The Matrix", 1999, "thematrix|1999")]
    [InlineData("  The  Matrix!  ", 1999, "thematrix|1999")]
    [InlineData("Amélie", null, "amélie|")]
    public void TitleYearKey_IgnoresPunctuationAndSpacing(string title, int? year, string expected)
        => Assert.Equal(expected, PlexMetadataSourceService.TitleYearKey(title, year));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("!!!")]
    public void TitleYearKey_NoUsableTitle_IsNull(string? title)
        => Assert.Null(PlexMetadataSourceService.TitleYearKey(title, 2000));

    [Fact]
    public void ParseServers_KeepsOnlyServers_OwnedFirst_LocalBeforeRemoteBeforeRelay()
    {
        const string json = """
        [
          { "name": "Phone", "provides": "client,player", "clientIdentifier": "p" },
          { "name": "Friend's Plex", "provides": "server", "owned": false, "clientIdentifier": "f", "accessToken": "friend-token",
            "connections": [ { "uri": "https://friend.example", "local": false, "relay": false } ] },
          { "name": "Mine", "provides": "server", "owned": true, "clientIdentifier": "m", "accessToken": "my-token",
            "connections": [
              { "uri": "https://relay.example", "local": false, "relay": true },
              { "uri": "https://wan.example", "local": false, "relay": false },
              { "uri": "http://10.255.255.254:32400", "local": true, "relay": false } ] }
        ]
        """;

        using var doc = JsonDocument.Parse(json);
        var servers = PlexMetadataSourceService.ParseServers(doc.RootElement, "account-token");

        Assert.Equal(new[] { "Mine", "Friend's Plex" }, servers.Select(s => s.Name).ToArray());
        Assert.Equal(
            new[] { "http://10.255.255.254:32400", "https://wan.example", "https://relay.example" },
            servers[0].Connections.Select(c => c.Uri).ToArray());
        Assert.Equal("my-token", servers[0].Token);
    }

    [Fact]
    public void ParseServers_FallsBackToAccountToken_WhenServerHasNone()
    {
        using var doc = JsonDocument.Parse("""[ { "name": "S", "provides": "server", "owned": true, "clientIdentifier": "s", "connections": [] } ]""");
        Assert.Equal("account-token", PlexMetadataSourceService.ParseServers(doc.RootElement, "account-token")[0].Token);
    }

    [Fact]
    public void ParseServers_NotAnArray_IsEmpty()
    {
        using var doc = JsonDocument.Parse("{}");
        Assert.Empty(PlexMetadataSourceService.ParseServers(doc.RootElement, "t"));
    }
}

public class PlexMetadataMatchingTests
{
    private const string Sections = """{"MediaContainer":{"size":2,"Directory":[{"key":"1","title":"Movies","type":"movie"},{"key":"2","title":"Shows","type":"show"}]}}""";

    private const string Movies = """
    {"MediaContainer":{"size":2,"Metadata":[
      {"ratingKey":"101","title":"The Matrix","year":1999,"type":"movie","summary":"Neo wakes.","contentRating":"R","Guid":[{"id":"imdb://tt0133093"},{"id":"tmdb://603"}]},
      {"ratingKey":"102","title":"Heat","year":1995,"type":"movie","summary":"Cops and robbers.","Guid":[]}
    ]}}
    """;

    private const string Shows = """
    {"MediaContainer":{"size":1,"Metadata":[
      {"ratingKey":"201","title":"Severance","year":2022,"type":"show","summary":"Work.","Guid":[{"id":"tvdb://371980"}]}
    ]}}
    """;

    private const string Episodes = """
    {"MediaContainer":{"size":2,"Metadata":[
      {"ratingKey":"301","title":"Good News About Hell","type":"episode","grandparentRatingKey":"201","grandparentTitle":"Severance","parentIndex":1,"index":1,"summary":"First day."},
      {"ratingKey":"302","title":"Half Loop","type":"episode","grandparentRatingKey":"201","grandparentTitle":"Severance","parentIndex":1,"index":2,"summary":"Second."}
    ]}}
    """;

    private sealed class FakePlex : HttpMessageHandler
    {
        public List<string> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var query = request.RequestUri.Query;
            Requests.Add(path + query);
            string? body = path switch
            {
                "/library/sections" => Sections,
                "/library/sections/1/all" => Movies,
                "/library/sections/2/all" when query.Contains("type=2") => Shows,
                "/library/sections/2/all" when query.Contains("type=4") => Episodes,
                "/library/metadata/302" => """{"MediaContainer":{"Metadata":[{"ratingKey":"302","thumb":"/library/metadata/302/thumb/9","parentThumb":"/library/metadata/300/thumb/7","art":"/library/metadata/201/art/3"}]}}""",
                "/library/metadata/101" => """{"MediaContainer":{"Metadata":[{"ratingKey":"101","thumb":"/library/metadata/101/thumb/1","art":"/library/metadata/101/art/2"}]}}""",
                _ => null,
            };

            return Task.FromResult(body == null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private static (PlexMetadataSourceService Service, FakePlex Plex) Build()
    {
        var plex = new FakePlex();
        var settings = new PlexMetadataSourceSettings { Enabled = true, ServerUrl = "http://plex.test:32400", Token = "t" };
        var service = new PlexMetadataSourceService(
            NullLogger<PlexMetadataSourceService>.Instance,
            new Mock<ILibraryManager>().Object,
            new Mock<IProviderManager>().Object,
            new Mock<IFileSystem>().Object)
        {
            HttpClientOverride = new HttpClient(plex),
            SettingsOverride = () => settings,
        };
        return (service, plex);
    }

    private static Dictionary<string, string> Ids(string key, string value) => new(StringComparer.OrdinalIgnoreCase) { [key] = value };

    [Fact]
    public async Task Movie_MatchesByExternalId_EvenWhenTitleDiffers()
    {
        var (service, _) = Build();
        var match = await service.FindMatchAsync(Ids("Tmdb", "603"), "Matrix, The (Open Matte)", 1999, CancellationToken.None);
        Assert.Equal("101", match?.RatingKey);
        Assert.Equal("Neo wakes.", match?.Dto.Overview);
    }

    [Fact]
    public async Task Movie_FallsBackToTitleAndYear_WhenNoIds()
    {
        var (service, _) = Build();
        Assert.Equal("102", (await service.FindMatchAsync(null, "HEAT", 1995, CancellationToken.None))?.RatingKey);
        Assert.Null(await service.FindMatchAsync(null, "Heat", 2024, CancellationToken.None));
    }

    [Fact]
    public async Task Episode_MatchesBySeriesIdSeasonAndNumber()
    {
        var (service, _) = Build();
        var match = await service.FindEpisodeAsync(Ids("Tvdb", "371980"), null, null, 1, 2, CancellationToken.None);
        Assert.Equal("302", match?.RatingKey);
        Assert.Equal("Half Loop", match?.Dto.Name);
        Assert.Null(await service.FindEpisodeAsync(Ids("Tvdb", "371980"), null, null, 1, 9, CancellationToken.None));
    }

    [Fact]
    public async Task Season_MatchesAndUsesSeasonPoster()
    {
        var (service, plex) = Build();
        var match = await service.FindSeasonAsync(null, "Severance", 2022, 1, CancellationToken.None);
        Assert.NotNull(match);
        Assert.Equal(PlexMetadataSourceService.PlexImageKind.Season, match!.ImageKind);

        using var image = await service.GetImageAsync("302", "seasonposter", CancellationToken.None);
        Assert.Null(image); // the fake serves no image bytes, but the lookup must have asked for the season poster
        Assert.Contains(plex.Requests, r => r.StartsWith("/library/metadata/300/thumb/7", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Poster_And_Backdrop_UseTheItemsOwnPaths()
    {
        var (service, plex) = Build();
        (await service.GetImageAsync("101", "poster", CancellationToken.None))?.Dispose();
        (await service.GetImageAsync("101", "backdrop", CancellationToken.None))?.Dispose();
        Assert.Contains(plex.Requests, r => r.StartsWith("/library/metadata/101/thumb/1", StringComparison.Ordinal));
        Assert.Contains(plex.Requests, r => r.StartsWith("/library/metadata/101/art/2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PlexDown_ReturnsNoMatch_InsteadOfThrowing()
    {
        var (service, _) = Build();
        service.HttpClientOverride = new HttpClient(new FailingHandler());
        Assert.Null(await service.FindMatchAsync(Ids("Tmdb", "603"), "The Matrix", 1999, CancellationToken.None));
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("down");
    }
}
