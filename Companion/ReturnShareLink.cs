namespace FederationCompanion;

/// <summary>A return offer belongs to an authenticated, existing Plex friend.
/// Recording it never fetches a URL or selects libraries on the owner's behalf.</summary>
public static class ReturnShareLink
{
    public static JellyfinImportPeer Offer(CompanionState state, CompanionPeer friend, ReturnShareOffer offer)
    {
        if (!state.Peers.Contains(friend)) throw new InvalidOperationException("Friend is no longer connected.");
        if (!Uri.TryCreate(offer.Url, UriKind.Absolute, out var uri)
            || uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)
            || !PlexRemoteEndpoint.IsPublicHttpsUrl(offer.Url)
            || string.IsNullOrWhiteSpace(offer.Token) || offer.Token.Length > 512
            || !Guid.TryParse(offer.FederationId, out _))
            throw new ArgumentException("The return share needs a public HTTPS Jellyfin address and a valid credential.");

        var url = uri.AbsoluteUri.TrimEnd('/');
        var peer = state.ImportPeers.FirstOrDefault(p => p.CompanionPeerId == friend.Id);
        if (peer != null && (!string.Equals(peer.Url, url, StringComparison.OrdinalIgnoreCase)
            || peer.SourceFederationId != offer.FederationId))
            throw new InvalidOperationException("The source address or identity changed. Remove the old import before connecting the new source.");

        // Adopt the owner's existing code-based import only when both address
        // and credential match; preserve selections, file identity and Plex IDs.
        peer ??= state.ImportPeers.FirstOrDefault(p => p.CompanionPeerId == null
            && string.Equals(p.Url, url, StringComparison.OrdinalIgnoreCase) && p.Token == offer.Token);
        if (peer == null)
        {
            peer = new JellyfinImportPeer
            {
                Url = url, SelectedLibraryIds = new(), ReturnSharePending = true,
                PlaybackBaseUrl = state.PublicUrl
            };
            peer.ExportPath = Path.Combine(AppContext.BaseDirectory, "imported", peer.Id);
            state.ImportPeers.Add(peer);
        }
        peer.CompanionPeerId = friend.Id;
        peer.SourceFederationId = offer.FederationId;
        peer.Token = offer.Token;
        peer.Name = string.IsNullOrWhiteSpace(offer.Name) ? friend.Name : offer.Name.Trim()[..Math.Min(offer.Name.Trim().Length, 160)];
        return peer;
    }
}

public sealed record ReturnShareOffer(string Url, string Token, string FederationId, string? Name);
