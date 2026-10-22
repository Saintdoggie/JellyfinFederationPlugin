using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Services.Pairing;
using Xunit;
using Xunit.Abstractions;

namespace Jellyfin.Plugin.Federation.Tests.Adversarial;

public class PairPortFloodAndGateTests
{
    private readonly ITestOutputHelper _out;

    public PairPortFloodAndGateTests(ITestOutputHelper output) => _out = output;

    /// <summary>Keyless stranger floods junk; the real friend's probes are dropped by the 50/s budget.</summary>
    [Fact]
    public async Task KeylessJunkFlood_StarvesTheRealFriendsProbes()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        using var port = PairPort.Bind(0, 1);
        port.SetKey(key, 1);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var punching = port.PunchAsync(() => Array.Empty<IPEndPoint>(), _ => null, timeout.Token);
        using var stranger = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        using var friend = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        var target = new IPEndPoint(IPAddress.Loopback, port.Port);
        var punchKey = PairingCrypto.SubKey(key, "punch");
        var tag = PairPunchPacket.PairTag(key);
        var junk = new byte[PairPunchPacket.Length];
        var answered = 0;
        for (var i = 0; i < 20; i++)
        {
            for (var j = 0; j < 200; j++) stranger.SendTo(junk, target);   // 200 junk packets in a burst
            var probe = PairPunchPacket.Build(PairPunchPacket.Probe, 0, tag, punchKey, DateTimeOffset.UtcNow);
            friend.SendTo(probe, target);
            var buf = new byte[256];
            var r = friend.ReceiveAsync(buf, SocketFlags.None);
            if (await Task.WhenAny(r, Task.Delay(100)) == r) answered++;
            await Task.Delay(30);
        }

        _out.WriteLine($"valid probes answered: {answered}/20");
        await punching;
        Assert.True(answered >= 15, $"only {answered}/20 genuine probes got an ack while a stranger flooded junk");
    }

    private static PairedRequestGate? Gate(string head) => PairedRequestGate.Parse(Encoding.ASCII.GetBytes(head + " HTTP/1.1\r\nHost: x\r\n\r\n"), true, out _);

    [Theory]
    [InlineData("GET /Plugins/Federation/Peer/SystemInfo/")]
    [InlineData("GET /Plugins/Federation/Peer/SystemInfo.")]
    [InlineData("GET /Plugins/Federation/Peer/SystemInfo;x=1/../Users")]
    [InlineData("GET /Plugins/Federation/Peer/SystemInfo%00")]
    [InlineData("GET /Plugins/Federation/Peer/Pl%61y/t/x")]
    [InlineData("GET /Plugins/Federation/Peer/Play/t/..;/..;/System/Info/Public")]
    [InlineData("GET /Plugins/Federation/Peer/Items/%2E%2E/x")]
    [InlineData("GET /Plugins/Federation/Peer/Items/a%2Fb")]
    [InlineData("GET /Plugins/Federation/Peer/Items/a%5cb")]
    [InlineData("GET /Plugins/Federation/Peer/Items/a\\b")]
    [InlineData("GET /Plugins/Federation/Peer/Items/é")]
    [InlineData("GET *")]
    [InlineData("CONNECT /Plugins/Federation/Peer/SystemInfo")]
    [InlineData("get /Plugins/Federation/Peer/SystemInfo")]
    [InlineData("GET /Plugins/Federation/Peer/SystemInfo#x")]
    [InlineData("OPTIONS /Plugins/Federation/Peer/Play/t/x")]
    [InlineData("GET /Plugins/Federation/Friends/Unfriend")]
    [InlineData("POST /Plugins/Federation/Pairing/Offer")]
    [InlineData("POST /Plugins/Federation/Pairing/Answer")]
    [InlineData("POST /Plugins/Federation/Pairing/abc/Confirm")]
    [InlineData("POST /Plugins/Federation/Settings/Pairing")]
    [InlineData("POST /Plugins/Federation/Pools/Create")]
    [InlineData("GET /Plugins/Federation/Peer/Items/abc/../../../Users")]
    public void PathTricks_AreRefused(string line) => Assert.Null(Gate(line));

    [Fact]
    public void UnderscoreAndCaseVariantsOfLinkHeaders_AreStrippedOrHarmless()
    {
        var gate = PairedRequestGate.Parse(Encoding.ASCII.GetBytes("GET /Plugins/Federation/Peer/SystemInfo HTTP/1.1\r\nx-federation-link: a\r\nX-FEDERATION-LINK-FRIEND: b\r\nx-federation-link-peer: c\r\nX-Federation-Linkx: d\r\n\r\n"), true, out _)!;
        var text = Encoding.ASCII.GetString(gate.BuildForward("h", "/", "f", "1.1.1.1"));
        Assert.DoesNotContain(": a\r\n", text);
        Assert.DoesNotContain(": b\r\n", text);
        Assert.DoesNotContain(": c\r\n", text);
        Assert.DoesNotContain(": d\r\n", text);
    }

    [Fact]
    public void BareCrAndOtherSplitters_InValues()
    {
        // Bare CR (Kestrel rejects it, but the gate should too).
        Assert.Null(PairedRequestGate.Parse(Encoding.ASCII.GetBytes("GET /Plugins/Federation/Peer/SystemInfo HTTP/1.1\r\nX-A: b\rX-Forwarded-For: 1.1.1.1\r\n\r\n"), true, out _));
    }
}
