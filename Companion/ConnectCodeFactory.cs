namespace FederationCompanion;

/// <summary>
/// Picks how a Plex → Jellyfin connect code is built.
/// When Companion has a Funnel/public HTTPS URL, that is the intended path
/// for friends outside the home (Starlink, no port-forward). Plex Remote
/// Access/Relay is included as a fallback inside the same code so a dead
/// Funnel TLS handshake can still connect. Funnel-only when Plex has no
/// public path; Relay-only when Funnel is not configured.
/// </summary>
public static class ConnectCodeFactory
{
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
                shared,
                FallbackUrl: hasPlex ? remotePlexUrl!.Trim().TrimEnd('/') : null,
                FallbackToken: hasPlex ? serverAccessToken : null);
            error = null;
            return true;
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
