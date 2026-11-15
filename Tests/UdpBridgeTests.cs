using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Jellyfin.Plugin.Federation.Services.DirectLink;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

/// <summary>
/// The UDP bridge that lets an accepting server keep its punched socket (Windows
/// Firewall only admits the friend on that socket). Plain sockets on loopback.
/// </summary>
public class UdpBridgeTests
{
    private static Socket Bound()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ReceiveTimeout = 3000 };
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return socket;
    }

    private static IPEndPoint Local(Socket socket) => (IPEndPoint)socket.LocalEndPoint!;

    private static (string Text, IPEndPoint From) Receive(Socket socket)
    {
        var buffer = new byte[2048];
        EndPoint from = new IPEndPoint(IPAddress.Any, 0);
        var length = socket.ReceiveFrom(buffer, ref from);
        return (Encoding.ASCII.GetString(buffer, 0, length), (IPEndPoint)from);
    }

    [Fact]
    public void CarriesPacketsBothWays_BetweenTheFriendAndTheListener()
    {
        using var friend = Bound();
        var punched = Bound();
        var punchedAt = Local(punched);
        using var bridge = UdpBridge.Create(punched, Local(friend));
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ReceiveTimeout = 3000 };
        listener.Bind(new IPEndPoint(IPAddress.Loopback, bridge.ListenerPort));
        bridge.Start();

        Assert.Equal(Local(friend), bridge.Peer);
        Assert.Equal(punchedAt, bridge.Local);
        Assert.True(IPAddress.IsLoopback(bridge.ListenerSees.Address));

        friend.SendTo(Encoding.ASCII.GetBytes("to the listener"), punchedAt);
        var (text, from) = Receive(listener);
        Assert.Equal("to the listener", text);
        Assert.Equal(bridge.ListenerSees, from);

        listener.SendTo(Encoding.ASCII.GetBytes("to the friend"), bridge.ListenerSees);
        (text, from) = Receive(friend);
        Assert.Equal("to the friend", text);
        Assert.Equal(punchedAt, from);
    }

    [Fact]
    public void LetsInOnlyTheFriend_AndOutOnlyTheListener()
    {
        using var friend = Bound();
        using var stranger = Bound();
        var punched = Bound();
        var punchedAt = Local(punched);
        using var bridge = UdpBridge.Create(punched, Local(friend));
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ReceiveTimeout = 3000 };
        listener.Bind(new IPEndPoint(IPAddress.Loopback, bridge.ListenerPort));
        bridge.Start();

        // A stranger on the outside never reaches the listener.
        stranger.SendTo(Encoding.ASCII.GetBytes("stranger"), punchedAt);
        friend.SendTo(Encoding.ASCII.GetBytes("friend"), punchedAt);
        Assert.Equal("friend", Receive(listener).Text);

        // Another local program can't send to the friend through the bridge.
        stranger.SendTo(Encoding.ASCII.GetBytes("local stranger"), bridge.ListenerSees);
        listener.SendTo(Encoding.ASCII.GetBytes("listener"), bridge.ListenerSees);
        Assert.Equal("listener", Receive(friend).Text);
        Assert.Equal(2, bridge.Dropped);
    }

    [Fact]
    public void Dispose_ClosesThePunchedSocket_SoThePortIsFreeAgain()
    {
        using var friend = Bound();
        var punched = Bound();
        var port = Local(punched).Port;
        var bridge = UdpBridge.Create(punched, Local(friend));
        bridge.Start();
        bridge.Dispose();
        bridge.Dispose();

        using var again = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        again.Bind(new IPEndPoint(IPAddress.Loopback, port));
        Assert.Throws<ObjectDisposedException>(() => punched.Send(new byte[1]));
    }

    [Fact]
    public void SameEndpoint_ComparesAddressAndPort()
    {
        var a = new IPEndPoint(IPAddress.Parse("192.0.2.7"), 4000).Serialize();
        Assert.True(UdpBridge.SameEndpoint(a, new IPEndPoint(IPAddress.Parse("192.0.2.7"), 4000).Serialize()));
        Assert.False(UdpBridge.SameEndpoint(a, new IPEndPoint(IPAddress.Parse("192.0.2.7"), 4001).Serialize()));
        Assert.False(UdpBridge.SameEndpoint(a, new IPEndPoint(IPAddress.Parse("192.0.2.8"), 4000).Serialize()));
    }
}
