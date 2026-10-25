using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Services.DirectLink;
using Jellyfin.Plugin.Federation.Services.Pairing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Jellyfin.Plugin.Federation.Tests;

/// <summary>
/// One download over a link with a 20 ms round trip (a UDP proxy holds every packet
/// 10 ms each way), with the QUIC library's default receive windows and with ours.
/// Live, 2026-10-22: one download over the link to friend-a stopped near 110 Mbps on
/// two fast lines; this reproduces the cap and checks the fix. Run with
/// FEDERATION_QUIC_BENCH=1 (it moves a few hundred MB).
/// </summary>
[Collection("QuicWindows")]
public class QuicWindowBenchTests
{
    private const int DelayMs = 10;
    private readonly ITestOutputHelper _output;

    public QuicWindowBenchTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task OneDownload_OverA20msLink_IsNotCappedNear110Mbps()
    {
        if (Environment.GetEnvironmentVariable("FEDERATION_QUIC_BENCH") != "1" || !QuicTransportHost.Available)
        {
            return;
        }

        try
        {
            QuicTransportHost.ConfigureWindows(-1, -1);
            var before = await MeasureAsync(128L * 1024 * 1024);
            QuicTransportHost.ConfigureWindows(0, 0);
            var after = await MeasureAsync(256L * 1024 * 1024);
            _output.WriteLine($"20 ms round trip, one download: library defaults {before:F0} Mbps, paired 16/4 MiB windows {after:F0} Mbps");
            Assert.True(after > 250, $"still capped: {after:F0} Mbps (defaults {before:F0})");
        }
        finally
        {
            QuicTransportHost.ConfigureWindows(0, 0);
        }
    }

    private static async Task<double> MeasureAsync(long bytes)
    {
        using var a = MakeCertificate();
        using var b = MakeCertificate();
        var key = RandomNumberGenerator.GetBytes(32);
        var transcript = SHA256.HashData(a.GetRawCertData()).Concat(SHA256.HashData(b.GetRawCertData())).ToArray();
        var serverPort = FreeUdpPort();
        var clientPort = FreeUdpPort();
        using var proxy = new DelayProxy(new IPEndPoint(IPAddress.Loopback, serverPort), TimeSpan.FromMilliseconds(DelayMs));

        var accept = QuicTransportHost.AcceptPaired(serverPort, proxy.BackEndPoint, b, Fp(a), key, transcript, 8, TimeSpan.FromSeconds(10), CancellationToken.None);
        var client = await QuicTransportHost.ConnectPaired(clientPort, proxy.FrontEndPoint, a, Fp(b), key, transcript, 8, CancellationToken.None);
        await using var clientLink = new DirectLinkConnection(client, "b", false, null);
        await using var serverLink = new DirectLinkConnection(await accept, "a", false, null);
        using var web = new BulkServer(bytes);
        var forwarder = new PairedForwarder(() => new Uri("http://127.0.0.1:" + web.Port), NullLogger.Instance);
        _ = forwarder.RunAsync(serverLink, () => ("a", "tok", true));

        await using var stream = await QuicTransportHost.OpenStream(clientLink.Quic, CancellationToken.None);
        await stream.WriteAsync(Encoding.ASCII.GetBytes("GET /Plugins/Federation/Peer/BitrateTest HTTP/1.1\r\nX-Federation-Token: tok\r\n\r\n"));
        var buffer = new byte[256 * 1024];
        long total = 0;
        var clock = Stopwatch.StartNew();
        int read;
        while ((read = await stream.ReadAsync(buffer)) > 0)
        {
            total += read;
        }

        Assert.True(total > bytes, $"only {total} bytes came back");
        return total * 8 / 1e6 / clock.Elapsed.TotalSeconds;
    }

    private static string Fp(X509Certificate2 certificate) => Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData())).ToLowerInvariant();

    private static X509Certificate2 MakeCertificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=bench", key, HashAlgorithmName.SHA256);
        using var made = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return X509CertificateLoader.LoadPkcs12(made.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable);
    }

    private static int FreeUdpPort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    /// <summary>Forwards UDP between one client and one server, holding every packet for a fixed delay.</summary>
    private sealed class DelayProxy : IDisposable
    {
        private readonly Socket _front = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        private readonly Socket _back = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        private readonly CancellationTokenSource _stop = new();
        private readonly IPEndPoint _server;
        private readonly TimeSpan _delay;
        private EndPoint? _client;

        public DelayProxy(IPEndPoint server, TimeSpan delay)
        {
            _server = server;
            _delay = delay;
            foreach (var s in new[] { _front, _back })
            {
                s.ReceiveBufferSize = 8 * 1024 * 1024;
                s.SendBufferSize = 8 * 1024 * 1024;
                s.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            }

            Pump(_front, _back, () => _server, remember: true);
            Pump(_back, _front, () => _client, remember: false);
        }

        public IPEndPoint FrontEndPoint => (IPEndPoint)_front.LocalEndPoint!;

        public IPEndPoint BackEndPoint => (IPEndPoint)_back.LocalEndPoint!;

        private void Pump(Socket from, Socket to, Func<EndPoint?> target, bool remember)
        {
            var queue = new BlockingCollection<(long Due, byte[] Data)>();
            var clock = Stopwatch.StartNew();
            new Thread(() =>
            {
                var buffer = new byte[65536];
                EndPoint any = new IPEndPoint(IPAddress.Any, 0);
                while (!_stop.IsCancellationRequested)
                {
                    try
                    {
                        var n = from.ReceiveFrom(buffer, ref any);
                        if (remember)
                        {
                            _client = any;
                        }

                        queue.Add((clock.ElapsedTicks + (long)(_delay.TotalSeconds * Stopwatch.Frequency), buffer.AsSpan(0, n).ToArray()));
                    }
                    catch (Exception)
                    {
                        return;
                    }
                }
            }) { IsBackground = true }.Start();
            new Thread(() =>
            {
                foreach (var (due, data) in queue.GetConsumingEnumerable())
                {
                    while (clock.ElapsedTicks < due)
                    {
                        var wait = (due - clock.ElapsedTicks) * 1000 / Stopwatch.Frequency;
                        if (wait > 1)
                        {
                            Thread.Sleep((int)wait - 1);
                        }
                        else
                        {
                            Thread.SpinWait(50);
                        }
                    }

                    try
                    {
                        if (target() is { } t)
                        {
                            to.SendTo(data, t);
                        }
                    }
                    catch (Exception)
                    {
                        return;
                    }
                }
            }) { IsBackground = true }.Start();
        }

        public void Dispose()
        {
            _stop.Cancel();
            _front.Dispose();
            _back.Dispose();
        }
    }

    /// <summary>Answers any request with a large body.</summary>
    private sealed class BulkServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

        public BulkServer(long bytes)
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        var tcp = await _listener.AcceptTcpClientAsync();
                        _ = Task.Run(async () =>
                        {
                            using (tcp)
                            {
                                var stream = tcp.GetStream();
                                var head = new byte[16384];
                                await stream.ReadAsync(head);
                                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {bytes}\r\nConnection: close\r\n\r\n"));
                                var chunk = new byte[256 * 1024];
                                for (long sent = 0; sent < bytes; sent += chunk.Length)
                                {
                                    await stream.WriteAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, bytes - sent)));
                                }
                            }
                        });
                    }
                }
                catch (Exception)
                {
                    // Stopped.
                }
            });
        }

        public int Port { get; }

        public void Dispose() => _listener.Stop();
    }
}
