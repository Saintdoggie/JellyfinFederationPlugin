using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Services.DirectLink;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

/// <summary>The direct link: STUN, punching, request gate, identity, and a real pinned QUIC link on loopback.</summary>
public class DirectLinkTests
{
    private static readonly byte[] Key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    // ---------- STUN ----------
    [Fact]
    public void Stun_Request_IsABindingRequestWithTheMagicCookie()
    {
        var packet = StunClient.BuildRequest(out var txid);
        Assert.Equal(20, packet.Length);
        Assert.Equal(0x0001, BinaryPrimitives.ReadUInt16BigEndian(packet));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(2)));
        Assert.Equal(StunClient.MagicCookie, BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(4)));
        Assert.Equal(txid, packet.AsSpan(8, 12).ToArray());
    }

    [Fact]
    public void Stun_ParsesXorMappedAddress_AndPrefersItOverMappedAddress()
    {
        var txid = new byte[12];
        new Random(1).NextBytes(txid);
        var reply = StunReply(txid, new IPEndPoint(IPAddress.Parse("203.0.113.7"), 41641), legacy: new IPEndPoint(IPAddress.Parse("10.0.0.1"), 1));
        Assert.Equal(new IPEndPoint(IPAddress.Parse("203.0.113.7"), 41641), StunClient.ParseResponse(reply, txid));
    }

    [Fact]
    public void Stun_RejectsWrongTransaction_TruncatedAndNonSuccess()
    {
        var txid = new byte[12];
        var reply = StunReply(txid, new IPEndPoint(IPAddress.Parse("198.51.100.2"), 5000));
        var other = (byte[])txid.Clone();
        other[0] = 9;
        Assert.Null(StunClient.ParseResponse(reply, other));
        Assert.Null(StunClient.ParseResponse(reply.AsSpan(0, reply.Length - 3), txid));
        var error = (byte[])reply.Clone();
        BinaryPrimitives.WriteUInt16BigEndian(error, 0x0111);
        Assert.Null(StunClient.ParseResponse(error, txid));
        var lying = (byte[])reply.Clone();
        BinaryPrimitives.WriteUInt16BigEndian(lying.AsSpan(2), 400);
        Assert.Null(StunClient.ParseResponse(lying, txid));
    }

    [Theory]
    [InlineData("stun.cloudflare.com:3478", "stun.cloudflare.com", 3478)]
    [InlineData("stun:stun.l.google.com:19302", "stun.l.google.com", 19302)]
    [InlineData("10.1.2.3", "10.1.2.3", 3478)]
    public void Stun_ParsesServerNames(string value, string host, int port)
        => Assert.Equal((host, port), StunClient.ParseServer(value));

    [Theory]
    [InlineData("")]
    [InlineData("host:0")]
    [InlineData("host:99999")]
    [InlineData(":3478")]
    public void Stun_RejectsBadServerNames(string value) => Assert.Null(StunClient.ParseServer(value));

    [Fact]
    public async Task Stun_Discover_TellsConeFromSymmetric()
    {
        using var a = FakeStun(mapPort: 40000);
        using var b = FakeStun(mapPort: 40000);
        using var c = FakeStun(mapPort: 40009);
        using var socket = Bound();
        var cone = await StunClient.DiscoverAsync(socket, new[] { a.EndPoint, b.EndPoint }, CancellationToken.None);
        Assert.Equal(NatKind.Cone, cone.Nat);
        Assert.Equal(40000, cone.Public!.Port);
        var symmetric = await StunClient.DiscoverAsync(socket, new[] { a.EndPoint, c.EndPoint }, CancellationToken.None);
        Assert.Equal(NatKind.Symmetric, symmetric.Nat);
        var single = await StunClient.DiscoverAsync(socket, new[] { a.EndPoint }, CancellationToken.None);
        Assert.Equal(NatKind.Unknown, single.Nat);
        Assert.NotNull(single.Public);
    }

    // ---------- Punch packets ----------
    [Fact]
    public void Punch_Packet_RoundTripsAndRejectsEveryTamper()
    {
        var session = Convert.FromHexString(DirectLinkProtocol.NewSessionId());
        var probe = PunchPacket.Build(PunchPacket.Probe, true, session, Key);
        Assert.Equal(PunchPacket.Length, probe.Length);
        Assert.Equal(PunchPacket.Probe, PunchPacket.Verify(probe, true, session, Key));

        // Our own packet reflected back (wrong role), another key, another session.
        Assert.Equal(0, PunchPacket.Verify(probe, false, session, Key));
        Assert.Equal(0, PunchPacket.Verify(probe, true, session, Key.Reverse().ToArray()));
        Assert.Equal(0, PunchPacket.Verify(probe, true, Convert.FromHexString(DirectLinkProtocol.NewSessionId()), Key));
        Assert.Equal(0, PunchPacket.Verify(probe.AsSpan(0, 46), true, session, Key));
        for (var i = 0; i < probe.Length; i++)
        {
            var bad = (byte[])probe.Clone();
            bad[i] ^= 0x40;
            Assert.Equal(0, PunchPacket.Verify(bad, true, session, Key));
        }

        Assert.NotEqual(probe, PunchPacket.Build(PunchPacket.Probe, true, session, Key));
    }

    [Fact]
    public void Punch_Targets_GuessNearbyPortsOnlyForSymmetricPeers()
    {
        var c = new[] { new IPEndPoint(IPAddress.Parse("93.184.216.9"), 50000) };
        Assert.Single(Puncher.Targets(c, false));
        var guessed = Puncher.Targets(c, true);
        Assert.Equal(5, guessed.Count);
        Assert.Contains(guessed, e => e.Port == 50002);
        Assert.Contains(guessed, e => e.Port == 49998);

        // However many candidates a friend sends, the spray stays bounded.
        var many = Enumerable.Range(1, 8).Select(i => new IPEndPoint(IPAddress.Parse("93.184.216." + i), 50000)).ToArray();
        Assert.Equal(Puncher.MaxTargets, Puncher.Targets(many, true).Count);
    }

    [Fact]
    public async Task Punch_BothSidesFindThePath_OnLoopback()
    {
        using var a = Bound();
        using var b = Bound();
        var session = DirectLinkProtocol.NewSessionId();
        var key = DirectLinkProtocol.SessionKey(Key, Key.Reverse().ToArray(), session);
        var initiator = new Puncher(a, true, session, key, new[] { (IPEndPoint)b.LocalEndPoint! }, false);
        var responder = new Puncher(b, false, session, key, new[] { (IPEndPoint)a.LocalEndPoint! }, false);
        var results = await Task.WhenAll(initiator.RunAsync(CancellationToken.None), responder.RunAsync(CancellationToken.None));
        Assert.Equal(((IPEndPoint)b.LocalEndPoint!).Port, results[0].Path!.Port);
        Assert.True(results[0].Confirmed);
        Assert.Equal(((IPEndPoint)a.LocalEndPoint!).Port, results[1].Path!.Port);
    }

    [Fact]
    public async Task Punch_WithAWrongKey_NeverFindsAPath()
    {
        using var a = Bound();
        using var b = Bound();
        var session = DirectLinkProtocol.NewSessionId();
        var initiator = new Puncher(a, true, session, DirectLinkProtocol.SessionKey(Key, Key, session), new[] { (IPEndPoint)b.LocalEndPoint! }, false) { Timeout = TimeSpan.FromSeconds(1.5) };
        var attacker = new Puncher(b, false, session, DirectLinkProtocol.SessionKey(Key, Key.Reverse().ToArray(), session), new[] { (IPEndPoint)a.LocalEndPoint! }, false) { Timeout = TimeSpan.FromSeconds(1.5) };
        var results = await Task.WhenAll(initiator.RunAsync(CancellationToken.None), attacker.RunAsync(CancellationToken.None));
        Assert.Null(results[0].Path);
        Assert.Null(results[1].Path);
        Assert.Contains("no packets", results[0].Failure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Punch_IgnoresGenuinePacketsFromAnUnexpectedAddress()
    {
        using var a = Bound();
        using var b = Bound();
        var session = DirectLinkProtocol.NewSessionId();
        var key = DirectLinkProtocol.SessionKey(Key, Key, session);

        // a only expects packets from 127.0.0.2; b (on 127.0.0.1) sends valid ones.
        var expectingElsewhere = new Puncher(a, true, session, key, new[] { new IPEndPoint(IPAddress.Parse("127.0.0.2"), ((IPEndPoint)b.LocalEndPoint!).Port) }, false) { Timeout = TimeSpan.FromSeconds(1.5) };
        var sender = new Puncher(b, false, session, key, new[] { (IPEndPoint)a.LocalEndPoint! }, false) { Timeout = TimeSpan.FromSeconds(1.5) };
        var results = await Task.WhenAll(expectingElsewhere.RunAsync(CancellationToken.None), sender.RunAsync(CancellationToken.None));
        Assert.Null(results[0].Path);
    }

    // ---------- Candidates and signaling ----------
    [Theory]
    [InlineData("srflx", "93.184.216.5", 41641, false, false, true)]
    [InlineData("srflx", "100.101.102.103", 41641, false, false, false)]
    [InlineData("srflx", "100.101.102.103", 41641, false, true, true)]
    [InlineData("host", "100.64.0.1", 2000, false, true, true)]
    [InlineData("host", "100.64.0.1", 2000, true, false, false)]
    [InlineData("host", "192.168.1.20", 41641, false, false, false)]
    [InlineData("host", "192.168.1.20", 41641, true, false, true)]
    [InlineData("srflx", "10.0.0.5", 41641, false, false, false)]
    [InlineData("srflx", "172.20.0.5", 41641, false, false, false)]
    [InlineData("srflx", "127.0.0.1", 41641, true, true, false)]
    [InlineData("srflx", "0.0.0.0", 41641, true, true, false)]
    [InlineData("srflx", "224.0.0.251", 5353, true, true, false)]
    [InlineData("srflx", "255.255.255.255", 41641, true, true, false)]
    [InlineData("srflx", "169.254.1.2", 41641, true, true, false)]
    [InlineData("srflx", "192.0.0.9", 41641, true, true, false)]
    [InlineData("srflx", "192.0.2.1", 41641, true, true, false)]
    [InlineData("srflx", "198.18.0.1", 41641, true, true, false)]
    [InlineData("srflx", "198.51.100.1", 41641, true, true, false)]
    [InlineData("srflx", "203.0.113.5", 41641, true, true, false)]
    [InlineData("srflx", "93.184.216.5", 53, false, false, false)]
    [InlineData("srflx", "93.184.216.5", 70000, false, false, false)]
    [InlineData("srflx", "::1", 41641, true, true, false)]
    [InlineData("srflx", "2001:db8::1", 41641, false, false, false)]
    [InlineData("srflx", "093.184.216.005", 41641, false, false, false)]
    [InlineData("relay", "93.184.216.5", 41641, false, false, false)]
    [InlineData("srflx", "example.com", 41641, false, false, false)]
    public void Candidates_OnlySafeAddressesAreAccepted(string kind, string address, int port, bool allowPrivate, bool allowTailscale, bool accepted)
        => Assert.Equal(accepted, DirectLinkProtocol.AcceptCandidate(new DirectLinkCandidate { Kind = kind, Address = address, Port = port }, allowPrivate, allowTailscale) != null);

    [Fact]
    public void Candidates_AreCappedAndDeduplicated()
    {
        var many = Enumerable.Range(0, 20).Select(i => new DirectLinkCandidate { Address = "93.184.216." + (i % 3), Port = 5000 }).ToList();
        var accepted = DirectLinkProtocol.AcceptCandidates(many, false);
        Assert.Equal(3, accepted.Count);
        Assert.True(DirectLinkProtocol.AcceptCandidates(Enumerable.Range(0, 20).Select(i => new DirectLinkCandidate { Address = "93.184.216." + i, Port = 5000 }), false).Count <= DirectLinkProtocol.MaxCandidates);
    }

    [Theory]
    [InlineData("https://friend.example.com", true)]
    [InlineData("http://friend.example.com", false)]
    [InlineData("http://100.70.1.2:8096", true)]
    [InlineData("http://box.tail1234.ts.net:8096", true)]
    [InlineData("http://10.255.255.254:8096", false)]
    [InlineData("not a url", false)]
    public void Signaling_NeedsHttpsOrTailscale(string url, bool safe) => Assert.Equal(safe, DirectLinkProtocol.IsSignalingSafe(url));

    [Fact]
    public void Secrets_AndSessionIds_AreStrictlyParsed()
    {
        Assert.NotNull(DirectLinkProtocol.ParseSecret(Convert.ToBase64String(new byte[32])));
        Assert.Null(DirectLinkProtocol.ParseSecret(Convert.ToBase64String(new byte[31])));
        Assert.Null(DirectLinkProtocol.ParseSecret("!!!"));
        Assert.Null(DirectLinkProtocol.ParseSecret(null));
        Assert.True(DirectLinkProtocol.IsSessionId(DirectLinkProtocol.NewSessionId()));
        Assert.False(DirectLinkProtocol.IsSessionId("xyz"));
        Assert.False(DirectLinkProtocol.IsSessionId(new string('g', 32)));
    }

    [Fact]
    public void Proofs_DependOnRoleSessionAndBothFingerprints()
    {
        var k = DirectLinkProtocol.SessionKey(Key, Key.Reverse().ToArray(), "ab");
        var p = DirectLinkProtocol.ConnectProof(k, true, "ab", "f1", "f2");
        Assert.NotEqual(p, DirectLinkProtocol.ConnectProof(k, false, "ab", "f1", "f2"));
        Assert.NotEqual(p, DirectLinkProtocol.ConnectProof(k, true, "ac", "f1", "f2"));
        Assert.NotEqual(p, DirectLinkProtocol.ConnectProof(k, true, "ab", "f2", "f1"));
        Assert.NotEqual(k, DirectLinkProtocol.SessionKey(Key.Reverse().ToArray(), Key, "ab"));
    }

    [Fact]
    public void Backoff_GrowsThenStaysAtAnHour()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), DirectLinkManager.BackoffAfter(1));
        Assert.Equal(TimeSpan.FromMinutes(2), DirectLinkManager.BackoffAfter(2));
        Assert.Equal(TimeSpan.FromMinutes(10), DirectLinkManager.BackoffAfter(3));
        Assert.Equal(TimeSpan.FromMinutes(30), DirectLinkManager.BackoffAfter(4));
        Assert.Equal(TimeSpan.FromHours(1), DirectLinkManager.BackoffAfter(5));
        Assert.Equal(TimeSpan.FromHours(1), DirectLinkManager.BackoffAfter(50));
    }

    // ---------- Request gate ----------
    [Theory]
    [InlineData("GET /Plugins/Federation/Peer/Play/tok/videos/abc/master.m3u8?x=1 HTTP/1.1")]
    [InlineData("HEAD /Plugins/Federation/Peer/Play/tok/videos/abc/stream.mkv HTTP/1.1")]
    [InlineData("GET /Plugins/Federation/DirectStream/abc?token=t&download=true HTTP/1.1")]
    [InlineData("GET /Plugins/Federation/Peer/BitrateTest?size=8000000 HTTP/1.1")]
    public void Gate_AllowsTheFriendMediaRoutes(string requestLine)
        => Assert.NotNull(DirectLinkRequestHead.Parse(Encoding.ASCII.GetBytes(requestLine + "\r\nHost: x\r\nRange: bytes=0-\r\n\r\n"), out _));

    [Theory]
    [InlineData("POST /Plugins/Federation/Peer/Play/tok/x HTTP/1.1\r\nHost: x\r\n\r\n")]
    [InlineData("GET /System/Info HTTP/1.1\r\nHost: x\r\n\r\n")]
    [InlineData("GET /Plugins/Federation/Peer/Items HTTP/1.1\r\nHost: x\r\n\r\n")]
    [InlineData("GET /Plugins/Federation/Config HTTP/1.1\r\nHost: x\r\n\r\n")]
    [InlineData("GET /Plugins/Federation/Peer/Play/ HTTP/1.1\r\nHost: x\r\n\r\n")]
    [InlineData("GET /Plugins/Federation/Peer/Play/../../System/Info HTTP/1.1\r\nHost: x\r\n\r\n")]
    [InlineData("GET /Plugins/Federation/Peer/Play/t/%2e%2e/%2e%2e/Users HTTP/1.1\r\nHost: x\r\n\r\n")]
    [InlineData("GET /Plugins/Federation/Peer/Play/t/a%2fb HTTP/1.1\r\nHost: x\r\n\r\n")]
    [InlineData("GET /Plugins/Federation/Peer/Play/t/a%5Cb HTTP/1.1\r\nHost: x\r\n\r\n")]
    [InlineData("GET /Plugins/Federation/Peer/Play/t\\..\\x HTTP/1.1\r\nHost: x\r\n\r\n")]
    [InlineData("GET //Plugins/Federation/Peer/Play/t/x HTTP/1.1\r\nHost: x\r\n\r\n")]
    [InlineData("GET http://evil/Plugins/Federation/Peer/Play/t/x HTTP/1.1\r\nHost: x\r\n\r\n")]
    [InlineData("GET /Plugins/Federation/Peer/Play/t/x HTTP/1.0\r\nHost: x\r\n\r\n")]
    [InlineData("GET /Plugins/Federation/Peer/Play/t/x  HTTP/1.1\r\nHost: x\r\n\r\n")]
    [InlineData("GET /Plugins/Federation/Peer/Play/t/x HTTP/1.1\r\nTransfer-Encoding: chunked\r\n\r\n")]
    [InlineData("GET /Plugins/Federation/Peer/Play/t/x HTTP/1.1\r\nContent-Length: 5\r\n\r\n")]
    [InlineData("GET /Plugins/Federation/Peer/Play/t/x HTTP/1.1\r\nHost: a\r\nHost: b\r\n\r\n")]
    [InlineData("GET /Plugins/Federation/Peer/Play/t/x HTTP/1.1\r\n Folded: yes\r\n\r\n")]
    [InlineData("GET /Plugins/Federation/Peer/Play/t/x HTTP/1.1\r\nBad Name: v\r\n\r\n")]
    [InlineData("GET /Plugins/Federation/Peer/Play/t/x HTTP/1.1\r\nNoColon\r\n\r\n")]
    [InlineData("GET /Plugins/Federation/Peer/Play/t/x HTTP/1.1\r\nX: a\nInjected: b\r\n\r\n")]
    [InlineData("GET /Plugins/Federation/Peer/Play/t/x#frag HTTP/1.1\r\nHost: x\r\n\r\n")]
    [InlineData("GET /Plugins/Federation/Peer/Play/t/x%00 HTTP/1.1\r\nHost: x\r\n\r\n")]
    [InlineData("GET /Plugins/Federation/Peer/BitrateTestX HTTP/1.1\r\nHost: x\r\n\r\n")]
    [InlineData("GET /Plugins/Federation/Peer/Play/t/x\nInjected:v HTTP/1.1\r\nHost: x\r\n\r\n")]
    [InlineData("GET /Plugins/Federation/Peer/Play/t/x\rInjected:v HTTP/1.1\r\nHost: x\r\n\r\n")]
    [InlineData("GET /Plugins/Federation/Peer/Play/t/x\tz HTTP/1.1\r\nHost: x\r\n\r\n")]
    [InlineData("GET /Plugins/Federation/Peer/Play/t/x HTTP/1.1\r\nHost: x\r\n")]
    public void Gate_RefusesEverythingElse(string head)
        => Assert.Null(DirectLinkRequestHead.Parse(Encoding.ASCII.GetBytes(head), out _));

    [Fact]
    public void Gate_RefusesNonAsciiAndHugeHeads()
    {
        var utf = Encoding.UTF8.GetBytes("GET /Plugins/Federation/Peer/Play/t/é HTTP/1.1\r\nHost: x\r\n\r\n");
        Assert.Null(DirectLinkRequestHead.Parse(utf, out _));
        var huge = Encoding.ASCII.GetBytes("GET /Plugins/Federation/Peer/Play/t/x HTTP/1.1\r\nX: " + new string('a', 17000) + "\r\n\r\n");
        Assert.Null(DirectLinkRequestHead.Parse(huge, out _));
        var many = "GET /Plugins/Federation/Peer/Play/t/x HTTP/1.1\r\n" + string.Concat(Enumerable.Range(0, 101).Select(i => "H" + i + ": v\r\n")) + "\r\n";
        Assert.Null(DirectLinkRequestHead.Parse(Encoding.ASCII.GetBytes(many), out _));
    }

    [Fact]
    public void Gate_ForwardedHead_ReplacesHostAndConnectionAndDropsForwardingHeaders()
    {
        var head = DirectLinkRequestHead.Parse(Encoding.ASCII.GetBytes("GET /Plugins/Federation/Peer/Play/t/x?a=1 HTTP/1.1\r\nHost: evil\r\nConnection: keep-alive\r\nX-Forwarded-For: 1.2.3.4\r\nX-Federation-Direct-Link: 0\r\nRange: bytes=5-\r\nX-Federation-Token: tok\r\n\r\n"), out _)!;
        var forwarded = Encoding.ASCII.GetString(head.BuildForward("127.0.0.1:8096", "/jf"));
        Assert.StartsWith("GET /jf/Plugins/Federation/Peer/Play/t/x?a=1 HTTP/1.1\r\nHost: 127.0.0.1:8096\r\n", forwarded, StringComparison.Ordinal);
        Assert.Contains("Range: bytes=5-\r\n", forwarded, StringComparison.Ordinal);
        Assert.Contains("X-Federation-Token: tok\r\n", forwarded, StringComparison.Ordinal);
        Assert.DoesNotContain("evil", forwarded, StringComparison.Ordinal);
        Assert.DoesNotContain("keep-alive", forwarded, StringComparison.Ordinal);
        Assert.DoesNotContain("1.2.3.4", forwarded, StringComparison.Ordinal);
        Assert.DoesNotContain("X-Federation-Direct-Link: 0", forwarded, StringComparison.Ordinal);
        Assert.EndsWith("Connection: close\r\nX-Federation-Direct-Link: 1\r\n\r\n", forwarded, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("X-Forwarded-Host", true)]
    [InlineData("X-Real-IP", false)]
    [InlineData("X-Client-IP", false)]
    [InlineData("X-Forwarded-For", false)]
    [InlineData("Forwarded", false)]
    [InlineData("CF-Connecting-IP", false)]
    [InlineData("True-Client-IP", false)]
    public void Relay_HomeViewer_UsesJellyfinsLanRules_AndDistrustsLeftoverProxyHeaders(string? header, bool home)
    {
        var network = new Moq.Mock<MediaBrowser.Common.Net.INetworkManager>();
        network.Setup(n => n.IsInLocalNetwork(Moq.It.IsAny<IPAddress>())).Returns(true);
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        http.Connection.RemoteIpAddress = IPAddress.Parse("10.255.255.253");
        if (header != null)
        {
            http.Request.Headers[header] = "1.2.3.4";
        }

        Assert.Equal(home, DirectLinkRelay.IsHomeViewer(http, network.Object));
        Assert.False(DirectLinkRelay.IsHomeViewer(http, null));
        network.Setup(n => n.IsInLocalNetwork(Moq.It.IsAny<IPAddress>())).Returns(false);
        Assert.False(DirectLinkRelay.IsHomeViewer(http, network.Object));
    }

    [Fact]
    public void Relay_MapsFriendUrlsOntoTheLink_DroppingTheFriendsPathBase()
    {
        Assert.Equal("http://federation-direct-link/Plugins/Federation/Peer/Play/t/a.m3u8?x=1", DirectLinkRelay.ToLinkUri("https://friend.example/jf/Plugins/Federation/Peer/Play/t/a.m3u8?x=1").ToString());
        Assert.Equal("/base/Plugins/Federation/DirectLink/abc%20d", DirectLinkRelay.PlayerBase("/base/", "abc d"));
        Assert.True(DirectLinkRelay.IsPlaylist("/x/main.m3u8", null));
        Assert.True(DirectLinkRelay.IsPlaylist("/x/y", "application/vnd.apple.mpegurl"));
        Assert.False(DirectLinkRelay.IsPlaylist("/x/0.ts", "video/mp2t"));
    }

    [Fact]
    public async Task Forwarder_DropsARequestWhoseReaderStopsReading()
    {
        using var web = new TinyHttpServer();
        var forwarder = new DirectLinkForwarder(() => new Uri("http://127.0.0.1:" + web.Port), NullLogger.Instance) { StallTimeout = TimeSpan.FromMilliseconds(300) };
        var stuck = new StuckReaderStream(Encoding.ASCII.GetBytes("GET /Plugins/Federation/DirectStream/x HTTP/1.1\r\nHost: f\r\n\r\n"));
        await forwarder.ServeAsync(stuck, null, "friend").WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(stuck.WriteCancelled);
    }

    // ---------- Identity ----------
    [Fact]
    public void Identity_IsPersisted_OwnerOnly_AndRecreatedWhenCorrupt()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fed-dl-id-" + Guid.NewGuid().ToString("N"));
        try
        {
            Jellyfin.Plugin.Federation.Services.ApiKeyProtector.Initialize(Path.Combine(dir, "keys"));
            var first = DirectLinkIdentity.LoadOrCreate(dir, "abc");
            Assert.True(DirectLinkIdentity.IsFingerprint(first.Fingerprint));
            Assert.True(first.Certificate.HasPrivateKey);
            var again = DirectLinkIdentity.LoadOrCreate(dir, "abc");
            Assert.Equal(first.Fingerprint, again.Fingerprint);
            var file = Path.Combine(dir, DirectLinkIdentity.FileName);
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
            }

            File.WriteAllBytes(file, new byte[] { 1, 2, 3 });
            var fresh = DirectLinkIdentity.LoadOrCreate(dir, "abc");
            Assert.NotEqual(first.Fingerprint, fresh.Fingerprint);
            Assert.True(DirectLinkIdentity.Matches(fresh.Certificate, fresh.Fingerprint));
            Assert.False(DirectLinkIdentity.Matches(fresh.Certificate, first.Fingerprint));
            Assert.False(DirectLinkIdentity.Matches(null, fresh.Fingerprint));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }

    // ---------- The real link ----------
    [Fact]
    public void Quic_LoadsFromTheBundledLibrary_InItsOwnContext()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.True(QuicTransportHost.Available, "QUIC should work here: " + QuicTransportHost.Reason);
        Assert.Contains("2.6.3", QuicTransportHost.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Link_CarriesAllowedRequests_RefusesOthers_AndOutlivesItsListener()
    {
        RequireQuic();
        using var web = new TinyHttpServer();
        var initiator = DirectLinkIdentity.LoadOrCreate(null, "initiator");
        var responder = DirectLinkIdentity.LoadOrCreate(null, "responder");
        var session = DirectLinkProtocol.NewSessionId();
        var key = DirectLinkProtocol.SessionKey(Key, Key.Reverse().ToArray(), session);
        var responderPort = FreeUdpPort();
        var initiatorPort = FreeUdpPort();

        var accept = DirectLinkHandshake.AcceptAsync(responderPort, new IPEndPoint(IPAddress.Loopback, initiatorPort), responder.Certificate, initiator.Fingerprint, key, session, responder.Fingerprint, 16, TimeSpan.FromSeconds(10), CancellationToken.None);
        var client = await DirectLinkHandshake.ConnectAsync(initiatorPort, new IPEndPoint(IPAddress.Loopback, responderPort), initiator.Certificate, responder.Fingerprint, key, session, initiator.Fingerprint, CancellationToken.None);
        var server = await accept;

        var serverLink = new DirectLinkConnection(server, "friend", false, null);
        var token = "the-token";
        await using var clientLink = new DirectLinkConnection(client, "friend", true, () => token);
        clientLink.StartCloseWatch();
        var forwarder = new DirectLinkForwarder(() => new Uri("http://127.0.0.1:" + web.Port), NullLogger.Instance);
        var serving = forwarder.RunAsync(serverLink, "friend");

        for (var i = 0; i < 3; i++)
        {
            using var response = await clientLink.Http!.GetAsync("/Plugins/Federation/Peer/Play/tok/videos/x/seg" + i + ".mp4");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsByteArrayAsync();
            Assert.Equal(TinyHttpServer.BodySize, body.Length);
        }

        Assert.Equal(3, web.Requests.Count);
        Assert.All(web.Requests, r => Assert.Contains("X-Federation-Direct-Link: 1", r, StringComparison.Ordinal));
        Assert.All(web.Requests, r => Assert.Contains("X-Federation-Token: the-token", r, StringComparison.Ordinal));

        // A re-paired friend's new token is used from the next request on.
        token = "new-token";
        using (var again = await clientLink.Http!.GetAsync("/Plugins/Federation/Peer/BitrateTest"))
        {
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        }

        Assert.Contains("X-Federation-Token: new-token", web.Requests[^1], StringComparison.Ordinal);
        Assert.DoesNotContain("the-token", web.Requests[^1], StringComparison.Ordinal);
        Assert.True(clientLink.BytesIn >= 3 * TinyHttpServer.BodySize);
        Assert.True(serverLink.BytesOut >= 3 * TinyHttpServer.BodySize);

        using (var refused = await clientLink.Http!.GetAsync("/System/Info"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }

        using (var post = await clientLink.Http!.PostAsync("/Plugins/Federation/Peer/Play/tok/x", new StringContent("x")))
        {
            Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);
        }

        Assert.Equal(4, web.Requests.Count);

        // Parallel requests share the link.
        var parallel = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => clientLink.Http!.GetByteArrayAsync("/Plugins/Federation/DirectStream/item" + i)));
        Assert.All(parallel, b => Assert.Equal(TinyHttpServer.BodySize, b.Length));

        await QuicTransportHost.Close(client);
        await serving.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(serverLink.IsClosed);
    }

    [Fact]
    public async Task Link_InitiatorNoticesWhenTheFriendClosesIt()
    {
        RequireQuic();
        var initiator = DirectLinkIdentity.LoadOrCreate(null, "initiator");
        var responder = DirectLinkIdentity.LoadOrCreate(null, "responder");
        var session = DirectLinkProtocol.NewSessionId();
        var key = DirectLinkProtocol.SessionKey(Key, Key.Reverse().ToArray(), session);
        var port = FreeUdpPort();
        var clientPort = FreeUdpPort();
        var accept = DirectLinkHandshake.AcceptAsync(port, new IPEndPoint(IPAddress.Loopback, clientPort), responder.Certificate, initiator.Fingerprint, key, session, responder.Fingerprint, 16, TimeSpan.FromSeconds(10), CancellationToken.None);
        var client = await DirectLinkHandshake.ConnectAsync(clientPort, new IPEndPoint(IPAddress.Loopback, port), initiator.Certificate, responder.Fingerprint, key, session, initiator.Fingerprint, CancellationToken.None);
        var server = await accept;
        await using var clientLink = new DirectLinkConnection(client, "friend", true, null);
        clientLink.StartCloseWatch();
        await Task.Delay(300);
        Assert.False(clientLink.IsClosed);

        // The friend restarts or turns the link off: this side must stop using it.
        await QuicTransportHost.Close(server);
        await clientLink.Closed.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(clientLink.IsClosed);
        Assert.False(string.IsNullOrEmpty(clientLink.CloseReason));
    }

    [Fact]
    public async Task Manager_StopsAndDisposesAnyNumberOfTimes_InAnyOrder()
    {
        // Jellyfin's host stops it, then the container disposes it once per
        // registration; none of that may throw (it crashed Jellyfin's shutdown).
        var manager = new DirectLinkManager(NullLogger<DirectLinkManager>.Instance, Moq.Mock.Of<Jellyfin.Plugin.Federation.Services.IRemoteServerClientFactory>());
        await manager.StartAsync(CancellationToken.None);
        await manager.StopAsync(CancellationToken.None);
        await manager.DisposeAsync();
        await manager.DisposeAsync();
        await manager.StopAsync(CancellationToken.None);
        manager.Reconsider("anyone");
    }

    [Fact]
    public void DirectLinks_AreOffUntilTheAdminOptsIn()
    {
        // A direct link shows the friend this home's address; nothing may link by default.
        var config = new Jellyfin.Plugin.Federation.Configuration.PluginConfiguration();
        var friend = new Jellyfin.Plugin.Federation.Configuration.RemoteServer { Enabled = true };
        Assert.False(config.DirectLinkOptIn);
        Assert.Equal(DirectLinkState.Off, DirectLinkManager.Eligibility(friend, config).State);
    }

    [Fact]
    public void DirectLinks_A0195SavedOnSetting_LoadsAsOff()
    {
        // 0.0.195 saved EnableDirectLink=true for everyone; that must not count as opting in.
        var xml = "<?xml version=\"1.0\"?><PluginConfiguration xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\"><EnableDirectLink>true</EnableDirectLink></PluginConfiguration>";
        var serializer = new System.Xml.Serialization.XmlSerializer(typeof(Jellyfin.Plugin.Federation.Configuration.PluginConfiguration));
        var config = (Jellyfin.Plugin.Federation.Configuration.PluginConfiguration)serializer.Deserialize(new StringReader(xml))!;
        Assert.False(config.DirectLinkOptIn);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(25, 20)]
    [InlineData(1, 1)]
    [InlineData(499, 399)]
    [InlineData(1400, null)]
    public void LinkCap_FollowsTheMeasuredLink_NotTheTunnel(double mbps, int? cap)
        => Assert.Equal(cap, Jellyfin.Plugin.Federation.Services.FederationRemoteTranscodeFilter.LinkCap(mbps));

    [Theory]
    [InlineData(0, 12, 12)]
    [InlineData(25, 12, 20)]
    [InlineData(1400, 12, null)]
    public void LinkCap_UnmeasuredKeepsFallback_ButFastLinkCanRemoveIt(double mbps, int? fallback, int? expected)
        => Assert.Equal(expected, Jellyfin.Plugin.Federation.Services.FederationRemoteTranscodeFilter.LinkCap(mbps, fallback));

    [Fact]
    public async Task Link_RefusesAPeerWithTheWrongCertificate()
    {
        RequireQuic();
        var initiator = DirectLinkIdentity.LoadOrCreate(null, "initiator");
        var impostor = DirectLinkIdentity.LoadOrCreate(null, "impostor");
        var responder = DirectLinkIdentity.LoadOrCreate(null, "responder");
        var session = DirectLinkProtocol.NewSessionId();
        var key = DirectLinkProtocol.SessionKey(Key, Key, session);
        var port = FreeUdpPort();
        var clientPort = FreeUdpPort();
        var accept = DirectLinkHandshake.AcceptAsync(port, new IPEndPoint(IPAddress.Loopback, clientPort), responder.Certificate, initiator.Fingerprint, key, session, responder.Fingerprint, 16, TimeSpan.FromSeconds(4), CancellationToken.None);

        // The impostor presents its own certificate where the initiator's is pinned.
        await Assert.ThrowsAnyAsync<Exception>(() => DirectLinkHandshake.ConnectAsync(clientPort, new IPEndPoint(IPAddress.Loopback, port), impostor.Certificate, responder.Fingerprint, key, session, impostor.Fingerprint, CancellationToken.None));
        await Assert.ThrowsAnyAsync<Exception>(() => accept);

        // And the initiator refuses a responder that isn't the one it pinned.
        var port2 = FreeUdpPort();
        var clientPort2 = FreeUdpPort();
        var fake = DirectLinkHandshake.AcceptAsync(port2, new IPEndPoint(IPAddress.Loopback, clientPort2), impostor.Certificate, initiator.Fingerprint, key, session, impostor.Fingerprint, 16, TimeSpan.FromSeconds(4), CancellationToken.None);
        await Assert.ThrowsAnyAsync<Exception>(() => DirectLinkHandshake.ConnectAsync(clientPort2, new IPEndPoint(IPAddress.Loopback, port2), initiator.Certificate, responder.Fingerprint, key, session, initiator.Fingerprint, CancellationToken.None));
        await Assert.ThrowsAnyAsync<Exception>(() => fake);
    }

    [Fact]
    public async Task Link_RefusesTheRightCertificateWithTheWrongSessionKey()
    {
        RequireQuic();
        var initiator = DirectLinkIdentity.LoadOrCreate(null, "initiator");
        var responder = DirectLinkIdentity.LoadOrCreate(null, "responder");
        var session = DirectLinkProtocol.NewSessionId();
        var port = FreeUdpPort();
        var clientPort = FreeUdpPort();
        var accept = DirectLinkHandshake.AcceptAsync(port, new IPEndPoint(IPAddress.Loopback, clientPort), responder.Certificate, initiator.Fingerprint, DirectLinkProtocol.SessionKey(Key, Key, session), session, responder.Fingerprint, 16, TimeSpan.FromSeconds(6), CancellationToken.None);
        await Assert.ThrowsAnyAsync<Exception>(() => DirectLinkHandshake.ConnectAsync(clientPort, new IPEndPoint(IPAddress.Loopback, port), initiator.Certificate, responder.Fingerprint, DirectLinkProtocol.SessionKey(Key.Reverse().ToArray(), Key, session), session, initiator.Fingerprint, CancellationToken.None));
        await Assert.ThrowsAsync<System.Security.Authentication.AuthenticationException>(() => accept);
    }

    [Fact]
    public async Task Link_PunchThenQuic_OnTheSamePorts()
    {
        RequireQuic();
        using var web = new TinyHttpServer();
        var initiator = DirectLinkIdentity.LoadOrCreate(null, "initiator");
        var responder = DirectLinkIdentity.LoadOrCreate(null, "responder");
        var session = DirectLinkProtocol.NewSessionId();
        var key = DirectLinkProtocol.SessionKey(Key, Key.Reverse().ToArray(), session);
        var a = Bound();
        var b = Bound();
        var aPort = ((IPEndPoint)a.LocalEndPoint!).Port;
        var bPort = ((IPEndPoint)b.LocalEndPoint!).Port;

        var responderSide = Task.Run(async () =>
        {
            PunchResult punch;
            using (b)
            {
                punch = await new Puncher(b, false, session, key, new[] { new IPEndPoint(IPAddress.Loopback, aPort) }, false).RunAsync(CancellationToken.None);
            }

            return await DirectLinkHandshake.AcceptAsync(bPort, punch.Path!, responder.Certificate, initiator.Fingerprint, key, session, responder.Fingerprint, 16, TimeSpan.FromSeconds(10), CancellationToken.None);
        });

        PunchResult path;
        using (a)
        {
            path = await new Puncher(a, true, session, key, new[] { new IPEndPoint(IPAddress.Loopback, bPort) }, false).RunAsync(CancellationToken.None);
        }

        await Task.Delay(400);
        var client = await DirectLinkHandshake.ConnectAsync(aPort, path.Path!, initiator.Certificate, responder.Fingerprint, key, session, initiator.Fingerprint, CancellationToken.None);
        var server = await responderSide;
        var serverLink = new DirectLinkConnection(server, "friend", false, null);
        await using var clientLink = new DirectLinkConnection(client, "friend", true, null);
        _ = new DirectLinkForwarder(() => new Uri("http://127.0.0.1:" + web.Port), NullLogger.Instance).RunAsync(serverLink, "friend");
        var bytes = await clientLink.Http!.GetByteArrayAsync("/Plugins/Federation/Peer/BitrateTest?size=10");
        Assert.Equal(TinyHttpServer.BodySize, bytes.Length);
        Assert.Equal(aPort, QuicTransportHost.LocalEndPoint(client).Port);
    }

    private static void RequireQuic()
    {
        if (!QuicTransportHost.Available)
        {
            // Loud on purpose: this machine is expected to run these.
            throw new InvalidOperationException("QUIC unavailable, direct link tests can't run: " + QuicTransportHost.Reason);
        }
    }

    private static Socket Bound()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return socket;
    }

    private static int FreeUdpPort()
    {
        using var socket = Bound();
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    private static byte[] StunReply(byte[] txid, IPEndPoint xorMapped, IPEndPoint? legacy = null)
    {
        var attrs = new List<byte>();
        void Add(ushort type, IPEndPoint ep, bool xor)
        {
            var value = new byte[8];
            value[1] = 1;
            var port = (ushort)ep.Port;
            var addr = ep.Address.GetAddressBytes();
            if (xor)
            {
                port ^= (ushort)(StunClient.MagicCookie >> 16);
                var cookie = new byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(cookie, StunClient.MagicCookie);
                for (var i = 0; i < 4; i++)
                {
                    addr[i] ^= cookie[i];
                }
            }

            BinaryPrimitives.WriteUInt16BigEndian(value.AsSpan(2), port);
            addr.CopyTo(value, 4);
            var header = new byte[4];
            BinaryPrimitives.WriteUInt16BigEndian(header, type);
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2), 8);
            attrs.AddRange(header);
            attrs.AddRange(value);
        }

        if (legacy != null)
        {
            Add(0x0001, legacy, false);
        }

        // An unknown attribute with padding, which must be skipped.
        attrs.AddRange(new byte[] { 0x80, 0x22, 0x00, 0x03, (byte)'a', (byte)'b', (byte)'c', 0 });
        Add(0x0020, xorMapped, true);
        var packet = new byte[20 + attrs.Count];
        BinaryPrimitives.WriteUInt16BigEndian(packet, 0x0101);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)attrs.Count);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), StunClient.MagicCookie);
        txid.CopyTo(packet, 8);
        attrs.CopyTo(packet, 20);
        return packet;
    }

    private static FakeStunServer FakeStun(int mapPort) => new(mapPort);

    /// <summary>Serves a request head, then never lets an answer through (a friend that stopped reading).</summary>
    private sealed class StuckReaderStream : Stream
    {
        private readonly byte[] _request;
        private int _read;

        public StuckReaderStream(byte[] request) => _request = request;

        public bool WriteCancelled { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_read >= _request.Length)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            var n = Math.Min(buffer.Length, _request.Length - _read);
            _request.AsMemory(_read, n).CopyTo(buffer);
            _read += n;
            return n;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                WriteCancelled = true;
                throw;
            }
        }

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class FakeStunServer : IDisposable
    {
        private readonly Socket _socket = Bound();
        private readonly CancellationTokenSource _stop = new();

        public FakeStunServer(int mapPort)
        {
            EndPoint = (IPEndPoint)_socket.LocalEndPoint!;
            _ = Task.Run(async () =>
            {
                var buffer = new byte[1500];
                while (!_stop.IsCancellationRequested)
                {
                    try
                    {
                        var r = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), _stop.Token);
                        var txid = buffer.AsSpan(8, 12).ToArray();
                        await _socket.SendToAsync(StunReply(txid, new IPEndPoint(IPAddress.Parse("203.0.113.50"), mapPort)), SocketFlags.None, r.RemoteEndPoint, _stop.Token);
                    }
                    catch (Exception)
                    {
                        return;
                    }
                }
            });
        }

        public IPEndPoint EndPoint { get; }

        public void Dispose()
        {
            _stop.Cancel();
            _socket.Dispose();
        }
    }

    /// <summary>A minimal HTTP/1.1 server standing in for Jellyfin: answers every request with a fixed body and closes.</summary>
    private sealed class TinyHttpServer : IDisposable
    {
        public const int BodySize = 300_000;

        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();

        public TinyHttpServer()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    TcpClient tcp;
                    try
                    {
                        tcp = await _listener.AcceptTcpClientAsync(_stop.Token);
                    }
                    catch (Exception)
                    {
                        return;
                    }

                    _ = Task.Run(async () =>
                    {
                        using (tcp)
                        {
                            var stream = tcp.GetStream();
                            var buffer = new byte[16384];
                            var filled = 0;
                            while (DirectLinkRequestHead.FindHeadEnd(buffer.AsSpan(0, filled)) < 0)
                            {
                                var n = await stream.ReadAsync(buffer.AsMemory(filled));
                                if (n == 0)
                                {
                                    return;
                                }

                                filled += n;
                            }

                            lock (Requests)
                            {
                                Requests.Add(Encoding.ASCII.GetString(buffer, 0, filled));
                            }

                            var head = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: video/mp4\r\nContent-Length: " + BodySize + "\r\nConnection: close\r\n\r\n");
                            await stream.WriteAsync(head);
                            await stream.WriteAsync(new byte[BodySize]);
                        }
                    });
                }
            });
        }

        public int Port { get; }

        public List<string> Requests { get; } = new();

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
        }
    }
}
