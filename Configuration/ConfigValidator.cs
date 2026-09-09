using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace Jellyfin.Plugin.Federation.Configuration
{
    /// <summary>
    /// Validates plugin configuration submitted through the API.
    /// </summary>
    public static class ConfigValidator
    {
        /// <summary>
        /// Validates a mapping name for use inside federation:// paths.
        /// Names may not contain '/' or ':' (they would corrupt path parsing).
        /// </summary>
        public static bool IsValidMappingName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            return name.IndexOf('/') < 0 && name.IndexOf(':') < 0;
        }

        /// <summary>
        /// Validates a remote server base URL. A reverse-proxy path is allowed,
        /// but credentials, query parameters, and fragments are not: every caller
        /// appends plugin/API paths to this value, and preserving any of those
        /// components would both produce a broken endpoint and risk persisting or
        /// logging a credential-bearing URL.
        /// </summary>
        public static bool IsValidServerUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return false;
            }

            return Uri.TryCreate(url, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                && !string.IsNullOrWhiteSpace(uri.Host)
                && string.IsNullOrEmpty(uri.UserInfo)
                && string.IsNullOrEmpty(uri.Query)
                && string.IsNullOrEmpty(uri.Fragment);
        }

        private static readonly HashSet<string> WellKnownLoopbackOrMetadataHosts = new(StringComparer.OrdinalIgnoreCase)
        {
            "localhost",
            "localhost.localdomain",
            "metadata",
            "metadata.google.internal",
            "metadata.google.com",
            "instance-data"
        };

        /// <summary>
        /// True when a URL's host is a loopback, RFC 1918, link-local, CGNAT/
        /// Tailscale (100.64.0.0/10), IPv6 unique-local/link-local, IPv4-mapped
        /// form of any of those, or a well-known loopback/cloud-metadata hostname.
        /// Used to catch a server's own public URL being auto-detected from a
        /// private-network request - e.g. an admin managing Jellyfin over their LAN
        /// when accepting a friend request - which silently hands a friend an
        /// address only reachable on that LAN or tailnet. Also used to skip
        /// unauthenticated callback/verify fetches that would otherwise SSRF into
        /// those ranges. Ordinary hostnames are not resolved here: DNS pointing at
        /// a private IP is normally deliberate (split-horizon DNS, Funnel
        /// <c>*.ts.net</c> names) rather than an accident.
        /// </summary>
        public static bool IsPrivateOrLoopbackHost(string? url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return false;
            }

            var host = uri.IdnHost;
            if (string.IsNullOrEmpty(host))
            {
                host = uri.Host;
            }

            if (IsWellKnownLoopbackOrMetadataHostname(host))
            {
                return true;
            }

            if (!IPAddress.TryParse(host, out var ip))
            {
                return false;
            }

            if (ip.IsIPv4MappedToIPv6)
            {
                ip = ip.MapToIPv4();
            }

            if (IPAddress.IsLoopback(ip))
            {
                return true;
            }

            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                var b = ip.GetAddressBytes();
                return b[0] == 10
                    || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                    || (b[0] == 192 && b[1] == 168)
                    || (b[0] == 169 && b[1] == 254)
                    || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);
            }

            if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            {
                // fc00::/7 (unique local) and fe80::/10 (link-local).
                var b = ip.GetAddressBytes();
                return (b[0] & 0xfe) == 0xfc || (b[0] == 0xfe && (b[1] & 0xc0) == 0x80);
            }

            return false;
        }

        private static bool IsWellKnownLoopbackOrMetadataHostname(string host)
        {
            if (string.IsNullOrEmpty(host))
            {
                return false;
            }

            if (WellKnownLoopbackOrMetadataHosts.Contains(host))
            {
                return true;
            }

            // RFC 6761: *.localhost is loopback even without a literal 127.0.0.1.
            return host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Validates a full configuration, returning all problems found.
        /// </summary>
        public static IReadOnlyList<string> Validate(PluginConfiguration config)
        {
            var errors = new List<string>();

            if (!string.IsNullOrEmpty(config.ServerUrl) && !IsValidServerUrl(config.ServerUrl))
            {
                errors.Add("ServerUrl must be an absolute http(s) URL.");
            }

            if (config.RefreshIntervalHours < 1)
            {
                errors.Add("RefreshIntervalHours must be at least 1.");
            }

            var servers = config.RemoteServers ?? new List<RemoteServer>();
            for (int i = 0; i < servers.Count; i++)
            {
                // A Companion entry represents a receiver of this server's catalog,
                // not a server this plugin connects to. It deliberately has no URL:
                // the generated connect code contains this server's address instead.
                // Treating that empty field like a Jellyfin/Plex source made every
                // unrelated settings save fail as soon as a Companion friend existed.
                if (servers[i].Kind == ServerKind.Companion && string.IsNullOrWhiteSpace(servers[i].Url))
                {
                    continue;
                }

                if (!IsValidServerUrl(servers[i].Url))
                {
                    errors.Add($"Remote server #{i + 1} ('{servers[i].Name}') has an invalid URL.");
                }
            }

            var mappings = config.LibraryMappings ?? new List<LibraryMapping>();
            for (int i = 0; i < mappings.Count; i++)
            {
                if (!IsValidMappingName(mappings[i].LocalLibraryName))
                {
                    errors.Add($"Mapping #{i + 1} has an invalid library name ('{mappings[i].LocalLibraryName}'). Names may not be empty or contain '/' or ':'.");
                }
            }

            var duplicateNames = mappings
                .Where(m => !string.IsNullOrWhiteSpace(m.LocalLibraryName))
                .GroupBy(m => m.LocalLibraryName, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key);
            foreach (var name in duplicateNames)
            {
                errors.Add($"Duplicate library mapping name: '{name}'.");
            }

            return errors;
        }
    }
}
