using System;
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

    [Fact]
    public void Automatic_BecomesNeverConvert_OnlyOnHardwareThatCannot()
    {
        var before = TranscodePolicy.TestProbe.Value;
        try
        {
            var config = new PluginConfiguration { EnableRemoteTranscodeForFriends = true };
            TranscodePolicy.TestProbe.Value = () => true;
            TranscodePolicy.ResetProbeCache();
            Assert.Equal(LocalTranscodeRestriction.BlockAll, TranscodePolicy.Effective(config));
            Assert.False(TranscodePolicy.MayConvertForOwnViewers(config));
            Assert.False(TranscodePolicy.FriendsMayConvertHere(config));
            Assert.True(TranscodePolicy.BlockedByHardware(config));

            // The admin can insist, and explicit choices are never changed.
            config.LocalTranscodeRestriction = LocalTranscodeRestriction.AlwaysAllow;
            Assert.True(TranscodePolicy.MayConvertForOwnViewers(config));
            Assert.True(TranscodePolicy.FriendsMayConvertHere(config));
            Assert.False(TranscodePolicy.BlockedByHardware(config));
            config.LocalTranscodeRestriction = LocalTranscodeRestriction.BlockFriends;
            Assert.True(TranscodePolicy.MayConvertForOwnViewers(config));

            // A capable server, a failing probe and a missing config change nothing.
            config.LocalTranscodeRestriction = LocalTranscodeRestriction.None;
            TranscodePolicy.TestProbe.Value = () => false;
            TranscodePolicy.ResetProbeCache();
            Assert.True(TranscodePolicy.MayConvertForOwnViewers(config));
            TranscodePolicy.TestProbe.Value = () => throw new InvalidOperationException();
            TranscodePolicy.ResetProbeCache();
            Assert.True(TranscodePolicy.MayConvertForOwnViewers(config));
            TranscodePolicy.TestProbe.Value = () => true;
            TranscodePolicy.ResetProbeCache();
            Assert.True(TranscodePolicy.MayConvertForOwnViewers(null));
        }
        finally
        {
            TranscodePolicy.TestProbe.Value = before;
            TranscodePolicy.ResetProbeCache();
        }
    }

    [Fact]
    public void TooWeak_MeansNoHardwareEncoderAndFewCpuThreads()
    {
        Assert.True(TranscodeHardware.TooWeakToConvert(new TranscodeHardwareInfo { CpuThreads = 2 }));
        Assert.True(TranscodeHardware.TooWeakToConvert(new TranscodeHardwareInfo { CpuThreads = 3 }));
        Assert.False(TranscodeHardware.TooWeakToConvert(new TranscodeHardwareInfo { CpuThreads = 4 }));
        Assert.False(TranscodeHardware.TooWeakToConvert(new TranscodeHardwareInfo { CpuThreads = 2, HardwareEncode = { "h264" } }));
        Assert.False(TranscodeHardware.TooWeakToConvert(new TranscodeHardwareInfo { CpuThreads = 2, Offloaded = true }));
        Assert.False(TranscodeHardware.TooWeakToConvert(new TranscodeHardwareInfo { CpuThreads = 0 }));
        Assert.False(TranscodeHardware.TooWeakToConvert(null));
    }

    [Fact]
    public void Describe_SaysWhatThisServerConvertsAndWhy()
    {
        var config = new PluginConfiguration();
        Assert.Equal("allowed (automatic)", TranscodePolicy.Describe(config, weak: false, cpuThreads: 8));

        var weak = TranscodePolicy.Describe(config, weak: true, cpuThreads: 2);
        Assert.StartsWith("nothing, automatically: no hardware encoder and 2 CPU threads (fewer than 4).", weak);
        Assert.Contains("Friends still convert", weak);

        config.UseFriendsTranscoders = false;
        Assert.DoesNotContain("Friends still convert", TranscodePolicy.Describe(config, weak: true, cpuThreads: 2));

        config.LocalTranscodeRestriction = LocalTranscodeRestriction.AlwaysAllow;
        Assert.StartsWith("everything", TranscodePolicy.Describe(config, weak: true, cpuThreads: 2));
        config.LocalTranscodeRestriction = LocalTranscodeRestriction.BlockFriends;
        Assert.Equal("for its own viewers only (set in settings)", TranscodePolicy.Describe(config, weak: true, cpuThreads: 2));
        config.LocalTranscodeRestriction = LocalTranscodeRestriction.BlockAll;
        Assert.StartsWith("nothing (set in settings)", TranscodePolicy.Describe(config, weak: false, cpuThreads: 8));
    }
}
