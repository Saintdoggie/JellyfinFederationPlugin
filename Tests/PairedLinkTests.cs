using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Services;
using Jellyfin.Plugin.Federation.Services.DirectLink;
using Jellyfin.Plugin.Federation.Services.Pairing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

/// <summary>The paired-link gate, forwarder and QUIC handshake (docs/PAIRING.md).</summary>
public class PairedLinkTests
{
    private static PairedRequestGate? Gate(string head, bool confirmed = true)
        => PairedRequestGate.Parse(Encoding.ASCII.GetBytes(head), confirmed, out _);

    [Theory]
    [InlineData("GET /Plugins/Federation/Peer/SystemInfo")]
    [InlineData("GET /Plugins/Federation/Peer/Items?StartIndex=0&Limit=100")]
    [InlineData("GET /Plugins/Federation/Peer/Items/0123abcd")]
    [InlineData("GET /Plugins/Federation/Peer/PlaybackInfo/0123abcd")]
    [InlineData("GET /Plugins/Federation/Peer/Play/tok123/videos/abc/master.m3u8?x=1")]
    [InlineData("HEAD /Plugins/Federation/Peer/Play/tok123/videos/abc/seg1.mp4")]
    [InlineData("GET /Plugins/Federation/Peer/Images/0123abcd/Primary?token=t")]
    [InlineData("GET /Plugins/Federation/Peer/Images/0123abcd/Backdrop/2?token=t")]
    [InlineData("GET /Plugins/Federation/DirectStream/0123abcd?token=t")]
    [InlineData("POST /Plugins/Federation/PlaybackToken")]
    [InlineData("POST /Plugins/Federation/Peer/Party/0123-abcd/Event")]
    [InlineData("POST /Plugins/Federation/Pools/Notice")]
    [InlineData("POST /Plugins/Federation/Pairing/Peer/Hello")]
    public void AllowedRoutes_Pass(string line)
        => Assert.NotNull(Gate(line + " HTTP/1.1\r\nHost: x\r\n\r\n"));

    [Theory]
    [InlineData("GET /System/Info")]
    [InlineData("GET /System/Info/Public")]
    [InlineData("GET /Users")]
    [InlineData("GET /web/index.html")]
    [InlineData("GET /Plugins/Federation/Configuration")]
    [InlineData("POST /Plugins/Federation/Configuration")]
    [InlineData("GET /Plugins/Federation/Stream?x=1")]
    [InlineData("GET /Plugins/Federation/HandoffPlaylist?u=http://x")]
    [InlineData("POST /Plugins/Federation/Friends/Request")]
    [InlineData("POST /Plugins/Federation/Friends/Accept")]
    [InlineData("GET /Plugins/Federation/Friends/List")]
    [InlineData("POST /Plugins/Federation/Peer/DirectLink/Offer")]
    [InlineData("POST /Plugins/Federation/Friends/abc/Sharing")]
    [InlineData("GET /Plugins/Federation/Peer/Items/../../System/Info")]
    [InlineData("GET /Plugins/Federation/Peer/Items/%2e%2e")]
    [InlineData("GET /Plugins/Federation//Peer/SystemInfo")]
    [InlineData("GET /Plugins/Federation/Peer/SystemInfoX")]
    [InlineData("GET /Plugins/Federation/Peer/Play/tok")]
    [InlineData("DELETE /Plugins/Federation/Peer/SystemInfo")]
    [InlineData("POST /Plugins/Federation/Peer/SystemInfo")]
    [InlineData("GET http://evil/Plugins/Federation/Peer/SystemInfo")]
    [InlineData("GET /Plugins/Federation/Pairing/Peer/Hello")]
    public void OtherRoutes_AreRefused(string line)
        => Assert.Null(Gate(line + " HTTP/1.1\r\nHost: x\r\n\r\n"));

    [Fact]
    public void BeforeTheSafetyCheck_OnlyThePairingRoutesPass()
    {
        Assert.Null(Gate("GET /Plugins/Federation/Peer/SystemInfo HTTP/1.1\r\n\r\n", confirmed: false));
        Assert.Null(Gate("GET /Plugins/Federation/Peer/Play/t/x HTTP/1.1\r\n\r\n", confirmed: false));
        Assert.NotNull(Gate("POST /Plugins/Federation/Pairing/Peer/Confirmed HTTP/1.1\r\nContent-Length: 2\r\n\r\n", confirmed: false));
    }

