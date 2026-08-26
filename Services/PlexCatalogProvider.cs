using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// <see cref="IExternalCatalogProvider"/> for Plex Media Server. Thin adapter
    /// over <see cref="PlexApiClient"/>: this type owns the per-server plumbing
    /// (client reuse, translating Plex's library vocabulary into Jellyfin's),
    /// while the client owns the protocol.
    /// </summary>
    public class PlexCatalogProvider : IExternalCatalogProvider
    {
        // One HttpClient for every Plex server, rather than one per server or one
        // per call: a Plex library sync is many small requests in a row, and
        // rebuilding the client each time would burn a fresh TCP+TLS handshake on
        // each one (and eventually exhaust sockets). Generous timeout because a
        // large section listing over a slow remote link is legitimately slow -
        // the byte relay itself does not go through here.
        private static readonly HttpClient SharedHttpClient = new()
        {
            Timeout = TimeSpan.FromMinutes(2)
        };

        /// <summary>
        /// Test-only seam: when set, used instead of <see cref="SharedHttpClient"/>.
        /// Tests must reset this to null afterwards.
        /// </summary>
        internal static HttpClient? HttpClientOverride { get; set; }

        // Plex's own library type vocabulary -> Jellyfin's, so a mapping's
        // MediaType lines up with what the rest of the plugin expects without
        // per-product special-casing further up.
        private static readonly Dictionary<string, string> MediaTypeByPlexType = new(StringComparer.OrdinalIgnoreCase)
        {
            ["movie"] = "Movie",
            ["show"] = "Series"
        };

        private readonly ILogger<PlexCatalogProvider> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="PlexCatalogProvider"/> class.
        /// </summary>
        public PlexCatalogProvider(ILogger<PlexCatalogProvider> logger)
        {
            _logger = logger;
        }

        /// <inheritdoc />
        public ServerKind Kind => ServerKind.Plex;

        /// <inheritdoc />
        public async Task<IReadOnlyList<ExternalLibrary>> GetLibrariesAsync(RemoteServer server, CancellationToken cancellationToken)
        {
            var client = CreateClient(server);
            if (client == null)
            {
                return Array.Empty<ExternalLibrary>();
            }

            var sections = await client.GetSectionsAsync(cancellationToken).ConfigureAwait(false);
            return sections
                .Where(s => MediaTypeByPlexType.ContainsKey(s.Type))
                .Where(s => IsAllowed(server, s.Key))
                .Select(s => new ExternalLibrary(s.Key, s.Title, MediaTypeByPlexType[s.Type]))
                .ToList();
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<ExternalItem>?> GetItemsAsync(RemoteServer server, string libraryId, CancellationToken cancellationToken)
        {
            var client = CreateClient(server);
            if (client == null)
            {
                return null;
            }

            // The section's own type decides whether to walk shows+episodes or
            // just movies, so it has to be looked up rather than assumed from the
            // mapping - and a library that has since been deleted on the remote
            // must read as a failure (preserve the cache), not as "now empty"
            // (delete everything).
            if (!IsAllowed(server, libraryId))
            {
                // Deliberately the same "keep cached content, sync nothing new"
                // outcome as the section-not-found case below, not an empty list:
                // an empty list would read to the caller as "this library is now
                // empty" and delete everything already synced from it, whereas
                // this is "not allowed to sync this library at all" - the two
                // must never be confused, or revoking a friend's sharing consent
                // would destructively wipe content that was legitimately synced
                // while it was still allowed.
                _logger.LogWarning(
                    "[Federation] Refusing to sync Plex library {LibraryId} from {Server} - not in this server's allowed-library list",
                    libraryId,
                    server.Name);
                return null;
            }

            var sections = await client.GetSectionsAsync(cancellationToken).ConfigureAwait(false);
            var section = sections.FirstOrDefault(s => string.Equals(s.Key, libraryId, StringComparison.Ordinal));
            if (section == null)
            {
                _logger.LogWarning(
                    "[Federation] Plex library {LibraryId} was not found on {Server}; keeping its cached content this cycle",
                    libraryId,
                    server.Name);
                return null;
            }

            return await client.GetSectionItemsAsync(section, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Whether <paramref name="libraryId"/> is OK to sync from
        /// <paramref name="server"/>, per <see cref="RemoteServer.AllowedExternalLibraryIds"/>.
        /// Null means no restriction is on record - see that property's own doc
        /// comment for why that has to mean "allow", not "deny".
        /// </summary>
        private static bool IsAllowed(RemoteServer server, string libraryId)
        {
            return server.AllowedExternalLibraryIds == null
                || server.AllowedExternalLibraryIds.Contains(libraryId);
        }

        /// <inheritdoc />
        public async Task<string?> ResolveStreamUrlAsync(RemoteServer server, string nativeId, CancellationToken cancellationToken)
        {
            var client = CreateClient(server);
            if (client == null)
            {
                return null;
            }

            var partKey = await client.GetPartKeyAsync(nativeId, cancellationToken).ConfigureAwait(false);
            return partKey == null ? null : client.BuildStreamUrl(partKey);
        }

        /// <inheritdoc />
        public async Task<ExternalImageSet?> GetImagesAsync(RemoteServer server, string nativeId, CancellationToken cancellationToken)
        {
            var client = CreateClient(server);
            if (client == null)
            {
                return null;
            }

            var paths = await client.GetImagePathsAsync(nativeId, cancellationToken).ConfigureAwait(false);
            if (paths == null)
            {
                return null;
            }

            return new ExternalImageSet(
                paths.Value.Thumb == null ? null : client.BuildStreamUrl(paths.Value.Thumb),
                paths.Value.Art == null ? null : client.BuildStreamUrl(paths.Value.Art));
        }

        /// <inheritdoc />
        public async Task<string?> TestConnectionAsync(RemoteServer server, CancellationToken cancellationToken)
        {
            var client = CreateClient(server);
            return client == null
                ? null
                : await client.TestConnectionAsync(cancellationToken).ConfigureAwait(false);
        }

        private PlexApiClient? CreateClient(RemoteServer server)
        {
            if (string.IsNullOrWhiteSpace(server.Url) || string.IsNullOrWhiteSpace(server.ApiKey))
            {
                _logger.LogWarning("[Federation] Plex server {Server} has no URL or token configured", server.Name);
                return null;
            }

            // ApiKey holds the Plex token for a Plex-kind server - the field is
            // reused rather than adding a parallel one so every existing
            // credential-handling path (encryption at rest, redaction from API
            // responses, the config UI's write-only handling) covers it already.
            return new PlexApiClient(server.Url, server.ApiKey, HttpClientOverride ?? SharedHttpClient, _logger);
        }
    }

    /// <summary>
    /// Resolves the <see cref="IExternalCatalogProvider"/> for a given
    /// <see cref="ServerKind"/>, or null for <see cref="ServerKind.Jellyfin"/>,
    /// which is handled natively rather than through a provider.
    /// <para>
    /// This is the single place that knows which products are supported: adding
    /// one means registering it here and nowhere else.
    /// </para>
    /// </summary>
    public class ExternalCatalogRegistry
    {
        private readonly ConcurrentDictionary<ServerKind, IExternalCatalogProvider> _providers = new();

        /// <summary>
        /// Initializes a new instance of the <see cref="ExternalCatalogRegistry"/> class.
        /// </summary>
        public ExternalCatalogRegistry(IEnumerable<IExternalCatalogProvider> providers)
        {
            foreach (var provider in providers)
            {
                _providers[provider.Kind] = provider;
            }
        }

        /// <summary>
        /// Gets the provider handling <paramref name="kind"/>, or null when that
        /// kind is handled natively (Jellyfin) or isn't supported.
        /// </summary>
        public IExternalCatalogProvider? Get(ServerKind kind)
            => _providers.TryGetValue(kind, out var provider) ? provider : null;

        /// <summary>
        /// Convenience overload: the provider for a server, or null when it is an
        /// ordinary Jellyfin federation peer.
        /// </summary>
        public IExternalCatalogProvider? For(RemoteServer? server)
            => server == null ? null : Get(server.Kind);
    }
}
