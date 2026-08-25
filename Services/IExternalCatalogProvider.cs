using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;
using MediaBrowser.Model.Dto;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Adapts a media server that is <em>not</em> another Jellyfin running this
    /// plugin (Plex today; Emby, Kodi or anything else later) onto the shapes the
    /// rest of federation already speaks, so its content flows through the
    /// existing sync, cache, materialization and stream-relay pipeline rather
    /// than needing a parallel one per product.
    /// <para>
    /// To add support for another server product: implement this interface,
    /// add a value to <see cref="ServerKind"/>, and register the implementation
    /// in <see cref="ExternalCatalogRegistry"/>. Nothing else in the plugin needs
    /// to change - <see cref="FederationSyncService"/> and
    /// <see cref="FederationStreamHandler"/> both dispatch through the registry
    /// rather than naming any concrete provider.
    /// </para>
    /// <para>
    /// Implementations must be safe to call concurrently and must never throw for
    /// an unreachable or misbehaving remote: return empty/null and let the caller
    /// treat it as an ordinary transient sync failure, which preserves the
    /// existing cache instead of deleting that server's whole library over a blip.
    /// </para>
    /// </summary>
    public interface IExternalCatalogProvider
    {
        /// <summary>
        /// Which <see cref="RemoteServer.Kind"/> this provider handles.
        /// </summary>
        ServerKind Kind { get; }

        /// <summary>
        /// Lists the libraries the remote exposes, for the admin to map onto local
        /// ones. Empty when unreachable.
        /// </summary>
        Task<IReadOnlyList<ExternalLibrary>> GetLibrariesAsync(RemoteServer server, CancellationToken cancellationToken);

        /// <summary>
        /// Fetches every item in one remote library. Implementations are
        /// responsible for their own paging, and must return parents before
        /// children (series before their episodes), because the sync pipeline
        /// skips any episode whose series it hasn't already seen this cycle.
        /// Returns null - distinct from an empty list - when the fetch failed, so
        /// the caller can preserve the cache rather than treating the library as
        /// having become empty.
        /// </summary>
        Task<IReadOnlyList<ExternalItem>?> GetItemsAsync(RemoteServer server, string libraryId, CancellationToken cancellationToken);

        /// <summary>
        /// Resolves the absolute, credential-bearing URL this server should fetch
        /// an item's bytes from, at play time. Resolved per play rather than
        /// cached at sync time so a remote-side rescan that moves or re-ids the
        /// file doesn't leave a permanently broken link.
        /// <para>
        /// The returned URL generally carries a credential for the whole remote
        /// server, so it is strictly internal: only ever fetched server-side by
        /// <see cref="FederationStreamHandler"/>, never handed to a client.
        /// </para>
        /// </summary>
        Task<string?> ResolveStreamUrlAsync(RemoteServer server, string nativeId, CancellationToken cancellationToken);

        /// <summary>
        /// Resolves cover art / backdrop URLs for an item, or null when the item
        /// has none or couldn't be fetched. Like <see cref="ResolveStreamUrlAsync"/>,
        /// resolved per request rather than cached at sync time, and the URLs
        /// generally carry a credential for the whole remote server - safe to embed
        /// because Jellyfin's image pipeline (<c>IRemoteImageProvider.GetImageResponse</c>)
        /// fetches them server-side and caches the bytes locally; a client is never
        /// handed the URL itself.
        /// </summary>
        Task<ExternalImageSet?> GetImagesAsync(RemoteServer server, string nativeId, CancellationToken cancellationToken);

        /// <summary>
        /// Verifies the server is reachable and its credential works, returning
        /// its reported friendly name, or null when it isn't usable.
        /// </summary>
        Task<string?> TestConnectionAsync(RemoteServer server, CancellationToken cancellationToken);
    }

    /// <summary>
    /// One library on an external server, as offered to the admin for mapping.
    /// <paramref name="MediaType"/> uses Jellyfin's own vocabulary ("Movie" /
    /// "Series") rather than the source product's, so it lines up with
    /// <see cref="LibraryMapping.MediaType"/> without per-product translation
    /// further up.
    /// </summary>
    public sealed record ExternalLibrary(string Id, string Name, string MediaType);

    /// <summary>
    /// One item from an external server: the metadata translated into Jellyfin's
    /// <see cref="BaseItemDto"/>, plus that server's own native id for it.
    /// <para>
    /// The native id is carried separately because <see cref="BaseItemDto.Id"/>
    /// is a Guid and most other products don't use Guids - a provider maps its
    /// native id into one deterministically (one-way), so the original has to be
    /// preserved for the stream path to resolve later. It is stored on
    /// <see cref="FederatedItemMetadata.RemoteNativeId"/>.
    /// </para>
    /// </summary>
    public sealed record ExternalItem(BaseItemDto Dto, string NativeId);

    /// <summary>
    /// An item's cover art / backdrop, as absolute, credential-bearing URLs ready
    /// to fetch. Either may be null when the source has no such image.
    /// </summary>
    public sealed record ExternalImageSet(string? PrimaryUrl, string? BackdropUrl);
}