    [Theory]
    [InlineData("POST /Plugins/Federation/PlaybackToken HTTP/1.1\r\nTransfer-Encoding: chunked\r\n\r\n")]
    [InlineData("POST /Plugins/Federation/PlaybackToken HTTP/1.1\r\nContent-Length: 5\r\nContent-Length: 5\r\n\r\n")]
    [InlineData("POST /Plugins/Federation/PlaybackToken HTTP/1.1\r\nContent-Length: 5\r\ncontent-length: 6\r\n\r\n")]
    [InlineData("POST /Plugins/Federation/PlaybackToken HTTP/1.1\r\nContent-Length: +5\r\n\r\n")]
    [InlineData("POST /Plugins/Federation/PlaybackToken HTTP/1.1\r\nContent-Length: 5 \r\nContent-Length:5\r\n\r\n")]
    [InlineData("POST /Plugins/Federation/PlaybackToken HTTP/1.1\r\nContent-Length: 65537\r\n\r\n")]
    [InlineData("POST /Plugins/Federation/PlaybackToken HTTP/1.1\r\nContent-Length: 0x10\r\n\r\n")]
    [InlineData("POST /Plugins/Federation/PlaybackToken HTTP/1.1\r\nExpect: 100-continue\r\nContent-Length: 1\r\n\r\n")]
    [InlineData("GET /Plugins/Federation/Peer/SystemInfo HTTP/1.1\r\nContent-Length: 4\r\n\r\n")]
    [InlineData("POST /Plugins/Federation/PlaybackToken HTTP/1.1\r\n Folded: x\r\n\r\n")]
    [InlineData("POST /Plugins/Federation/PlaybackToken HTTP/1.0\r\n\r\n")]
    [InlineData("GET /Plugins/Federation/Peer/SystemInfo HTTP/1.1\r\nHost: a\r\nHost: b\r\n\r\n")]
    public void BadHeads_AreRefused(string head) => Assert.Null(Gate(head));

