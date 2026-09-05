using System.Net;
using System.Text;
using FederationCompanion;
using Xunit;

namespace FederationCompanion.Tests;

public class PlexClientTests
{
    [Fact]
    public void BuildCreateSectionUrl_UsesModernMovieAgent()
    {
        var url = PlexClient.BuildCreateSectionUrl("http://plex:32400", "Friend Movies", "movie", "/import/abc/Movies");

        Assert.Contains("/library/sections?", url);
        Assert.Contains("type=movie", url);
        Assert.Contains("tv.plex.agents.movie", url);
        Assert.Contains("Plex%20Movie", url);
        Assert.Contains("en-US", url);
        Assert.Contains(Uri.EscapeDataString("/import/abc/Movies"), url);
    }

    [Fact]
    public void BuildCreateSectionUrl_UsesTvAgentForShows()
    {
        var url = PlexClient.BuildCreateSectionUrl("http://plex:32400", "Friend Shows", "show", "/import/abc/Shows");

        Assert.Contains("type=show", url);
        Assert.Contains("tv.plex.agents.series", url);
        Assert.Contains("Plex%20TV%20Series", url);
    }

    [Fact]
    public void MapExportPathForPlex_UsesVisibleRootWhenSet()
    {
        Assert.Equal("/import/peer1", PlexClient.MapExportPathForPlex("/app/imported/peer1", "/import/peer1"));
        Assert.Equal("/app/imported/peer1", PlexClient.MapExportPathForPlex("/app/imported/peer1", null));
    }

    [Theory]
    [InlineData("Beta Horizon", 2021, "Beta Horizon (2021)")]
    [InlineData("Beta Horizon (2021)", 2021, "Beta Horizon (2021)")]
    [InlineData("Alpha Rising", null, "Alpha Rising")]
    public void MovieFolderName_DoesNotDoubleYear(string name, int? year, string expected)
        => Assert.Equal(expected, StrmExporter.MovieFolderName(name, year));

    [Fact]
    public async Task EnsureSectionAsync_ReusesExistingLibraryWithSamePath()
    {
        const string body = """
            {"MediaContainer":{"Directory":[{"key":"7","title":"Old Name","type":"movie","Location":[{"path":"/import/abc/Movies"}]}]}}
            """;
        var plex = new PlexClient(new HttpClient(new ScriptedHandler(body)) { BaseAddress = new Uri("http://plex.example") });

        var key = await plex.EnsureSectionAsync("http://plex.example", "token", "Friend Movies", "movie", "/import/abc/Movies", CancellationToken.None);

        Assert.Equal("7", key);
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly string _body;

        public ScriptedHandler(string body) => _body = body;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            });
    }
}
