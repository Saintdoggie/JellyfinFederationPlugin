using FederationCompanion;

namespace FederationCompanion.Tests;

public class ConnectCodeFactoryTests
{
    [Fact]
    public void TryGenerate_PrefersPlexRelay_EvenWhenFunnelUrlIsSaved()
    {
        var libraries = new[]
        {
            new CompanionLibrary { SectionKey = "1", Title = "Movies", Type = "movie", Shared = true },
            new CompanionLibrary { SectionKey = "2", Title = "Private", Type = "movie", Shared = false }
        };

        var ok = ConnectCodeFactory.TryGenerate(
            publicUrl: "https://freakbob.tail4e0b6f.ts.net",
            remotePlexUrl: "https://relay.plex.direct:443",
            serverAccessToken: "plex-token",
            serverName: "freakbob",
            libraries,
            createClaimToken: () => throw new InvalidOperationException("claim token must not be minted when Plex Relay exists"),
            out var code,
            out var error);

        Assert.True(ok, error);
        Assert.NotNull(code);
        Assert.Equal("direct", code!.Mode);
        Assert.False(code.Claim);
        Assert.Equal("https://relay.plex.direct:443", code.Url);
        Assert.Equal("plex-token", code.Token);
        Assert.Equal("freakbob", code.Name);
        Assert.Equal("1", Assert.Single(code.Libraries).SectionKey);
    }

    [Fact]
    public void TryGenerate_UsesFunnelClaim_OnlyWhenPlexHasNoPublicPath()
    {
        var ok = ConnectCodeFactory.TryGenerate(
            publicUrl: "https://name.tail12345.ts.net",
            remotePlexUrl: null,
            serverAccessToken: "plex-token",
            serverName: "Home Plex",
            Array.Empty<CompanionLibrary>(),
            createClaimToken: () => "claim-token",
            out var code,
            out var error);

        Assert.True(ok, error);
        Assert.Equal("claim", code!.Mode);
        Assert.True(code.Claim);
        Assert.Equal("https://name.tail12345.ts.net", code.Url);
        Assert.Equal("claim-token", code.Token);
        Assert.Empty(code.Libraries);
    }

    [Fact]
    public void TryGenerate_Errors_WhenNeitherPlexNorFunnelIsPublic()
    {
        var ok = ConnectCodeFactory.TryGenerate(
            publicUrl: "http://100.64.1.20:7890",
            remotePlexUrl: null,
            serverAccessToken: "plex-token",
            serverName: "LAN Plex",
            Array.Empty<CompanionLibrary>(),
            createClaimToken: () => "unused",
            out var code,
            out var error);

        Assert.False(ok);
        Assert.Null(code);
        Assert.Contains("Plex Remote Access", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FriendFacingShare_PrefersPlexRemote_OverFunnelRelay()
    {
        var share = ConnectCodeFactory.FriendFacingShare(
            remotePlexUrl: "https://1-2-3-4.hash.plex.direct:32400",
            serverAccessToken: "real-plex-token",
            publicUrl: "https://name.ts.net",
            peerId: "peer-1",
            peerAccessToken: "relay-token");

        Assert.False(share.RelayedThroughCompanion);
        Assert.Equal("https://1-2-3-4.hash.plex.direct:32400", share.PlexUrl);
        Assert.Equal("real-plex-token", share.PlexToken);
    }

    [Fact]
    public void FriendFacingShare_UsesFunnelRelay_WhenPlexIsLanOnly()
    {
        var share = ConnectCodeFactory.FriendFacingShare(
            remotePlexUrl: null,
            serverAccessToken: "real-plex-token",
            publicUrl: "https://name.ts.net",
            peerId: "peer-1",
            peerAccessToken: "relay-token");

        Assert.True(share.RelayedThroughCompanion);
        Assert.Equal("https://name.ts.net/plex/peer-1", share.PlexUrl);
        Assert.Equal("relay-token", share.PlexToken);
    }
}
