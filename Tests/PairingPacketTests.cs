using System;
using System.Collections.Generic;
using System.Net;
using System.Security.Cryptography;
using Jellyfin.Plugin.Federation.Services.Pairing;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

/// <summary>Punch packets, the replay window and endpoint checks for paired friends.</summary>
public class PairingPacketTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_790_000_000);

    [Fact]
    public void Probe_FromThePeer_IsAccepted_Once()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var tag = PairPunchPacket.PairTag(key);
        var packet = PairPunchPacket.Build(PairPunchPacket.Probe, 0, tag, key, Now);
        var replay = new ReplayWindow();
        Assert.Equal(PairPunchPacket.Length, packet.Length);
        Assert.Equal(tag, PairPunchPacket.PeekPairTag(packet));
        Assert.Equal(PairPunchPacket.Probe, PairPunchPacket.Verify(packet, 1, key, Now, replay));
        Assert.Equal(0, PairPunchPacket.Verify(packet, 1, key, Now, replay));
    }

    [Fact]
    public void OwnPacketsReflectedBack_AreRefused()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var packet = PairPunchPacket.Build(PairPunchPacket.Probe, 1, PairPunchPacket.PairTag(key), key, Now);
        Assert.Equal(0, PairPunchPacket.Verify(packet, 1, key, Now, new ReplayWindow()));
    }

    [Fact]
    public void WrongKeyChangedBytesAndOldPackets_AreRefused()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var packet = PairPunchPacket.Build(PairPunchPacket.Ack, 0, PairPunchPacket.PairTag(key), key, Now);
        Assert.Equal(0, PairPunchPacket.Verify(packet, 1, RandomNumberGenerator.GetBytes(32), Now, new ReplayWindow()));
        for (var i = 0; i < packet.Length; i++)
        {
            var changed = (byte[])packet.Clone();
            changed[i] ^= 0x01;
            Assert.Equal(0, PairPunchPacket.Verify(changed, 1, key, Now, new ReplayWindow()));
        }

        Assert.Equal(0, PairPunchPacket.Verify(packet, 1, key, Now + TimeSpan.FromSeconds(121), new ReplayWindow()));
        Assert.Equal(0, PairPunchPacket.Verify(packet, 1, key, Now - TimeSpan.FromSeconds(121), new ReplayWindow()));
        Assert.Equal(PairPunchPacket.Ack, PairPunchPacket.Verify(packet, 1, key, Now + TimeSpan.FromSeconds(119), new ReplayWindow()));
        Assert.Equal(0, PairPunchPacket.Verify(packet.AsSpan(0, packet.Length - 1), 1, key, Now, new ReplayWindow()));
        Assert.Null(PairPunchPacket.PeekPairTag(new byte[10]));
    }

    [Fact]
    public void ReplayWindow_ForgetsOnlyAfterItsTime_AndStaysBounded()
    {
        var window = new ReplayWindow(TimeSpan.FromMinutes(5), capacity: 4);
        var a = RandomNumberGenerator.GetBytes(16);
        Assert.True(window.TryAdd(a, Now));
        Assert.False(window.TryAdd(a, Now + TimeSpan.FromMinutes(4)));
        Assert.True(window.TryAdd(a, Now + TimeSpan.FromMinutes(6)));
        for (var i = 0; i < 100; i++)
        {
            window.TryAdd(RandomNumberGenerator.GetBytes(16), Now + TimeSpan.FromMinutes(6));
        }
    }

    [Theory]
    [InlineData("8.8.8.8", 47950, true)]
    [InlineData("93.184.216.34", 40220, true)]
    [InlineData("8.8.8.8", 53, false)]
    [InlineData("8.8.8.8", 1023, false)]
    [InlineData("127.0.0.1", 47950, false)]
    [InlineData("0.0.0.0", 47950, false)]
    [InlineData("10.0.0.5", 47950, false)]
    [InlineData("172.20.1.1", 47950, false)]
    [InlineData("192.168.7.9", 47950, false)]
    [InlineData("100.100.1.1", 47950, false)]
    [InlineData("169.254.1.1", 47950, false)]
    [InlineData("224.0.0.251", 5353, false)]
    [InlineData("239.255.255.250", 1900, false)]
    [InlineData("255.255.255.255", 47950, false)]
    [InlineData("203.0.113.5", 47950, false)]
    [InlineData("198.18.0.1", 47950, false)]
    [InlineData("192.168.1.20", 47950, true)]
    [InlineData("192.168.1.10", 47950, false)]
    [InlineData("192.168.2.20", 47950, false)]
    public void Endpoints_FromACode_AreChecked(string address, int port, bool allowed)
    {
        var own = new List<(IPAddress, int)> { (IPAddress.Parse("192.168.1.10"), 24) };
        Assert.Equal(allowed, PairingEndpoints.IsAcceptable(new IPEndPoint(IPAddress.Parse(address), port), own));
    }

    [Fact]
    public void Endpoints_IPv6_AreRefused_AndTestModeAllowsPrivate()
    {
        var own = new List<(IPAddress, int)>();
        Assert.False(PairingEndpoints.IsAcceptable(new IPEndPoint(IPAddress.IPv6Loopback, 47950), own));
        Assert.True(PairingEndpoints.IsAcceptable(new IPEndPoint(IPAddress.Parse("10.231.0.10"), 47950), own, allowPrivate: true));
        Assert.False(PairingEndpoints.IsAcceptable(new IPEndPoint(IPAddress.Parse("127.0.0.1"), 47950), own, allowPrivate: true));
    }
}
