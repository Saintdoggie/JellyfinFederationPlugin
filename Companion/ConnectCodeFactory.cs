namespace FederationCompanion;

/// <summary>
/// Picks how a Plex → Jellyfin connect code is built.
/// When Companion has a Funnel/public HTTPS URL, that is the intended path
/// for friends outside the home (Starlink, no port-forward). Plex Remote
/// Access/Relay remains the legacy direct mode only when no Funnel is configured,
/// or when the owner explicitly chooses it after a Funnel miss.
/// Scoped Funnel codes never carry a raw Plex-token fallback. A Funnel miss
/// (URL is Plex, or TLS is dead) must not mint the standing PMS token either.
/// </summary>
public static class ConnectCodeFactory
{
    public const string FunnelMissRequiresExplicitChoice =
        "Funnel is set but it is not this Companion (it may be Plex, or HTTPS is down). Fix Funnel so it points at Companion, or explicitly choose Plex Remote Access. Companion will not put your Plex token in a connect code while Funnel is expected.";

    public sealed record SharedLibraryView(string SectionKey, string Title, string Type);

    public sealed record GeneratedCode(
        string Mode,
        string Url,
        string Token,
        string? Name,
        bool Claim,
        IReadOnlyList<SharedLibraryView> Libraries,
        string? FallbackUrl = null,
        string? FallbackToken = null);

    public static bool TryGenerate(
        string? publicUrl,
        string? remotePlexUrl,
        string? serverAccessToken,
        string? serverName,
        IEnumerable<CompanionLibrary> libraries,
        Func<string> createClaimToken,
        out GeneratedCode? code,
        out string? error)
        => TryGenerate(
            publicUrl,
            remotePlexUrl,
            serverAccessToken,
            serverName,
            libraries,
            createClaimToken,
            funnelExpected: false,
            usePlexRemoteAccess: false,
            out code,
            out error);

    public static bool TryGenerate(
        string? publicUrl,
        string? remotePlexUrl,
        string? serverAccessToken,
        string? serverName,
        IEnumerable<CompanionLibrary> libraries,
        Func<string> createClaimToken,
        bool funnelExpected,
        bool usePlexRemoteAccess,
        out GeneratedCode? code,
        out string? error)
    {
        var shared = libraries
            .Where(l => l.Shared && !string.IsNullOrWhiteSpace(l.SectionKey))
            .Select(l => new SharedLibraryView(l.SectionKey, l.Title, l.Type))
            .ToList();

        var hasPlex = !string.IsNullOrWhiteSpace(remotePlexUrl) && !string.IsNullOrWhiteSpace(serverAccessToken);
        var hasFunnel = PlexRemoteEndpoint.IsPublicHttpsUrl(publicUrl);

        if (hasFunnel)
        {
            code = new GeneratedCode(
                "claim",
                publicUrl!.Trim().TrimEnd('/'),
                createClaimToken(),
                serverName,
                Claim: true,
                shared);
            error = null;
            return true;
        }

        if (funnelExpected && !usePlexRemoteAccess)
        {
            code = null;
            error = FunnelMissRequiresExplicitChoice;
            return false;
        }

        if (hasPlex)
        {
            code = new GeneratedCode(
                "direct",
                remotePlexUrl!.Trim().TrimEnd('/'),
                serverAccessToken!,
                serverName,
                Claim: false,
                shared);
            error = null;
            return true;
        }

        code = null;
        error = "Friends outside your home need a public path. Turn on Tailscale Funnel for this app (Starlink, no port forwarding), or enable Plex Remote Access / Plex Relay.";
        return false;
    }

    /// <summary>
    /// After a Funnel claim handshake, keep playback on Funnel when that is
    /// the owner's public path — that is what makes Plex reachable off-LAN.
    /// Plex Remote Access/Relay is only used when Funnel is not configured.
    /// </summary>
    public static (string PlexUrl, string PlexToken, bool RelayedThroughCompanion) FriendFacingShare(
        string? remotePlexUrl,
        string? serverAccessToken,
        string? publicUrl,
        string peerId,
        string peerAccessToken)
    {
        if (PlexRemoteEndpoint.IsPublicHttpsUrl(publicUrl))
        {
            return (
                $"{publicUrl!.Trim().TrimEnd('/')}/plex/{Uri.EscapeDataString(peerId)}",
                peerAccessToken,
                true);
        }

        if (!string.IsNullOrWhiteSpace(remotePlexUrl) && !string.IsNullOrWhiteSpace(serverAccessToken))
        {
            return (remotePlexUrl.Trim().TrimEnd('/'), serverAccessToken, false);
        }

        return (
            $"{publicUrl!.Trim().TrimEnd('/')}/plex/{Uri.EscapeDataString(peerId)}",
            peerAccessToken,
            true);
    }
}
