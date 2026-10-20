using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Federation.Configuration;
using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

/// <summary>
/// A friend's per-user rule must hide the title from that user (not only refuse to
/// play it), for that user only, and in the same cases playback refuses. These pin
/// the tag decision, the tag order every writer agrees on, and the policy edit.
/// </summary>
[Collection("PluginInstance")]
public class FederationVisibilityServiceTests : IDisposable
{
    private readonly RealPluginInstance _plugin = new();
    private readonly FederationItemCache _cache = new(NullLogger<FederationItemCache>.Instance);
    private readonly Guid _kid = Guid.NewGuid();
    private readonly Guid _parent = Guid.NewGuid();

    public void Dispose() => _plugin.Dispose();

    private FederationVisibilityService Service() => new(NullLogger<FederationVisibilityService>.Instance, _cache);

    private static RemoteServer Friend(string id, params RemoteUserAccessRule[] rules)
        => new() { Id = id, Name = id, Enabled = true, FriendUserAccessRules = rules.ToList() };

    private FederatedCacheEntry Movie(string serverId, Guid remoteId, string rating = "PG")
        => _cache.UpsertRaw("Movies", serverId, remoteId, new BaseItemDto { Name = "Film " + remoteId.ToString("N")[..6], OfficialRating = rating }, 0, "Movie");

