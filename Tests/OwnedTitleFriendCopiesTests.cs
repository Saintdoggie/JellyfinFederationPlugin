using System;
using System.Linq;
using Jellyfin.Plugin.Federation.Configuration;
using Jellyfin.Plugin.Federation.Services;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

public class OwnedTitleFriendCopiesTests
{
    [Fact]
    public void Wanted_FollowsTheAutomaticChoice_AndNeedsMerging()
    {
        Assert.False(OwnedTitleFriendCopies.Wanted(null));
        Assert.False(OwnedTitleFriendCopies.Wanted(new PluginConfiguration { LocalTranscodeRestriction = LocalTranscodeRestriction.AlwaysAllow }));
        Assert.True(OwnedTitleFriendCopies.Wanted(new PluginConfiguration { SmartSourceSelection = true, LocalTranscodeRestriction = LocalTranscodeRestriction.AlwaysAllow }));
        Assert.True(OwnedTitleFriendCopies.Wanted(new PluginConfiguration { LocalTranscodeRestriction = LocalTranscodeRestriction.BlockAll }));
        Assert.False(OwnedTitleFriendCopies.Wanted(new PluginConfiguration { SmartSourceSelection = true, EnableDedup = false }));
    }

    [Theory]
    [InlineData(PlayFit.Unknown, false)]
    [InlineData(PlayFit.Direct, false)]
    [InlineData(PlayFit.AudioOnly, true)]
    [InlineData(PlayFit.Convert, true)]
    internal void OwnFile_NeedsHelpOnlyWhenKnownNotToPlayAsStored(PlayFit fit, bool expected)
        => Assert.Equal(expected, OwnedTitleFriendCopies.OwnFileNeedsHelp(new DevicePlayFitResult(fit, null, "test")));

    [Fact]
    public void Offers_ArePerViewerDeviceAndTitle_AndExpire()
    {
        var now = new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
        var memory = new OwnedTitleFriendCopies { UtcNow = () => now };
        var user = Guid.NewGuid();
        var item = Guid.NewGuid();
        Assert.True(memory.IsEmpty);

        memory.Remember(user, "tv", item, new[] { "ABC" });
        Assert.False(memory.IsEmpty);
        Assert.Contains("abc", memory.Offered(user, "tv", item)!);
        Assert.Null(memory.Offered(user, "phone", item));
        Assert.Null(memory.Offered(Guid.NewGuid(), "tv", item));
        Assert.Null(memory.Offered(user, "tv", Guid.NewGuid()));

        now += OwnedTitleFriendCopies.ForgetAfter + TimeSpan.FromSeconds(1);
        Assert.Null(memory.Offered(user, "tv", item));

        memory.Remember(user, "tv", item, new[] { "abc" });
        memory.Forget(user, "tv", item);
        Assert.Null(memory.Offered(user, "tv", item));
        Assert.True(memory.IsEmpty);
    }

    [Fact]
    public void Order_FriendCopyFirstWhenNoOwnCopyPlays_AndNeverLeftForConversion()
    {
        var own = new MediaBrowser.Model.Dto.MediaSourceInfo { Id = "own", SupportsDirectPlay = false, SupportsDirectStream = false, SupportsTranscoding = true };
        var friend = new MediaBrowser.Model.Dto.MediaSourceInfo { Id = "FRIEND", SupportsDirectPlay = false, SupportsDirectStream = true };
        var friendUnplayable = new MediaBrowser.Model.Dto.MediaSourceInfo { Id = "other", SupportsDirectPlay = false, SupportsDirectStream = false };
        var offered = new[] { "friend", "other" };

        // The friend copy that plays goes first; one that would need converting is dropped.
        Assert.Equal(new[] { "FRIEND", "own" }, OwnedTitleFriendCopies.Order(new[] { own, friendUnplayable, friend }, offered).Select(s => s.Id));

        var ownPlays = new MediaBrowser.Model.Dto.MediaSourceInfo { Id = "own", SupportsDirectPlay = true };
        var unchanged = new[] { ownPlays, friend };
        Assert.Same(unchanged, OwnedTitleFriendCopies.Order(unchanged, offered));
        Assert.Equal(new[] { "own" }, OwnedTitleFriendCopies.Order(new[] { own, friendUnplayable }, offered).Select(s => s.Id));
        var notOffered = new[] { own, friend };
        Assert.Same(notOffered, OwnedTitleFriendCopies.Order(notOffered, Array.Empty<string>()));
    }
}