    [Fact]
    public void Forwarding_StripsLinkHeadersFromTheFriend_AndSetsItsOwn()
    {
        var gate = Gate("POST /Plugins/Federation/PlaybackToken HTTP/1.1\r\nHost: evil\r\nX-Federation-Token: t\r\nX-Federation-Link: forged\r\nx-federation-link-friend: someone-else\r\nX-Federation-Link-Peer: 1.2.3.4\r\nX-Forwarded-For: 9.9.9.9\r\nContent-Length: 2\r\n\r\n")!;
        var forwarded = Encoding.ASCII.GetString(gate.BuildForward("127.0.0.1:8096", "/", "friend-1", "203.0.113.9"));
        Assert.StartsWith("POST /Plugins/Federation/PlaybackToken HTTP/1.1\r\nHost: 127.0.0.1:8096\r\n", forwarded);
        Assert.DoesNotContain("forged", forwarded);
        Assert.DoesNotContain("someone-else", forwarded);
        Assert.DoesNotContain("1.2.3.4", forwarded);
        Assert.DoesNotContain("9.9.9.9", forwarded);
        Assert.DoesNotContain("evil", forwarded);
        Assert.Contains("X-Federation-Token: t\r\n", forwarded);
        Assert.Contains("X-Federation-Link: " + PairedLinkContext.Secret + "\r\n", forwarded);
        Assert.Contains("X-Federation-Link-Friend: friend-1\r\n", forwarded);
        Assert.Contains("X-Federation-Link-Peer: 203.0.113.9\r\n", forwarded);
        Assert.Contains("Content-Length: 2\r\n", forwarded);
        Assert.Single(forwarded.Split("\r\n"), l => l.StartsWith("Content-Length", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task BytesAfterTheBody_RefuseTheRequest()
    {
        var smuggled = "POST /Plugins/Federation/PlaybackToken HTTP/1.1\r\nContent-Length: 2\r\n\r\n{}GET /System/Info HTTP/1.1\r\n\r\n";
        var (head, _, refusal) = await PairedForwarder.ReadRequestAsync(new MemoryStream(Encoding.ASCII.GetBytes(smuggled)), true, CancellationToken.None);
        Assert.Null(head);
        Assert.Equal("bytes after the request", refusal);

        var exact = "POST /Plugins/Federation/PlaybackToken HTTP/1.1\r\nContent-Length: 2\r\n\r\n{}";
        var (ok, body, _) = await PairedForwarder.ReadRequestAsync(new MemoryStream(Encoding.ASCII.GetBytes(exact)), true, CancellationToken.None);
        Assert.NotNull(ok);
        Assert.Equal("{}", Encoding.ASCII.GetString(body));

        var shortBody = "POST /Plugins/Federation/PlaybackToken HTTP/1.1\r\nContent-Length: 9\r\n\r\n{}";
        Assert.Null((await PairedForwarder.ReadRequestAsync(new MemoryStream(Encoding.ASCII.GetBytes(shortBody)), true, CancellationToken.None)).Head);
    }

    [Fact]
    public void RouteTable_HasNoWildcardForPeerRoutes()
    {
        foreach (var (_, pattern, auth) in PairedRequestGate.Table())
        {
            Assert.DoesNotContain("Peer/.*", pattern);
            Assert.DoesNotContain("Peer/.+", pattern);
            if (pattern.Contains("Play/", StringComparison.Ordinal))
            {
                Assert.Equal(PairedAuth.ItemToken, auth);
            }
        }
    }

    [Fact]
    public async Task Paired_Quic_ConnectsBothWays_AndRefusesWrongKeysAndCertificates()
    {
        RequireQuic();
        using var a = MakeCertificate();
        using var b = MakeCertificate();
        var key = RandomNumberGenerator.GetBytes(32);
        var transcript = SHA256.HashData(a.GetRawCertData()).Concat(SHA256.HashData(b.GetRawCertData())).ToArray();
        var portA = FreeUdpPort();
        var portB = FreeUdpPort();

        // A good proof.
        var accept = QuicTransportHost.AcceptPaired(portB, new IPEndPoint(IPAddress.Loopback, portA), b, Fp(a), key, transcript, 8, TimeSpan.FromSeconds(10), CancellationToken.None);
        var client = await QuicTransportHost.ConnectPaired(portA, new IPEndPoint(IPAddress.Loopback, portB), a, Fp(b), key, transcript, 8, CancellationToken.None);
        var server = await accept;
        await using var linkA = new DirectLinkConnection(client, "b", false, null);
        await using var linkB = new DirectLinkConnection(server, "a", false, null);

        // Both sides can open request streams (one link carries both directions).
        using var webA = new RecordingServer();
        using var webB = new RecordingServer();
        var forwarderA = new PairedForwarder(() => new Uri("http://127.0.0.1:" + webA.Port), NullLogger.Instance);
        var forwarderB = new PairedForwarder(() => new Uri("http://127.0.0.1:" + webB.Port), NullLogger.Instance);
        _ = forwarderA.RunAsync(linkA, () => ("friend-b", "token-from-b", true));
        _ = forwarderB.RunAsync(linkB, () => ("friend-a", "token-from-a", true));

        Assert.Equal("200", await RequestAsync(linkA, "GET /Plugins/Federation/Peer/SystemInfo HTTP/1.1\r\nX-Federation-Token: token-from-a\r\n\r\n"));
        Assert.Equal("200", await RequestAsync(linkB, "POST /Plugins/Federation/PlaybackToken HTTP/1.1\r\nX-Federation-Token: token-from-b\r\nContent-Length: 2\r\n\r\n{}"));
        Assert.Contains(webB.Requests, r => r.Contains("X-Federation-Link-Friend: friend-a", StringComparison.Ordinal));
        Assert.Contains(webA.Requests, r => r.EndsWith("\r\n\r\n{}", StringComparison.Ordinal) && r.Contains("X-Federation-Link-Friend: friend-b", StringComparison.Ordinal));

        // Another friend's token, a core route and no token are all refused before Jellyfin sees them.
        var before = webB.Requests.Count;
        Assert.Equal("403", await RequestAsync(linkA, "GET /Plugins/Federation/Peer/SystemInfo HTTP/1.1\r\nX-Federation-Token: someone-elses\r\n\r\n"));
        Assert.Equal("403", await RequestAsync(linkA, "GET /System/Info HTTP/1.1\r\nX-Federation-Token: token-from-a\r\n\r\n"));
        Assert.Equal("403", await RequestAsync(linkA, "GET /Plugins/Federation/Peer/SystemInfo HTTP/1.1\r\n\r\n"));
        Assert.Equal(before, webB.Requests.Count);

        // A wrong pair key fails on both sides.
        var portC = FreeUdpPort();
        var portD = FreeUdpPort();
        var wrong = QuicTransportHost.AcceptPaired(portD, new IPEndPoint(IPAddress.Loopback, portC), b, Fp(a), RandomNumberGenerator.GetBytes(32), transcript, 8, TimeSpan.FromSeconds(5), CancellationToken.None);
        await Assert.ThrowsAnyAsync<Exception>(() => QuicTransportHost.ConnectPaired(portC, new IPEndPoint(IPAddress.Loopback, portD), a, Fp(b), key, transcript, 8, CancellationToken.None));
        await Assert.ThrowsAnyAsync<Exception>(() => wrong);

        // An impostor certificate never gets a link.
        using var eve = MakeCertificate();
        var portE = FreeUdpPort();
        var portF = FreeUdpPort();
        var pinned = QuicTransportHost.AcceptPaired(portF, new IPEndPoint(IPAddress.Loopback, portE), b, Fp(a), key, transcript, 8, TimeSpan.FromSeconds(4), CancellationToken.None);
        await Assert.ThrowsAnyAsync<Exception>(() => QuicTransportHost.ConnectPaired(portE, new IPEndPoint(IPAddress.Loopback, portF), eve, Fp(b), key, transcript, 8, CancellationToken.None));
        await Assert.ThrowsAnyAsync<Exception>(() => pinned);

        // Another address gets nothing, even with the right certificate and key.
        var portG = FreeUdpPort();
        var portH = FreeUdpPort();
        var onlyG = QuicTransportHost.AcceptPaired(portH, new IPEndPoint(IPAddress.Loopback, portG), b, Fp(a), key, transcript, 8, TimeSpan.FromSeconds(4), CancellationToken.None);
        await Assert.ThrowsAnyAsync<Exception>(() => QuicTransportHost.ConnectPaired(FreeUdpPort(), new IPEndPoint(IPAddress.Loopback, portH), a, Fp(b), key, transcript, 8, CancellationToken.None));
        await Assert.ThrowsAnyAsync<Exception>(() => onlyG);
    }

    private static async Task<string> RequestAsync(DirectLinkConnection link, string raw)
    {
        await using var stream = await QuicTransportHost.OpenStream(link.Quic, CancellationToken.None);
        await stream.WriteAsync(Encoding.ASCII.GetBytes(raw));
        using var reader = new StreamReader(stream, Encoding.ASCII);
        var status = await reader.ReadLineAsync();
        return status!.Split(' ')[1];
    }

    private static string Fp(X509Certificate2 certificate) => Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData())).ToLowerInvariant();

    private static X509Certificate2 MakeCertificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=pair-test", key, HashAlgorithmName.SHA256);
        using var made = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return X509CertificateLoader.LoadPkcs12(made.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable);
    }

