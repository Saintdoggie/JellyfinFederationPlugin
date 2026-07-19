using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;
using Jellyfin.Plugin.Federation.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Shared factory for <see cref="RemoteServerClient"/> instances. Reuses HttpClients per server
    /// so sockets are shared across requests.
    /// </summary>
    public interface IRemoteServerClientFactory
    {
        /// <summary>
        /// Gets or creates a client for the given server configuration.
        /// </summary>
        RemoteServerClient GetClient(RemoteServer server);

        /// <summary>
        /// Gets or creates a client for the given server ID (must be configured).
        /// </summary>
        RemoteServerClient? GetClient(string serverId);

        /// <summary>
        /// Drops the cached HttpClient for the given server (call on config change).
        /// </summary>
        void Invalidate(string serverId);

        /// <summary>
        /// Drops all cached clients.
        /// </summary>
        void InvalidateAll();
    }

    /// <summary>
    /// Default implementation of <see cref="IRemoteServerClientFactory"/>.
    /// </summary>
    public class RemoteServerClientFactory : IRemoteServerClientFactory, IDisposable
    {
        private readonly ILoggerFactory _loggerFactory;
        private readonly ConcurrentDictionary<string, HttpClient> _httpClients = new();

        /// <summary>
        /// Initializes a new instance of the <see cref="RemoteServerClientFactory"/> class.
        /// </summary>
        public RemoteServerClientFactory(ILoggerFactory loggerFactory)
        {
            _loggerFactory = loggerFactory;
        }

        /// <inheritdoc />
        public RemoteServerClient GetClient(RemoteServer server)
        {
            if (server == null)
            {
                throw new ArgumentNullException(nameof(server));
            }

            var httpClient = _httpClients.GetOrAdd(server.Id, _ => CreateHttpClient(server));
            return new RemoteServerClient(server, _loggerFactory.CreateLogger<RemoteServerClient>(), httpClient);
        }

        /// <inheritdoc />
        public RemoteServerClient? GetClient(string serverId)
        {
            var config = Plugin.Instance?.Configuration;
            var server = config?.RemoteServers?.Find(s => s.Id == serverId);
            return server == null ? null : GetClient(server);
        }

        /// <inheritdoc />
        public void Invalidate(string serverId)
        {
            if (_httpClients.TryRemove(serverId, out var client))
            {
                client.Dispose();
            }
        }

        /// <inheritdoc />
        public void InvalidateAll()
        {
            foreach (var kvp in _httpClients)
            {
                kvp.Value.Dispose();
            }

            _httpClients.Clear();
        }

        /// <inheritdoc />
        public void Dispose()
        {
            InvalidateAll();
        }

        private static HttpClient CreateHttpClient(RemoteServer server)
        {
            var client = new HttpClient
            {
                BaseAddress = new Uri(server.Url.TrimEnd('/')),
                Timeout = TimeSpan.FromMinutes(5)
            };
            client.DefaultRequestHeaders.Add("X-Emby-Token", server.ApiKey);
            return client;
        }
    }
}
