namespace FederationCompanion;

/// <summary>
/// Picks how a Plex → Jellyfin connect code is built. A saved Funnel URL is
/// not proof Funnel HTTPS actually works: Tailscale often publishes public DNS
/// and an HTTP→HTTPS redirect while TLS on :443 immediately EOFs (common on
/// Starlink, no port-forward). Plex Remote Access / Plex Relay is the path
/// that works without port forwarding, so it wins whenever it exists.
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
        IReadOnlyList<SharedLibraryView> Libraries);

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

        if (!string.IsNullOrWhiteSpace(remotePlexUrl) && !string.IsNullOrWhiteSpace(serverAccessToken))
        {
            code = new GeneratedCode(
                "direct",
                remotePlexUrl.Trim().TrimEnd('/'),
                serverAccessToken,
                serverName,
                Claim: false,
                shared);
            error = null;
            return true;
        }

        if (PlexRemoteEndpoint.IsPublicHttpsUrl(publicUrl))
        {
            code = new GeneratedCode(
                "claim",
                publicUrl!.Trim().TrimEnd('/'),
                createClaimToken(),
                serverName,
                Claim: true,
                Array.Empty<SharedLibraryView>());
            error = null;
            return true;
        }

        code = null;
        error = "Friends who aren't on your Tailscale need a public path. Enable Plex Remote Access / Plex Relay (works on Starlink, no port forwarding), or set a Tailscale Funnel URL whose HTTPS actually works.";
        return false;
    }

    /// <summary>
    /// After a Funnel claim handshake, hand the friend Plex's own public
    /// address when one exists so playback does not depend on Funnel TLS or
    /// Funnel bandwidth limits. Funnel relay is only the last resort.
    /// </summary>
    public static (string PlexUrl, string PlexToken, bool RelayedThroughCompanion) FriendFacingShare(
        string? remotePlexUrl,
        string? serverAccessToken,
        string? publicUrl,
        string peerId,
        string peerAccessToken)
    {
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