    [Fact]
    public void HiddenTag_IsStablePerUser_AndRevealsNothing()
    {
        var tag = FederationVisibilityService.HiddenTagFor(_kid);
        Assert.Equal(tag, FederationVisibilityService.HiddenTagFor(_kid));
        Assert.NotEqual(tag, FederationVisibilityService.HiddenTagFor(_parent));
        Assert.StartsWith(FederationVisibilityService.HiddenTagPrefix, tag);
        Assert.DoesNotContain(_kid.ToString("N"), tag, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(tag.ToLowerInvariant(), tag);
    }

    [Fact]
    public void NoRules_HidesNothing()
    {
        var entry = Movie("a", Guid.NewGuid());
        var snapshot = Service().TakeSnapshot(new PluginConfiguration { RemoteServers = { Friend("a") } });
        Assert.True(snapshot.IsEmpty);
        Assert.Empty(snapshot.HiddenTags(entry));
    }

    [Fact]
    public void BlockedUser_IsHidden_OthersAreNot()
    {
        var entry = Movie("a", Guid.NewGuid());
        var config = new PluginConfiguration
        {
            RemoteServers = { Friend("a", new RemoteUserAccessRule { RemoteUserId = _kid.ToString("N"), Mode = RemoteUserAccessMode.Blocked }) }
        };

        var tags = Service().TakeSnapshot(config).HiddenTags(entry);

        Assert.Equal(new[] { FederationVisibilityService.HiddenTagFor(_kid) }, tags);
    }

    [Fact]
    public void RuleIdInDashedForm_StillMatchesTheUser()
    {
        var entry = Movie("a", Guid.NewGuid());
        var config = new PluginConfiguration
        {
            RemoteServers = { Friend("a", new RemoteUserAccessRule { RemoteUserId = _kid.ToString("D").ToUpperInvariant(), Mode = RemoteUserAccessMode.Blocked }) }
        };

        Assert.Single(Service().TakeSnapshot(config).HiddenTags(entry));
        Assert.False(RemoteAccessControlService.Evaluate(config.RemoteServers[0], _kid, "Movies", entry.GetSourcesSnapshot()[0].RemoteItemId, _cache, out _));
    }

    [Fact]
    public void PerItemBlock_HidesOnlyThatItem()
    {
        var blockedId = Guid.NewGuid();
        var blocked = Movie("a", blockedId);
        var other = Movie("a", Guid.NewGuid());
        var config = new PluginConfiguration
        {
            RemoteServers = { Friend("a", new RemoteUserAccessRule { RemoteUserId = _kid.ToString("N"), BlockedItemIds = { blockedId.ToString("N") } }) }
        };

        var snapshot = Service().TakeSnapshot(config);

        Assert.Single(snapshot.HiddenTags(blocked));
        Assert.Empty(snapshot.HiddenTags(other));
    }

    [Fact]
    public void RatingCeiling_HidesAboveIt()
    {
        var r = Movie("a", Guid.NewGuid(), "R");
        var pg = Movie("a", Guid.NewGuid(), "PG");
        var config = new PluginConfiguration
        {
            RemoteServers = { Friend("a", new RemoteUserAccessRule { RemoteUserId = _kid.ToString("N"), MaxAllowedRating = "PG-13" }) }
        };

        var snapshot = Service().TakeSnapshot(config);

        Assert.Single(snapshot.HiddenTags(r));
        Assert.Empty(snapshot.HiddenTags(pg));
    }

    [Fact]
    public void DownloadOnlyRule_NeverHides()
    {
        var entry = Movie("a", Guid.NewGuid());
        var config = new PluginConfiguration
        {
            RemoteServers = { Friend("a", new RemoteUserAccessRule { RemoteUserId = _kid.ToString("N"), AllowDownload = false }) }
        };

        var snapshot = Service().TakeSnapshot(config);

        Assert.True(snapshot.IsEmpty);
        Assert.Empty(snapshot.HiddenTags(entry));
    }

    [Fact]
    public void SameTitleFromTwoFriends_StaysVisibleWhileOneAllows()
    {
        var entry = Movie("a", Guid.NewGuid());
        entry.AddSource("b", Guid.NewGuid(), 1);
        var block = new RemoteUserAccessRule { RemoteUserId = _kid.ToString("N"), Mode = RemoteUserAccessMode.Blocked };

        var oneBlocks = new PluginConfiguration { RemoteServers = { Friend("a", block), Friend("b") } };
        Assert.Empty(Service().TakeSnapshot(oneBlocks).HiddenTags(entry));

        var bothBlock = new PluginConfiguration { RemoteServers = { Friend("a", block), Friend("b", block) } };
        Assert.Single(Service().TakeSnapshot(bothBlock).HiddenTags(entry));
    }

    [Fact]
    public void DisabledFriendsRules_AreIgnored()
    {
        var entry = Movie("a", Guid.NewGuid());
        var friend = Friend("a", new RemoteUserAccessRule { RemoteUserId = _kid.ToString("N"), Mode = RemoteUserAccessMode.Blocked });
        friend.Enabled = false;

        var snapshot = Service().TakeSnapshot(new PluginConfiguration { RemoteServers = { friend } });

        Assert.Empty(snapshot.HiddenTags(entry));
    }

    [Fact]
    public void SeriesBlock_HidesEpisodesToo()
    {
        var seriesId = Guid.NewGuid();
        var series = _cache.UpsertRaw("TV", "a", seriesId, new BaseItemDto { Name = "Show" }, 0, "Series");
        var season = _cache.UpsertRaw("TV", "a", Guid.NewGuid(), new BaseItemDto { Name = "S1" }, 0, "Season", parentKey: series.Key);
        var episode = _cache.UpsertRaw("TV", "a", Guid.NewGuid(), new BaseItemDto { Name = "E1" }, 0, "Episode", parentKey: season.Key);
        var config = new PluginConfiguration
        {
            RemoteServers = { Friend("a", new RemoteUserAccessRule { RemoteUserId = _kid.ToString("N"), BlockedItemIds = { seriesId.ToString("N") } }) }
        };

        var snapshot = Service().TakeSnapshot(config);

        Assert.Single(snapshot.HiddenTags(series));
        Assert.Single(snapshot.HiddenTags(episode));
    }

    [Fact]
    public void ManyUsers_TagsAreSortedAndOnlyTheRestricted()
    {
        var entry = Movie("a", Guid.NewGuid());
        var third = Guid.NewGuid();
        var config = new PluginConfiguration
        {
            RemoteServers =
            {
                Friend(
                    "a",
                    new RemoteUserAccessRule { RemoteUserId = _kid.ToString("N"), Mode = RemoteUserAccessMode.Blocked },
                    new RemoteUserAccessRule { RemoteUserId = third.ToString("N"), Mode = RemoteUserAccessMode.Blocked },
                    new RemoteUserAccessRule { RemoteUserId = _parent.ToString("N"), Mode = RemoteUserAccessMode.AllLibraries })
            }
        };

        var tags = Service().TakeSnapshot(config).HiddenTags(entry);

        Assert.Equal(2, tags.Count);
        Assert.Equal(tags.OrderBy(t => t, StringComparer.Ordinal), tags);
        Assert.DoesNotContain(FederationVisibilityService.HiddenTagFor(_parent), tags);
    }

    [Fact]
    public void ComposeTags_OneOrderForEveryWriter_AndIdempotent()
    {
        var hide = new[] { FederationVisibilityService.HiddenTagFor(_kid) };
        var composed = FederationLibraryManager.ComposeTags(new[] { "Drama" }, "Alice", hide);

        Assert.Equal(new[] { FederationLibraryManager.FederationTagName, "Drama", "🌐 Alice", hide[0] }, composed);
        Assert.Equal(composed, FederationLibraryManager.ComposeTags(composed, "Alice", hide));

        // The older writer (server-tag restamp) must neither drop nor reorder them.
        Assert.Equal(composed, FederationLibraryManager.AppendServerTag(composed, "Alice"));

        // Clearing the rule removes the tag and nothing else.
        Assert.Equal(new[] { FederationLibraryManager.FederationTagName, "Drama", "🌐 Alice" }, FederationLibraryManager.ComposeTags(composed, "Alice", Array.Empty<string>()));
    }

    [Fact]
    public void DesiredBlockedTags_KeepsAdminTags_AddsOrRemovesOnlyOurs()
    {
        var ours = FederationVisibilityService.HiddenTagFor(_kid);
        var stale = FederationVisibilityService.HiddenTagFor(_parent);

        Assert.Equal(new[] { "gore", ours }, FederationVisibilityService.DesiredBlockedTags(new[] { "gore" }, _kid, restricted: true));
        Assert.Equal(new[] { "gore", ours }, FederationVisibilityService.DesiredBlockedTags(new[] { "gore", stale }, _kid, restricted: true));
        Assert.Equal(new[] { "gore" }, FederationVisibilityService.DesiredBlockedTags(new[] { "gore", ours }, _kid, restricted: false));
        Assert.Empty(FederationVisibilityService.DesiredBlockedTags(Array.Empty<string>(), _kid, restricted: false));
    }

    [Fact]
    public void SameRules_DetectsAnyChange()
    {
        var a = new List<RemoteUserAccessRule> { new() { RemoteUserId = "x", Mode = RemoteUserAccessMode.Blocked } };
        var b = new List<RemoteUserAccessRule> { new() { RemoteUserId = "x", Mode = RemoteUserAccessMode.Blocked } };
        Assert.True(FederationSyncService.SameRules(a, b));
        b[0].BlockedItemIds.Add("y");
        Assert.False(FederationSyncService.SameRules(a, b));
    }
}
