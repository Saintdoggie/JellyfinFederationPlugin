using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Federation.Configuration;
using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

/// <summary>
/// Switching a server off clears the stream URL stamped on its items, so they
/// stop playing rather than continuing to stream from a server the admin just
/// disabled. These tests pin the part that is easy to get wrong: deciding
/// *which* source that verdict is based on. Sources are ordered by Priority and
/// never re-ordered by enabled state, so reading only the primary would strip
/// the Play button off deduped titles that another, still-enabled server serves
/// perfectly well - defeating the redundancy dedup exists to provide.
/// </summary>
public class FederationDisabledSourceTests
{
    private static FederatedCacheEntry EntryWithSources(params (string ServerId, int Priority)[] sources)
    {
        var cache = new FederationItemCache(NullLogger<FederationItemCache>.Instance);
        FederatedCacheEntry? entry = null;
        foreach (var (serverId, priority) in sources)
        {
            var remoteId = Guid.NewGuid();
            entry = cache.UpsertByProviderId(
                "Movies",
                "imdb",
                "tt1234567",
                new BaseItemDto { Id = remoteId, Name = "Shared Movie" },
                serverId,
                remoteId,
                priority,
                "Movie");
        }

        Assert.NotNull(entry);
        return entry!;
    }

    private static PluginConfiguration ConfigWith(params (string Id, bool Enabled)[] servers)
    {
        var config = new PluginConfiguration { RemoteServers = new List<RemoteServer>() };
        foreach (var (id, enabled) in servers)
        {
            config.RemoteServers.Add(new RemoteServer { Id = id, Name = id, Url = "http://" + id + ".local", Enabled = enabled });
        }

        return config;
    }

    [Fact]
    public void AllServersEnabled_PicksTheHighestPrioritySource()
    {
        var entry = EntryWithSources(("a", 0), ("b", 1));
        var config = ConfigWith(("a", true), ("b", true));

        var picked = FederationItemPersistenceService.FirstEnabledSource(entry, config);

        Assert.NotNull(picked);
        Assert.Equal("a", picked!.ServerId);
    }

    /// <summary>
    /// The regression this helper was extracted for: a deduped title whose
    /// preferred server is switched off is still served by the other one, so it
    /// must keep a stamped path and therefore its Play button.
    /// </summary>
    [Fact]
    public void PrimaryServerDisabled_FallsBackToTheStillEnabledSource()
    {
        var entry = EntryWithSources(("a", 0), ("b", 1));
        var config = ConfigWith(("a", false), ("b", true));

        var picked = FederationItemPersistenceService.FirstEnabledSource(entry, config);

        Assert.NotNull(picked);
        Assert.Equal("b", picked!.ServerId);
    }

    [Fact]
    public void EveryServerDisabled_ReturnsNull_SoTheStampedPathIsCleared()
    {
        var entry = EntryWithSources(("a", 0), ("b", 1));
        var config = ConfigWith(("a", false), ("b", false));

        Assert.Null(FederationItemPersistenceService.FirstEnabledSource(entry, config));
    }

    /// <summary>
    /// A source whose server was deleted outright is as unusable as a disabled
    /// one, and must not keep a title playable on its own.
    /// </summary>
    [Fact]
    public void SourceWhoseServerNoLongerExists_IsNotTreatedAsPlayable()
    {
        var entry = EntryWithSources(("a", 0));
        var config = ConfigWith(("someone-else", true));

        Assert.Null(FederationItemPersistenceService.FirstEnabledSource(entry, config));
    }

    [Fact]
    public void SingleEnabledServer_IsPicked()
    {
        var entry = EntryWithSources(("a", 0));
        var config = ConfigWith(("a", true));

        var picked = FederationItemPersistenceService.FirstEnabledSource(entry, config);

        Assert.NotNull(picked);
        Assert.Equal("a", picked!.ServerId);
    }
}
