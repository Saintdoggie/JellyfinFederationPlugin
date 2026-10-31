using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
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

#if !FEDERATION_TEST_HOOKS
    [Fact]
    public void ProductionBuild_IgnoresThePrivateAddressTestOverride()
    {
        var previous = Environment.GetEnvironmentVariable(Services.DirectLink.DirectLinkProtocol.TestAllowPrivateEnv);
        try
        {
            Environment.SetEnvironmentVariable(Services.DirectLink.DirectLinkProtocol.TestAllowPrivateEnv, "1");
            Assert.False(Services.DirectLink.DirectLinkProtocol.TestMode);
        }
        finally
        {
            Environment.SetEnvironmentVariable(Services.DirectLink.DirectLinkProtocol.TestAllowPrivateEnv, previous);
        }
    }
#else
    [Fact]
    public void TestHooksBuild_StillRequiresTheExplicitEnvironmentOptIn()
    {
        var previous = Environment.GetEnvironmentVariable(Services.DirectLink.DirectLinkProtocol.TestAllowPrivateEnv);
        try
        {
            Environment.SetEnvironmentVariable(Services.DirectLink.DirectLinkProtocol.TestAllowPrivateEnv, null);
            Assert.False(Services.DirectLink.DirectLinkProtocol.TestMode);
            Environment.SetEnvironmentVariable(Services.DirectLink.DirectLinkProtocol.TestAllowPrivateEnv, "1");
            Assert.True(Services.DirectLink.DirectLinkProtocol.TestMode);
        }
        finally
        {
            Environment.SetEnvironmentVariable(Services.DirectLink.DirectLinkProtocol.TestAllowPrivateEnv, previous);
        }
    }
#endif

    [Fact]
    public void Endpoints_IPv6_AreRefused_AndTestModeAllowsPrivate()
    {
        var own = new List<(IPAddress, int)>();
        Assert.False(PairingEndpoints.IsAcceptable(new IPEndPoint(IPAddress.IPv6Loopback, 47950), own));
        Assert.True(PairingEndpoints.IsAcceptable(new IPEndPoint(IPAddress.Parse("10.231.0.10"), 47950), own, allowPrivate: true));
        Assert.False(PairingEndpoints.IsAcceptable(new IPEndPoint(IPAddress.Parse("127.0.0.1"), 47950), own, allowPrivate: true));
    }
}

/// <summary>Punching between two pairing ports on loopback.</summary>
public class PairPortTests
{
    private static Func<int, TimeSpan?> Fast => round => round == 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(100);

