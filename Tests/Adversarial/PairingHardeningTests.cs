using System;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Services.Pairing;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests.Adversarial;

/// <summary>Fixes from the 2026-10-22 security review and adversarial pass on pairing.</summary>
public class PairingHardeningTests
{
    private static Func<int, TimeSpan?> Fast => round => round == 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(100);

    [Fact]
    public async Task AnAckThatAnswersNoProbeSentHere_SetsNoPath()
    {
        // An on-path observer replays a genuine-looking ack (right key, fresh time and
        // nonce) from its own address: it would have become the path.
        var key = RandomNumberGenerator.GetBytes(32);
        using var a = PairPort.Bind(0, 0);
        a.SetKey(key, 0);
        using var attacker = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var silent = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var punch = a.PunchAsync(() => new[] { (IPEndPoint)silent.Client.LocalEndPoint! }, Fast, timeout.Token);

        var ackPunchKey = PairingCrypto.SubKey(key, "punch");
        for (var i = 0; i < 10; i++)
        {
            var ack = PairPunchPacket.Build(PairPunchPacket.Ack, 1, PairPunchPacket.PairTag(key), ackPunchKey, DateTimeOffset.UtcNow);
            await attacker.SendAsync(ack, new IPEndPoint(IPAddress.Loopback, a.Port));
            await Task.Delay(100);
        }

        Assert.Null(await punch);
    }

    [Fact]
    public void AckNonces_AreTiedToOneProbe()
    {
        var probe = RandomNumberGenerator.GetBytes(16);
        Assert.Equal(PairPort.AckNonce(probe), PairPort.AckNonce(probe));
        Assert.NotEqual(PairPort.AckNonce(probe), PairPort.AckNonce(RandomNumberGenerator.GetBytes(16)));
        Assert.NotEqual(probe, PairPort.AckNonce(probe));
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("[::1]", true)]
    [InlineData("localhost", true)]
    [InlineData("203.0.113.9", false)]
    [InlineData("proxy.example", false)]
    [InlineData("media.example.com", false)]
    public void TheLinkSecret_OnlyGoesToThisMachine(string host, bool allowed)
    {
        Assert.Equal(allowed, PairedForwarder.IsThisMachine(host));
    }
}
