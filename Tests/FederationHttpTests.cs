using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Services;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

/// <summary>
/// The paired-friend routing rules (docs/PAIRING.md, "Routing"): a paired friend's
/// address only ever reaches its own link.
/// </summary>
[Collection("FederationHttp")]
public class FederationHttpTests : IDisposable
{
    public FederationHttpTests() => FederationHttp.OpenLinkStream = null;

    public void Dispose() => FederationHttp.OpenLinkStream = null;

    [Fact]
    public void PairUrls_RoundTripTheirId_AndAreRecognised()
    {
        var url = FederationHttp.PairUrl(0x0123456789abcdefUL);
        Assert.Equal("http://[100::123:4567:89ab:cdef]", url);
        var uri = new Uri(url);
        Assert.True(FederationHttp.IsPairHost(uri.Host));
        Assert.True(FederationHttp.IsPairUrl(url + "/Plugins/Federation/Peer/SystemInfo"));
        Assert.Equal(0x0123456789abcdefUL, FederationHttp.PairingIdOf(IPAddress.Parse(uri.Host.Trim('[', ']'))));
        foreach (var other in new[] { "example.com", "127.0.0.1", "[::1]", "[100:0:0:1::5]", "[2001:db8::1]", "[::ffff:1.2.3.4]", "100.0.0.1", null, string.Empty })
        {
            Assert.False(FederationHttp.IsPairHost(other));
        }
    }

    [Fact]
    public void PairedAddresses_AreNeverValidInput_ExceptOnAStoredPairedFriend()
    {
        var url = FederationHttp.PairUrl(77);
        Assert.False(Jellyfin.Plugin.Federation.Configuration.ConfigValidator.IsValidServerUrl(url));
        Assert.False(Jellyfin.Plugin.Federation.Configuration.ConfigValidator.IsValidServerUrl(url + "/jellyfin"));
        Assert.True(Jellyfin.Plugin.Federation.Configuration.ConfigValidator.IsValidServerUrl("https://friend.example"));

        var config = new Jellyfin.Plugin.Federation.Configuration.PluginConfiguration();
        config.RemoteServers.Add(new Jellyfin.Plugin.Federation.Configuration.RemoteServer { Name = "x", Url = url });
        Assert.NotEmpty(Jellyfin.Plugin.Federation.Configuration.ConfigValidator.Validate(config));
        config.RemoteServers[0].Paired = true;
        Assert.Empty(Jellyfin.Plugin.Federation.Configuration.ConfigValidator.Validate(config));
    }

