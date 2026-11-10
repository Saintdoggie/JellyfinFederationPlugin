using Jellyfin.Plugin.Federation.Configuration;
using Jellyfin.Plugin.Federation.Services;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

public sealed class TranscodePolicyTests
{
    [Fact]
    public void DefaultsKeepExistingBehaviour()
    {
        var config = new PluginConfiguration();
        Assert.Equal(LocalTranscodeRestriction.None, config.LocalTranscodeRestriction);
        Assert.True(TranscodePolicy.FriendsMayConvertHere(config));
        Assert.True(TranscodePolicy.MayConvertForOwnViewers(config));
        Assert.True(config.ShowPlaybackServerLabel);
    }

    [Fact]
    public void MissingConfigurationNeverInventsANewPermission()
    {
        // Owner side stays refused without a config (as before); viewer side has
        // no restriction, so a missing config cannot break ordinary playback.
        Assert.False(TranscodePolicy.FriendsMayConvertHere(null));
        Assert.True(TranscodePolicy.MayConvertForOwnViewers(null));
    }

    [Fact]
    public void LegacySwitchStillRefusesFriendsOnItsOwn()
    {
        var config = new PluginConfiguration { EnableRemoteTranscodeForFriends = false };
        Assert.False(TranscodePolicy.FriendsMayConvertHere(config));
        Assert.True(TranscodePolicy.MayConvertForOwnViewers(config));
    }

    [Fact]
    public void BlockFriendsRefusesOnlyFriends()
    {
        var config = new PluginConfiguration { LocalTranscodeRestriction = LocalTranscodeRestriction.BlockFriends };
        Assert.False(TranscodePolicy.FriendsMayConvertHere(config));
        Assert.True(TranscodePolicy.MayConvertForOwnViewers(config));
    }

    [Fact]
    public void BlockAllRefusesEveryone()
    {
        var config = new PluginConfiguration { LocalTranscodeRestriction = LocalTranscodeRestriction.BlockAll };
        Assert.False(TranscodePolicy.FriendsMayConvertHere(config));
        Assert.False(TranscodePolicy.MayConvertForOwnViewers(config));
    }

    [Fact]
    public void SmartPlacementNeverChoosesThisServerUnderBlockAll()
    {
        var config = new PluginConfiguration
        {
            SmartTranscodePlacement = true,
            LocalTranscodeRestriction = LocalTranscodeRestriction.BlockAll
        };
        var decision = TranscodeHardware.Decide(config, null, null, "hevc", true, 2160, false);
        Assert.True(decision.FriendConverts);
        Assert.Contains("never to convert", decision.Reason);
    }

    [Fact]
    public void SmartPlacementStillWorksWithoutTheRestriction()
    {
        var config = new PluginConfiguration { SmartTranscodePlacement = true };
        var decision = TranscodeHardware.Decide(config, null, null, "hevc", true, 2160, false);
        Assert.True(decision.FriendConverts);
        Assert.Contains("hasn't shared its hardware", decision.Reason);
    }
}
