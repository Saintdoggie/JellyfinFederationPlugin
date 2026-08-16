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
        /// Validates a directory username: letters, digits, underscore, and
        /// hyphen only, 3-32 characters. Kept simple and URL-safe with no
        /// escaping needed, since it appears directly in directory search
        /// results and (eventually) invite/profile URLs.
        /// </summary>
        public static bool IsValidUsername(string? username)
        {
            if (string.IsNullOrWhiteSpace(username) || username.Length is < 3 or > 32)
            {
                return false;
            }

            return username.All(c => char.IsLetterOrDigit(c) || c is '_' or '-');
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

            if (config.LocalUploadCapacityMbps < 0)
            {
                errors.Add("LocalUploadCapacityMbps cannot be negative.");
            }

            if (config.AutoManageUploadBudget && config.LocalUploadCapacityMbps <= 0)
            {
                errors.Add("Set an upload capacity before enabling automatic upload budget management.");
            }

            if (!string.IsNullOrEmpty(config.LocalUsername) && !IsValidUsername(config.LocalUsername))
            {
                errors.Add("LocalUsername must be 3-32 characters: letters, digits, underscore, or hyphen only.");
            }

            if (!string.IsNullOrEmpty(config.DirectoryServerUrl) && !IsValidServerUrl(config.DirectoryServerUrl))
            {
                errors.Add("DirectoryServerUrl must be an absolute http(s) URL.");
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