    [Fact]
    public async Task PairedAddress_WithoutALink_FailsBeforeAnySocket()
    {
        using var client = new HttpClient(FederationHttp.Handler());
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(FederationHttp.PairUrl(7) + "/x"));
    }

    [Fact]
    public async Task PairedAddress_GoesOverItsLink()
    {
        IPAddress? asked = null;
        FederationHttp.OpenLinkStream = (address, _) =>
        {
            asked = address;
            return ValueTask.FromResult<Stream>(FakeServer("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"));
        };
        using var client = new HttpClient(FederationHttp.Handler());
        var body = await client.GetStringAsync(FederationHttp.PairUrl(42) + "/Plugins/Federation/Peer/SystemInfo");
        Assert.Equal("ok", body);
        Assert.Equal(42UL, FederationHttp.PairingIdOf(asked!));
    }

    [Fact]
    public async Task RedirectFromAnotherHost_IntoALink_IsRefused()
    {
        var opened = false;
        FederationHttp.OpenLinkStream = (_, _) =>
        {
            opened = true;
            return ValueTask.FromResult<Stream>(FakeServer("HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n"));
        };
        using var server = new OneShotServer("HTTP/1.1 302 Found\r\nLocation: " + FederationHttp.PairUrl(9) + "/steal\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        using var client = new HttpClient(FederationHttp.Handler(allowAutoRedirect: true));
        client.DefaultRequestHeaders.Add("X-Federation-Token", "secret");
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(server.Url));
        Assert.False(opened);
    }

    [Fact]
    public async Task PairedRequest_RedirectedElsewhere_IsRefused()
    {
        using var elsewhere = new OneShotServer("HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n");
        FederationHttp.OpenLinkStream = (_, _) => ValueTask.FromResult<Stream>(FakeServer("HTTP/1.1 302 Found\r\nLocation: " + elsewhere.Url + "\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
        using var client = new HttpClient(FederationHttp.Handler(allowAutoRedirect: true));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(FederationHttp.PairUrl(3) + "/x"));
        Assert.False(elsewhere.WasContacted);
    }

    [Fact]
    public async Task PairedAddress_ThroughAProxy_IsRefused()
    {
        using var proxy = new OneShotServer("HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n");
        var opened = false;
        FederationHttp.OpenLinkStream = (_, _) =>
        {
            opened = true;
            return ValueTask.FromResult<Stream>(FakeServer("HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n"));
        };
        var handler = FederationHttp.Sockets();
        handler.UseProxy = true;
        handler.Proxy = new WebProxy(proxy.Url);
        using var client = new HttpClient(FederationHttp.Stamp(handler));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(FederationHttp.PairUrl(5) + "/x"));
        Assert.False(opened);
        Assert.False(proxy.WasContacted);
    }

    [Fact]
    public async Task UnstampedHandler_CantReachALink()
    {
        FederationHttp.OpenLinkStream = (_, _) => throw new InvalidOperationException("must not open");
        using var client = new HttpClient(FederationHttp.Sockets());
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(FederationHttp.PairUrl(11) + "/x"));
    }

    [Fact]
    public async Task NormalHosts_StillConnect()
    {
        using var server = new OneShotServer("HTTP/1.1 200 OK\r\nContent-Length: 5\r\nConnection: close\r\n\r\nhello");
        using var client = new HttpClient(FederationHttp.Handler());
        Assert.Equal("hello", await client.GetStringAsync(server.Url));
    }

    [Fact]
    public async Task GuardedPublicOnlyHook_StillRefusesPairedAddresses()
    {
        var innerCalled = false;
        var hook = FederationHttp.Guard((_, _) =>
        {
            innerCalled = true;
            throw new HttpRequestException("inner");
        });
        FederationHttp.OpenLinkStream = (_, _) => throw new InvalidOperationException("must not open");
        using var client = new HttpClient(FederationHttp.Stamp(new SocketsHttpHandler { ConnectCallback = hook }));
        var redirectHost = new OneShotServer("HTTP/1.1 200 OK\r\n\r\n");
        using (redirectHost)
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(redirectHost.Url));
        }

        Assert.True(innerCalled);
    }

    /// <summary>
    /// Every HttpClient the plugin keeps in a static field connects through the
    /// paired-friend hook, except clients that never talk to friends.
    /// </summary>
    [Fact]
    public void EveryFriendFacingClient_UsesTheHook()
    {
        var notFriendFacing = new HashSet<string>
        {
            "FunFactService.DefaultHttpClient",
            "PlexCatalogProvider.SharedHttpClient",
            "PlexMetadataSourceService.SharedHttpClient",
            "PlexCompanionEndpoint.SharedHttp",
            "FederationController.InternalJsonHttpClient",
            "FederationRemoteTranscodeGateway.Loopback",
            "TailscaleService.HttpClient",
        };
        var missing = new List<string>();
        var checkedCount = 0;
        foreach (var type in typeof(FederationHttp).Assembly.GetTypes())
        {
            foreach (var field in type.GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly))
            {
                if (field.FieldType != typeof(HttpClient) || type.IsGenericTypeDefinition)
                {
                    continue;
                }

                var name = type.Name + "." + field.Name.Replace("<", string.Empty).Replace(">k__BackingField", string.Empty);
                if (notFriendFacing.Contains(name) || field.GetValue(null) is not HttpClient client)
                {
                    continue;
                }

                checkedCount++;
                if (!UsesHook(client))
                {
                    missing.Add(name);
                }
            }
        }

        Assert.True(checkedCount >= 10, $"only {checkedCount} clients found");
        Assert.Empty(missing);
    }

    private static bool UsesHook(HttpClient client)
    {
        var handler = typeof(HttpMessageInvoker).GetField("_handler", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client) as HttpMessageHandler;
        var stamped = false;
        while (handler is DelegatingHandler delegating)
        {
            stamped |= delegating.GetType().Name == "StampHandler";
            handler = delegating.InnerHandler;
        }

        return stamped && handler is SocketsHttpHandler sockets && sockets.ConnectCallback != null;
    }

    private static Stream FakeServer(string response)
    {
        var toServer = new Pipe();
        var toClient = new Pipe();
        _ = Task.Run(async () =>
        {
            // Read the request head, then answer.
            var reader = toServer.Reader;
            while (true)
            {
                var read = await reader.ReadAsync();
                var text = Encoding.ASCII.GetString(System.Buffers.BuffersExtensions.ToArray(read.Buffer));
                reader.AdvanceTo(read.Buffer.Start, read.Buffer.End);
                if (text.Contains("\r\n\r\n", StringComparison.Ordinal) || read.IsCompleted)
                {
                    break;
                }
            }

            await toClient.Writer.WriteAsync(Encoding.ASCII.GetBytes(response));
            await toClient.Writer.CompleteAsync();
        });
        return new DuplexStream(toClient.Reader.AsStream(), toServer.Writer.AsStream());
    }

    private sealed class DuplexStream : Stream
    {
        private readonly Stream _read;
        private readonly Stream _write;

        public DuplexStream(Stream read, Stream write) => (_read, _write) = (read, write);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() => _write.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => _write.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => _read.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _read.ReadAsync(buffer, cancellationToken);

        public override void Write(byte[] buffer, int offset, int count) => _write.Write(buffer, offset, count);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => _write.WriteAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _read.Dispose();
                _write.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class OneShotServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

        public OneShotServer(string response)
        {
            _listener.Start();
            Url = "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port + "/";
            _ = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        using var socket = await _listener.AcceptSocketAsync();
                        WasContacted = true;
                        var buffer = new byte[8192];
                        await socket.ReceiveAsync(buffer, SocketFlags.None);
                        await socket.SendAsync(Encoding.ASCII.GetBytes(response), SocketFlags.None);
                        socket.Shutdown(SocketShutdown.Both);
                    }
                }
                catch (Exception)
                {
                    // Stopped.
                }
            });
        }

        public string Url { get; }

        public bool WasContacted { get; private set; }

        public void Dispose() => _listener.Stop();
    }
}
