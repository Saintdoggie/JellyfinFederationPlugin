using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

/// <summary>
/// Covers <see cref="FederationNowWatchingService"/>: filtering active sessions down
/// to the ones playing a federated item, and reading the source server name back off
/// the "🌐 ServerName" tag <see cref="FederationLibraryManager.AppendServerTag"/>
/// stamps on every materialized federation item.
/// </summary>
public class FederationNowWatchingServiceTests
{
    private readonly Mock<ISessionManager> _sessionManager = new();

    private SessionInfo MakeSession(string id, string userName, BaseItemDto? nowPlaying, PlayerStateInfo? playState = null)
    {
        return new SessionInfo(_sessionManager.Object, NullLogger.Instance)
        {
            Id = id,
            UserName = userName,
            NowPlayingItem = nowPlaying,
            PlayState = playState ?? new PlayerStateInfo()
        };
    }

    [Fact]
    public void GetNowWatching_NoSessions_ReturnsEmpty()
    {
        _sessionManager.Setup(s => s.Sessions).Returns(Array.Empty<SessionInfo>());
        var service = new FederationNowWatchingService(_sessionManager.Object);

        Assert.Empty(service.GetNowWatching());
    }

    [Fact]
    public void GetNowWatching_SessionNotPlayingAnything_Excluded()
    {
        var session = MakeSession("s1", "alice", nowPlaying: null);
        _sessionManager.Setup(s => s.Sessions).Returns(new[] { session });
        var service = new FederationNowWatchingService(_sessionManager.Object);

        Assert.Empty(service.GetNowWatching());
    }

    [Fact]
    public void GetNowWatching_PlayingLocalItem_Excluded()
    {
        var item = new BaseItemDto { Name = "Local Movie", ProviderIds = new Dictionary<string, string>() };
        var session = MakeSession("s1", "alice", item);
        _sessionManager.Setup(s => s.Sessions).Returns(new[] { session });
        var service = new FederationNowWatchingService(_sessionManager.Object);

        Assert.Empty(service.GetNowWatching());
    }

    [Fact]
    public void GetNowWatching_PlayingFederatedItem_IsReported()
    {
        var item = new BaseItemDto
        {
            Name = "Remote Movie",
            ProviderIds = new Dictionary<string, string> { ["FederationKey"] = "Movies/raw/server-1/" + Guid.NewGuid() },
            Tags = new[] { "🌐 Friend's Server" },
            RunTimeTicks = 72000000000
        };
        var playState = new PlayerStateInfo { IsPaused = false, PositionTicks = 1200000000 };
        var session = MakeSession("s1", "alice", item, playState);
        _sessionManager.Setup(s => s.Sessions).Returns(new[] { session });
        var service = new FederationNowWatchingService(_sessionManager.Object);

        var result = service.GetNowWatching();

        var entry = Assert.Single(result);
        Assert.Equal("s1", entry.SessionId);
        Assert.Equal("alice", entry.UserName);
        Assert.Equal("Remote Movie", entry.ItemName);
        Assert.Equal("Friend's Server", entry.ServerName);
        Assert.False(entry.IsPaused);
        Assert.Equal(1200000000, entry.PositionTicks);
        Assert.Equal(72000000000, entry.RuntimeTicks);
    }

    [Fact]
    public void GetNowWatching_Episode_UsesSeriesNamePrefix()
    {
        var item = new BaseItemDto
        {
            Name = "The Pilot",
            SeriesName = "Some Show",
            ProviderIds = new Dictionary<string, string> { ["FederationKey"] = "Episodes/raw/server-1/" + Guid.NewGuid() },
            Tags = new[] { "🌐 Friend's Server" }
        };
        var session = MakeSession("s1", "bob", item);
        _sessionManager.Setup(s => s.Sessions).Returns(new[] { session });
        var service = new FederationNowWatchingService(_sessionManager.Object);

        var entry = Assert.Single(service.GetNowWatching());
        Assert.Equal("Some Show - The Pilot", entry.ItemName);
    }

    [Fact]
    public void GetNowWatching_NoServerTag_FallsBackToUnknownServer()
    {
        var item = new BaseItemDto
        {
            Name = "Remote Movie",
            ProviderIds = new Dictionary<string, string> { ["FederationKey"] = "Movies/raw/server-1/" + Guid.NewGuid() },
            Tags = Array.Empty<string>()
        };
        var session = MakeSession("s1", "alice", item);
        _sessionManager.Setup(s => s.Sessions).Returns(new[] { session });
        var service = new FederationNowWatchingService(_sessionManager.Object);

        var entry = Assert.Single(service.GetNowWatching());
        Assert.Equal("Unknown server", entry.ServerName);
    }
}
