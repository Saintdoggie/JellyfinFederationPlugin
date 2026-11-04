using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;
using MediaBrowser.Controller;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Reads the reviewed integration catalog and filters it for this server.
    /// This version is discovery-only: it never downloads or executes an add-in.
    /// </summary>
    public sealed class IntegrationCatalogService
    {
        internal const string CatalogUrl = "https://raw.githubusercontent.com/Saintdoggie/JellyfinFederationPlugin/master/integrations/catalog.json";
        private const int MaxCatalogBytes = 64 * 1024;
        private static readonly TimeSpan CacheFor = TimeSpan.FromHours(6);
        private static readonly HttpClient CatalogHttp = new(FederationHttp.Stamp(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            ConnectCallback = FederationHttp.ConnectAsync
        }))
        {
            Timeout = TimeSpan.FromSeconds(10)
        };

        private readonly IServerApplicationHost _applicationHost;
        private readonly ExternalCatalogRegistry _providers;
        private readonly ILogger<IntegrationCatalogService> _logger;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private IntegrationCatalogResult? _cached;
        private DateTime _fetchedAt;

        /// <summary>Initializes the catalog service.</summary>
        public IntegrationCatalogService(
            IServerApplicationHost applicationHost,
            ExternalCatalogRegistry providers,
            ILogger<IntegrationCatalogService> logger)
        {
            _applicationHost = applicationHost;
            _providers = providers;
            _logger = logger;
        }

        /// <summary>Returns compatible catalog entries, using the bundled snapshot when GitHub is unavailable.</summary>
        public async Task<IntegrationCatalogResult> GetAsync(bool refresh, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!refresh && _cached != null && DateTime.UtcNow - _fetchedAt < CacheFor)
                {
                    return _cached;
                }

                var hostVersion = Plugin.Instance?.Version ?? typeof(Plugin).Assembly.GetName().Version ?? new Version(0, 0, 0);
                var jellyfinVersion = _applicationHost.ApplicationVersion;
                var installed = _providers.Get(ServerKind.Plex) != null;
                string? warning = null;
                var source = "github";
                IReadOnlyList<IntegrationView> entries;
                try
                {
                    var json = await FetchAsync(cancellationToken).ConfigureAwait(false);
                    entries = ParseCompatible(json, hostVersion, jellyfinVersion, RuntimeInformation.RuntimeIdentifier, installed);
                }
                catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException or JsonException or NotSupportedException) && !cancellationToken.IsCancellationRequested)
                {
                    _logger.LogInformation("[Federation] Integration catalog unavailable or invalid; using the bundled snapshot");
                    warning = "GitHub could not be reached or returned an invalid catalog. Showing the bundled catalog; no packages were downloaded.";
                    source = "bundled-fallback";
                    entries = ParseCompatible(ReadBundled(), hostVersion, jellyfinVersion, RuntimeInformation.RuntimeIdentifier, installed);
                }

                _cached = new IntegrationCatalogResult(source, warning, entries);
                _fetchedAt = DateTime.UtcNow;
                return _cached;
            }
            finally
            {
                _gate.Release();
            }
        }

        internal static IReadOnlyList<IntegrationView> ParseCompatible(
            string json,
            Version hostVersion,
            Version jellyfinVersion,
            string platform,
            bool plexInstalled)
        {
            if (json.Length > MaxCatalogBytes)
            {
                throw new InvalidOperationException("The integration catalog is too large.");
            }

            var catalog = JsonSerializer.Deserialize<IntegrationCatalog>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidOperationException("The integration catalog is empty.");
            if (catalog.SchemaVersion != 1 || catalog.Integrations == null || catalog.Integrations.Count > 64)
            {
                throw new InvalidOperationException("The integration catalog schema is not supported.");
            }

            var ids = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<IntegrationView>();
            foreach (var entry in catalog.Integrations)
            {
                if (!Valid(entry) || !ids.Add(entry.Id) || !Compatible(entry, hostVersion, jellyfinVersion, platform))
                {
                    continue;
                }

                result.Add(new IntegrationView(
                    entry.Id,
                    entry.Type,
                    entry.Name.Trim(),
                    entry.Summary.Trim(),
                    entry.Publisher.Trim(),
                    entry.BuiltinProviderId,
                    entry.InstallGuideUrl,
                    entry.Capabilities.ToArray(),
                    entry.Type == "addin" && entry.BuiltinProviderId == "Plex" && plexInstalled));
            }

            return result;
        }

        private static bool Compatible(IntegrationCatalogEntry entry, Version hostVersion, Version jellyfinVersion, string platform)
        {
            return Version.TryParse(entry.MinHostVersion, out var minimum)
                && Version.TryParse(entry.MaxHostVersion, out var maximum)
                && NormalizeVersion(hostVersion) >= NormalizeVersion(minimum)
                && NormalizeVersion(hostVersion) <= NormalizeVersion(maximum)
                && Version.TryParse(entry.JellyfinAbi, out var abi)
                && jellyfinVersion.Major == abi.Major
                && (entry.Platforms.Contains("all", StringComparer.OrdinalIgnoreCase)
                    || entry.Platforms.Contains(platform, StringComparer.OrdinalIgnoreCase));
        }

        private static Version NormalizeVersion(Version value)
            => new(value.Major, value.Minor, Math.Max(0, value.Build), Math.Max(0, value.Revision));

        private static bool Valid(IntegrationCatalogEntry? entry)
        {
            if (entry == null
                || !ValidText(entry.Id, 128)
                || !ValidText(entry.Type, 24)
                || entry.Type is not ("declarative" or "addin")
                || !ValidText(entry.Name, 100)
                || !ValidText(entry.Summary, 300)
                || !ValidText(entry.Publisher, 120)
                || !ValidText(entry.MinHostVersion, 40)
                || !ValidText(entry.MaxHostVersion, 40)
                || !ValidText(entry.JellyfinAbi, 40)
                || entry.Platforms == null
                || entry.Platforms.Count == 0
                || entry.Platforms.Count > 16
                || entry.Platforms.Any(p => !ValidText(p, 64))
                || entry.Capabilities == null
                || entry.Capabilities.Count > 16
                || entry.Capabilities.Any(c => !ValidText(c, 64)))
            {
                return false;
            }

            if (entry.Type == "declarative")
            {
                return ValidText(entry.BuiltinProviderId, 128) && string.IsNullOrEmpty(entry.InstallGuideUrl);
            }

            return entry.BuiltinProviderId == "Plex" && ValidGuide(entry.InstallGuideUrl);
        }

        private static bool ValidGuide(string? value)
            => Uri.TryCreate(value, UriKind.Absolute, out var uri)
                && uri.Scheme == Uri.UriSchemeHttps
                && string.IsNullOrEmpty(uri.UserInfo)
                && (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                    || uri.Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase));

        private static bool ValidText(string? value, int max)
            => !string.IsNullOrWhiteSpace(value) && value.Length <= max && value.All(c => !char.IsControl(c) && c != '<' && c != '>');

        private static async Task<string> FetchAsync(CancellationToken cancellationToken)
        {
            using var response = await CatalogHttp.GetAsync(CatalogUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaxCatalogBytes)
            {
                throw new InvalidOperationException("The integration catalog is too large.");
            }

            return await HttpContentReader.ReadTextAsync(response.Content, MaxCatalogBytes, cancellationToken).ConfigureAwait(false);
        }

        private static string ReadBundled()
        {
            var assembly = typeof(IntegrationCatalogService).Assembly;
            var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith("integrations.catalog.json", StringComparison.Ordinal));
            using var stream = assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException("The bundled integration catalog is missing.");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }

    /// <summary>One compatible integration shown in the dashboard.</summary>
    public sealed record IntegrationView(
        string Id,
        string Type,
        string Name,
        string Summary,
        string Publisher,
        string? BuiltinProviderId,
        string? InstallGuideUrl,
        IReadOnlyList<string> Capabilities,
        bool Installed);

    /// <summary>The catalog source and its compatible entries.</summary>
    public sealed record IntegrationCatalogResult(string Source, string? Warning, IReadOnlyList<IntegrationView> Integrations);

    internal sealed class IntegrationCatalog
    {
        public int SchemaVersion { get; set; }

        public List<IntegrationCatalogEntry> Integrations { get; set; } = new();
    }

    internal sealed class IntegrationCatalogEntry
    {
        public string Id { get; set; } = string.Empty;

        public string Type { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string Summary { get; set; } = string.Empty;

        public string Publisher { get; set; } = string.Empty;

        public string? BuiltinProviderId { get; set; }

        public string? InstallGuideUrl { get; set; }

        public string MinHostVersion { get; set; } = string.Empty;

        public string MaxHostVersion { get; set; } = string.Empty;

        public string JellyfinAbi { get; set; } = string.Empty;

        public List<string> Platforms { get; set; } = new();

        public List<string> Capabilities { get; set; } = new();
    }
}
