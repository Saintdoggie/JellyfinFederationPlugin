using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;
using Jellyfin.Plugin.Federation.Services.Pairing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests.Adversarial.Round2;

[Collection("PluginInstance")]
public class Round2PairingTests
{
    /// <summary>Someone who has seen the tag (it is cleartext in every packet) sprays tag-bearing junk from 70 source ports of ONE address.</summary>
    [Fact]
    public async Task OneAddressManyPorts_CannotUseUpTheSourceBudget()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        using var port = PairPort.Bind(0, 1);
        port.SetKey(key, 1);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        var punching = port.PunchAsync(() => Array.Empty<IPEndPoint>(), _ => null, timeout.Token);
        await Task.Delay(50);
        var target = new IPEndPoint(IPAddress.Loopback, port.Port);
        var punchKey = PairingCrypto.SubKey(key, "punch");
        var tag = PairPunchPacket.PairTag(key);
        var junk = PairPunchPacket.Build(PairPunchPacket.Probe, 0, tag, punchKey, DateTimeOffset.UtcNow);
        junk[^1] ^= 0xFF; // right tag, bad MAC
        var sockets = Enumerable.Range(0, 70).Select(_ => new UdpClient(new IPEndPoint(IPAddress.Loopback, 0))).ToList();
        foreach (var s in sockets) s.Send(junk, junk.Length, target);
        using var friend = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        // The friend is another host: a different address from the attacker's.
        friend.Bind(new IPEndPoint(IPAddress.Parse("127.0.0.2"), 0));
        var probe = PairPunchPacket.Build(PairPunchPacket.Probe, 0, tag, punchKey, DateTimeOffset.UtcNow);
        friend.SendTo(probe, target);
        var r = friend.ReceiveAsync(new byte[256], SocketFlags.None);
        var answered = await Task.WhenAny(r, Task.Delay(500)) == r;
        foreach (var s in sockets) s.Dispose();
        timeout.Cancel();
        await punching;
        Assert.True(answered, "the genuine probe got no ack: one address with 64+ source ports used up the per-second source budget");
    }

    [Fact]
    public async Task ManyAddresses_CannotLockOutTheAddressThisPortProbes()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        using var port = PairPort.Bind(0, 1);
        port.SetKey(key, 1);
        using var friend = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        friend.Bind(new IPEndPoint(IPAddress.Parse("127.0.0.200"), 0));
        var friendEp = (IPEndPoint)friend.LocalEndPoint!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        var punching = port.PunchAsync(() => new[] { friendEp }, r => r == 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(10), timeout.Token);
        await Task.Delay(100);
        var target = new IPEndPoint(IPAddress.Loopback, port.Port);
        var punchKey = PairingCrypto.SubKey(key, "punch");
        var tag = PairPunchPacket.PairTag(key);
        var junk = PairPunchPacket.Build(PairPunchPacket.Probe, 0, tag, punchKey, DateTimeOffset.UtcNow);
        junk[^1] ^= 0xFF;
        var sockets = Enumerable.Range(1, 80).Select(i => new UdpClient(new IPEndPoint(IPAddress.Parse("127.0.1." + i), 0))).ToList();
        foreach (var s in sockets) s.Send(junk, junk.Length, target);
        var probe = PairPunchPacket.Build(PairPunchPacket.Probe, 0, tag, punchKey, DateTimeOffset.UtcNow);
        friend.SendTo(probe, target);
        var buffer = new byte[256];
        var answered = false;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!answered && clock.ElapsedMilliseconds < 1500)
        {
            var r = friend.ReceiveAsync(buffer, SocketFlags.None);
            if (await Task.WhenAny(r, Task.Delay(500)) != r)
            {
                break;
            }

            answered = buffer[4] == PairPunchPacket.Ack;
        }

        foreach (var s in sockets) s.Dispose();
        timeout.Cancel();
        await punching;
        Assert.True(answered, "80 other addresses filled the sources and locked out the friend this port probes");
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("::ffff:203.0.113.9")]
    [InlineData("[::ffff:203.0.113.9]")]
    [InlineData("203.0.113.9%eth0")]
    [InlineData("0xCB007109")]
    [InlineData("3405803785")]
    [InlineData("203.0.113.9.")]
    [InlineData("127.0.0.1@203.0.113.9")]
    [InlineData("localhost.evil.example")]
    [InlineData("")]
    [InlineData("localhost%00.evil")]
    public void IsThisMachine_OffMachineForms_AreRefused(string host)
        => Assert.False(PairedForwarder.IsThisMachine(host));

    [Fact]
    public void CopyPairing_CoversEveryPairProperty()
    {
        var copy = typeof(Jellyfin.Plugin.Federation.Api.FederationController).GetMethod("CopyPairing", BindingFlags.NonPublic | BindingFlags.Static)!;
        var props = typeof(RemoteServer).GetProperties().Where(p => p.Name.StartsWith("Pair", StringComparison.Ordinal)).ToList();
        Assert.True(props.Count >= 16);
        var from = new RemoteServer();
        foreach (var p in props)
        {
            object v = p.PropertyType == typeof(bool) ? true
                : p.PropertyType == typeof(int) ? 7
                : p.PropertyType == typeof(long) ? 7L
                : p.PropertyType == typeof(string) ? "x"
                : p.PropertyType == typeof(DateTime?) ? (object)DateTime.UtcNow
                : p.PropertyType == typeof(DateTime) ? DateTime.UtcNow
                : Activator.CreateInstance(p.PropertyType)!;
            if (v is System.Collections.IList l && l is System.Collections.Generic.List<string> ls) ls.Add("x");
            p.SetValue(from, v);
        }

        var to = new RemoteServer();
        copy.Invoke(null, new object[] { from, to });
        foreach (var p in props)
        {
            var a = p.GetValue(from);
            var b = p.GetValue(to);
            Assert.True(Equals(a, b) || (a is System.Collections.Generic.List<string> x && b is System.Collections.Generic.List<string> y && x.SequenceEqual(y)), "CopyPairing misses " + p.Name);
        }
    }

    [Theory]
    [InlineData("12345 67890", "12345 67890", true)]
    [InlineData("12345 67890", "12345 67891", false)]
    [InlineData("12345 67890", "", false)]
    [InlineData("", "", false)]
    [InlineData("12345 67890", "12345 67890 ", false)]
    public void ConfirmSafetyNumber_RequiresTheCurrentExactNumber(string current, string displayed, bool expected)
    {
        var friend = new RemoteServer { PairSafetyNumber = current };
        Assert.Equal(expected, PairingService.TryConfirmSafetyNumber(friend, displayed));
        Assert.Equal(expected, friend.PairSafetyConfirmed);
    }

    /// <summary>OnHello's contract (docs): tokens swap "once both admins confirmed". It hands out this side's token when only this side confirmed.</summary>
    [Fact]
    public void OnHello_RefusesWhileThePeerHasNotConfirmed()
    {
        using var plugin = new RealPluginInstance();
        var cfg = plugin.Configuration;
        cfg.RemoteServers.Add(new RemoteServer { Id = "f1", Paired = true, Enabled = true, PairSafetyConfirmed = true, PairPeerConfirmed = false, Url = "http://x" });
        var svc = new PairingService(NullLogger<PairingService>.Instance);
        var (answer, error) = svc.OnHello("f1", new PairingHello { FederationId = "friend-fed-id-1", Token = new string('a', 64) });
        Assert.True(answer == null, "OnHello issued this server's token although the friend never said its admin confirmed");
    }
}
