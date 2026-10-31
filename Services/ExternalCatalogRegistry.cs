using System.Collections.Concurrent;
using System.Collections.Generic;
using Jellyfin.Plugin.Federation.Configuration;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Resolves the registered <see cref="IExternalCatalogProvider"/> for a given
    /// <see cref="ServerKind"/>, or null for <see cref="ServerKind.Jellyfin"/>,
    /// which is handled natively rather than through a provider.
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
