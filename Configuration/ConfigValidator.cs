using System;
using System.Collections.Generic;
using System.Linq;

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
        /// Validates a remote server URL.
        /// </summary>
        public static bool IsValidServerUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return false;
            }

            return Uri.TryCreate(url, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        }

        /// <summary>
        /// True when a URL's host is a loopback or RFC 1918 private-range IP address
        /// (127.0.0.0/8, 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16), or an IPv6
        /// loopback/unique-local equivalent. Used to catch a server's own public URL
        /// being auto-detected from a private-network request - e.g. an admin
        /// managing Jellyfin over their LAN when accepting a friend request - which
        /// silently hands a friend an address only reachable on that LAN. A hostname
        /// (not a literal IP) is never flagged: DNS resolution isn't attempted here,
        /// and a hostname pointing at a private IP is normally deliberate (split-horizon
        /// DNS, VPN-only setups) rather than an accident.
        /// </summary>
        public static bool IsPrivateOrLoopbackHost(string? url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return false;
            }

            if (!System.Net.IPAddress.TryParse(uri.Host, out var ip))
            {
                return false;
            }

            if (System.Net.IPAddress.IsLoopback(ip))
            {
                return true;
            }

            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                var b = ip.GetAddressBytes();
                return b[0] == 10
                    || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                    || (b[0] == 192 && b[1] == 168);
            }

            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                // fc00::/7 (unique local) and fe80::/10 (link-local).
                var b = ip.GetAddressBytes();
                return (b[0] & 0xfe) == 0xfc || (b[0] == 0xfe && (b[1] & 0xc0) == 0x80);
            }

            return false;
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
