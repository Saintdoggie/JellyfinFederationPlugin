using System;
using System.Collections.Generic;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Federation.Configuration;
using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

[Collection("PluginInstance")]
public sealed class FederationInheritedRatingTests : IDisposable
{
    private readonly RealPluginInstance _plugin = new();

    public void Dispose() => _plugin.Dispose();

    [Fact]
    public void ReceiverAndSharedPath_InheritSeriesRatingForUnratedEpisode()
    {
        var (cache, _, _, episodeId) = Show();
        var server = new RemoteServer { Id = "server-a" };
        var userId = Guid.NewGuid();
        server.FriendUserAccessRules.Add(new RemoteUserAccessRule
        {
            RemoteUserId = userId.ToString("N"),
            Mode = RemoteUserAccessMode.AllLibraries,
            MaxAllowedRating = "PG"
        });
        var access = new RemoteAccessControlService(NullLogger<RemoteAccessControlService>.Instance, cache);

        Assert.False(access.IsAllowed(server, userId, "TV", episodeId));
        Assert.False(RemoteAccessControlService.IsAllowedForEveryConfiguredUser(server, "TV", episodeId, null, cache));
    }

    [Fact]
    public void Owner_InheritsSeriesRatingThroughJellyfinParents()
    {
        var series = new Series { Id = Guid.NewGuid(), OfficialRating = "TV-MA" };
        var season = new Season { Id = Guid.NewGuid(), ParentId = series.Id };
        var episode = new Episode { Id = Guid.NewGuid(), ParentId = season.Id };
        var library = new Mock<ILibraryManager>();
        library.Setup(l => l.GetItemById(series.Id)).Returns(series);
        library.Setup(l => l.GetItemById(season.Id)).Returns(season);
        library.Setup(l => l.GetItemById(episode.Id)).Returns(episode);
        var friend = new RemoteServer { ShareAllLibraries = true };
        friend.RemoteUserAccessRules.Add(new RemoteUserAccessRule
        {
            RemoteUserId = "viewer",
            Mode = RemoteUserAccessMode.AllLibraries,
            MaxAllowedRating = "PG"
        });

        Assert.False(new FederationPeerAccessService(library.Object).IsItemVisible(friend, "viewer", episode.Id, "library"));
    }

    [Fact]
    public void IncomingFilter_UsesInheritedRating_AndOwnRatingWins()
    {
        var filter = new IncomingContentFilter { MaxAllowedRating = "PG" };
        var episode = new BaseItemDto { Type = BaseItemKind.Episode };

        Assert.False(IncomingContentFilterService.IsAllowedByIncomingFilter(filter, episode, "TV-MA"));
        episode.OfficialRating = "TV-Y";
        Assert.True(IncomingContentFilterService.IsAllowedByIncomingFilter(filter, episode, "TV-MA"));
    }

    private static (FederationItemCache Cache, Guid SeriesId, Guid SeasonId, Guid EpisodeId) Show()
    {
        var cache = new FederationItemCache(NullLogger<FederationItemCache>.Instance);
        var seriesId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var episodeId = Guid.NewGuid();
        var series = cache.UpsertRaw("TV", "server-a", seriesId, new BaseItemDto { Name = "Show", OfficialRating = "TV-MA" }, 0, "Series");
        var season = cache.UpsertRaw("TV", "server-a", seasonId, new BaseItemDto { Name = "Season" }, 0, "Season", parentKey: series.Key);
        cache.UpsertRaw("TV", "server-a", episodeId, new BaseItemDto { Name = "Episode" }, 0, "Episode", parentKey: season.Key);
        return (cache, seriesId, seasonId, episodeId);
    }
}