    [Fact]
    public async Task BothSides_FindEachOther()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        using var a = PairPort.Bind(0, 0);
        using var b = PairPort.Bind(0, 1);
        a.SetKey(key, 0);
        b.SetKey(key, 1);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var toB = new[] { new IPEndPoint(IPAddress.Loopback, b.Port) };
        var toA = new[] { new IPEndPoint(IPAddress.Loopback, a.Port) };
        var pa = a.PunchAsync(() => toB, Fast, timeout.Token);
        var pb = b.PunchAsync(() => toA, Fast, timeout.Token);
        Assert.Equal(b.Port, (await pa)!.Port);
        Assert.Equal(a.Port, (await pb)!.Port);
    }

    [Fact]
    public async Task ASideWithoutTheKey_AnswersNothing_UntilItHasIt()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        using var a = PairPort.Bind(0, 0);
        using var b = PairPort.Bind(0, 1);
        b.SetKey(key, 1);
        var toA = new[] { new IPEndPoint(IPAddress.Loopback, a.Port) };
        var toB = new[] { new IPEndPoint(IPAddress.Loopback, b.Port) };
        using var early = new CancellationTokenSource(TimeSpan.FromSeconds(1.5));
        var waitingA = a.PunchAsync(() => toB, Fast, early.Token);
        Assert.Null(await b.PunchAsync(() => toA, Fast, early.Token));
        Assert.Null(await waitingA);

        a.SetKey(key, 0);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pa = a.PunchAsync(() => toB, Fast, timeout.Token);
        Assert.NotNull(await b.PunchAsync(() => toA, Fast, timeout.Token));
        Assert.NotNull(await pa);
    }

    [Fact]
    public async Task PortsBoundBeforeTheirRoleIsKnown_StillPunch()
    {
        // A code's port is bound before the answer says which role this side has.
        var key = RandomNumberGenerator.GetBytes(32);
        using var a = PairPort.Bind(0, 0);
        using var b = PairPort.Bind(0, 0);
        a.SetKey(key, 1);
        b.SetKey(key, 0);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pa = a.PunchAsync(() => new[] { new IPEndPoint(IPAddress.Loopback, b.Port) }, Fast, timeout.Token);
        Assert.NotNull(await b.PunchAsync(() => new[] { new IPEndPoint(IPAddress.Loopback, a.Port) }, Fast, timeout.Token));
        Assert.NotNull(await pa);
    }

    [Fact]
    public async Task AWrongKey_NeverGetsAnAck()
    {
        using var a = PairPort.Bind(0, 0);
        using var b = PairPort.Bind(0, 1);
        a.SetKey(RandomNumberGenerator.GetBytes(32), 0);
        b.SetKey(RandomNumberGenerator.GetBytes(32), 1);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1.5));
        var pa = a.PunchAsync(() => new[] { new IPEndPoint(IPAddress.Loopback, b.Port) }, Fast, timeout.Token);
        Assert.Null(await b.PunchAsync(() => new[] { new IPEndPoint(IPAddress.Loopback, a.Port) }, Fast, timeout.Token));
        Assert.Null(await pa);
    }

    [Fact]
    public async Task JunkAndReplays_GetNoReply()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        using var port = PairPort.Bind(0, 1);
        port.SetKey(key, 1);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var punching = port.PunchAsync(() => Array.Empty<IPEndPoint>(), _ => null, timeout.Token);
        using var attacker = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Dgram, System.Net.Sockets.ProtocolType.Udp);
        attacker.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var target = new IPEndPoint(IPAddress.Loopback, port.Port);
        var probe = PairPunchPacket.Build(PairPunchPacket.Probe, 0, PairPunchPacket.PairTag(key), key, DateTimeOffset.UtcNow);
        attacker.SendTo(new byte[] { 0, 1, 0, 1, 0, 0, 0, 0 }, target);
        attacker.SendTo(PairPunchPacket.Build(PairPunchPacket.Probe, 0, PairPunchPacket.PairTag(key), RandomNumberGenerator.GetBytes(32), DateTimeOffset.UtcNow), target);
        attacker.SendTo(PairPunchPacket.Build(PairPunchPacket.Probe, 0, PairPunchPacket.PairTag(key), PairingCrypto.SubKey(key, "punch"), DateTimeOffset.UtcNow), target);
        var buffer = new byte[2048];
        var reply = attacker.ReceiveAsync(buffer, System.Net.Sockets.SocketFlags.None);
        var first = await Task.WhenAny(reply, Task.Delay(800));
        Assert.Same(reply, first);
        Assert.Equal(PairPunchPacket.Length, await reply);

        // The same probe again is a replay: no second answer.
        var punchKeyProbe = PairPunchPacket.Build(PairPunchPacket.Probe, 0, PairPunchPacket.PairTag(key), PairingCrypto.SubKey(key, "punch"), DateTimeOffset.UtcNow);
        attacker.SendTo(punchKeyProbe, target);
        Assert.Equal(PairPunchPacket.Length, await attacker.ReceiveAsync(buffer, System.Net.Sockets.SocketFlags.None));
        attacker.SendTo(punchKeyProbe, target);
        var again = attacker.ReceiveAsync(buffer, System.Net.Sockets.SocketFlags.None);
        Assert.NotSame(again, await Task.WhenAny(again, Task.Delay(800)));
        _ = probe;
        await punching;
    }

    [Fact]
    public async Task AckFromThirdEndpoint_DoesNotCompletePunch()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        using var victim = PairPort.Bind(0, 0);
        victim.SetKey(key, 0);
        using var expected = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Dgram, System.Net.Sockets.ProtocolType.Udp);
        expected.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        using var attacker = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Dgram, System.Net.Sockets.ProtocolType.Udp);
        attacker.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var expectedEndpoint = (IPEndPoint)expected.LocalEndPoint!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(700));
        var punching = victim.PunchAsync(() => new[] { expectedEndpoint }, Fast, timeout.Token);

        var buffer = new byte[256];
        EndPoint sender = new IPEndPoint(IPAddress.Any, 0);
        var received = await expected.ReceiveFromAsync(buffer, System.Net.Sockets.SocketFlags.None, sender);
        var probe = buffer.AsSpan(0, received.ReceivedBytes).ToArray();
        var ack = PairPunchPacket.Build(
            PairPunchPacket.Ack,
            1,
            PairPunchPacket.PairTag(key),
            PairingCrypto.SubKey(key, "punch"),
            DateTimeOffset.UtcNow,
            PairPort.AckNonce(PairPunchPacket.Nonce(probe)));
        attacker.SendTo(ack, new IPEndPoint(IPAddress.Loopback, victim.Port));

        Assert.Null(await punching);
    }
}