    private static int FreeUdpPort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    private static void RequireQuic()
    {
        if (!QuicTransportHost.Available)
        {
            throw new InvalidOperationException("QUIC unavailable, paired link tests can't run: " + QuicTransportHost.Reason);
        }
    }

    /// <summary>Records each request (head and body) and answers 200 "ok".</summary>
    private sealed class RecordingServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

        public RecordingServer()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    TcpClient tcp;
                    try
                    {
                        tcp = await _listener.AcceptTcpClientAsync();
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
                            var buffer = new byte[70000];
                            var filled = 0;
                            int end;
                            while ((end = DirectLinkRequestHead.FindHeadEnd(buffer.AsSpan(0, filled))) < 0)
                            {
                                var n = await stream.ReadAsync(buffer.AsMemory(filled));
                                if (n == 0)
                                {
                                    return;
                                }

                                filled += n;
                            }

                            var head = Encoding.ASCII.GetString(buffer, 0, end);
                            var lengthLine = head.Split("\r\n").FirstOrDefault(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                            var length = lengthLine == null ? 0 : int.Parse(lengthLine.Substring(15).Trim());
                            while (filled < end + length)
                            {
                                filled += await stream.ReadAsync(buffer.AsMemory(filled));
                            }

                            Requests.Add(Encoding.ASCII.GetString(buffer, 0, end + length));
                            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"));
                        }
                    });
                }
            });
        }

        public int Port { get; }

        public ConcurrentBag<string> Requests { get; } = new();

        public void Dispose() => _listener.Stop();
    }
}

/// <summary>What a paired friend's stored settings report, whatever was saved.</summary>
public class PairedFriendSettingsTests
{
    [Fact]
    public void APairedFriend_AlwaysProxies_NeverHandsOff_AndHasNoDirectLink()
    {
        var friend = new Jellyfin.Plugin.Federation.Configuration.RemoteServer
        {
            StreamingMode = Jellyfin.Plugin.Federation.Configuration.StreamingMode.Direct,
            AllowRemoteTranscode = true,
            DirectLinkMode = "Auto",
        };
        Assert.Equal(Jellyfin.Plugin.Federation.Configuration.StreamingMode.Direct, friend.StreamingMode);
        friend.Paired = true;
        Assert.Equal(Jellyfin.Plugin.Federation.Configuration.StreamingMode.Proxy, friend.StreamingMode);
        Assert.False(friend.AllowRemoteTranscode);
        Assert.Equal("Off", friend.DirectLinkMode);
        friend.StreamingMode = Jellyfin.Plugin.Federation.Configuration.StreamingMode.Direct;
        Assert.Equal(Jellyfin.Plugin.Federation.Configuration.StreamingMode.Proxy, friend.StreamingMode);
        Assert.False(Jellyfin.Plugin.Federation.Services.FederationMediaSourceProvider.UsesRemoteTranscode(friend));
    }
}
