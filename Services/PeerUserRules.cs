using System.Collections.Generic;
using Jellyfin.Plugin.Federation.Configuration;

namespace Jellyfin.Plugin.Federation.Services;

/// <summary>
/// What a friend answered to <c>Peer/UserRules</c>: the complete list of rules its
/// admin set for this server's own users. <see cref="Supported"/> is false for a
/// friend running a version without the endpoint; its stored rules stay as pushed.
/// </summary>
public sealed record PeerUserRulesResult(
    bool Supported,
    IReadOnlyList<RemoteUserAccessRule> Rules,
    bool? AllowDownloads = null,
    bool? ShareFavorites = null)
{
    /// <summary>A friend without the endpoint.</summary>
    public static readonly PeerUserRulesResult Unsupported = new(false, new List<RemoteUserAccessRule>());
}

/// <summary>Wire shape of <c>Peer/UserRules</c>.</summary>
public sealed class PeerUserRulesBody
{
    /// <summary>Gets or sets the rules for the caller's users.</summary>
    public List<RemoteUserAccessRule>? Rules { get; set; }

    /// <summary>Gets or sets whether the friend lets the caller's viewers download (absent from 0.0.187).</summary>
    public bool? AllowDownloads { get; set; }

    /// <summary>Gets or sets whether the friend shares its users' favorites (absent from 0.0.187).</summary>
    public bool? ShareFavorites { get; set; }
}
