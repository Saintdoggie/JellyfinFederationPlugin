using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Auto-provisions Jellyfin virtual libraries for each configured
    /// <see cref="LibraryMapping"/>. Idempotent: skips folders that already exist.
    /// When a mapping's name collides with a pre-existing non-federation library,
    /// the federation shadow folder is added as an additional media path on that
    /// library (merge) instead of creating a new library.
    /// </summary>
    public class LibraryProvisioningService
    {
        private const string FederationSubFolder = "federation";

        private readonly ILibraryManager _libraryManager;
        private readonly ILogger<LibraryProvisioningService> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="LibraryProvisioningService"/> class.
        /// </summary>
        public LibraryProvisioningService(
            ILibraryManager libraryManager,
            ILogger<LibraryProvisioningService> logger)
        {
            _libraryManager = libraryManager;
            _logger = logger;
        }

        /// <summary>
        /// Resolves the on-disk root directory used for federation shadow folders:
        /// <c>&lt;plugin data dir&gt;/federation/</c>. Returns an empty string when
        /// the plugin instance is not yet available.
        /// </summary>
        internal static string GetFederationRoot()
        {
            var dataPath = Plugin.Instance?.DataFolderPath;
            return string.IsNullOrEmpty(dataPath)
                ? string.Empty
                : Path.Combine(dataPath, FederationSubFolder);
        }

        /// <summary>
        /// Ensures a virtual library exists for each enabled, auto-provision mapping,
        /// and removes libraries for disabled/removed mappings. Idempotent.
        /// </summary>
        public async Task EnsureLibrariesAsync(CancellationToken cancellationToken = default)
        {
            var config = Plugin.Instance?.Configuration;
            if (config?.LibraryMappings == null)
            {
                return;
            }

            var enabledMappings = config.LibraryMappings.Where(m => m.Enabled && m.AutoProvision).ToList();
            var disabledMappingNames = config.LibraryMappings
                .Where(m => !m.Enabled || !m.AutoProvision)
                .Select(m => m.LocalLibraryName)
                .ToList();

            foreach (var mapping in enabledMappings)
            {
                try
                {
                    await EnsureLibraryAsync(mapping, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[Federation] Error provisioning library {Name}", mapping.LocalLibraryName);
                }
            }

            await RemoveDisabledAsync(disabledMappingNames).ConfigureAwait(false);
        }

        /// <summary>
        /// Removes all federation virtual libraries (used on uninstall / reset).
        /// </summary>
        public async Task RemoveAllAsync(CancellationToken cancellationToken = default)
        {
            var config = Plugin.Instance?.Configuration;
            if (config?.LibraryMappings == null)
            {
                return;
            }

            foreach (var mapping in config.LibraryMappings)
            {
                try
                {
                    await RemoveLibraryAsync(mapping.LocalLibraryName).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Federation] Could not remove library {Name}", mapping.LocalLibraryName);
                }
            }
        }

        private async Task EnsureLibraryAsync(LibraryMapping mapping, CancellationToken cancellationToken)
        {
            var federationRoot = GetFederationRoot();
            if (string.IsNullOrEmpty(federationRoot))
            {
                _logger.LogWarning("[Federation] Plugin data path unavailable; cannot provision {Name}", mapping.LocalLibraryName);
                return;
            }

            var shadowPath = Path.Combine(federationRoot, SafeFolderName(mapping.LocalLibraryName));
            if (!Directory.Exists(shadowPath))
            {
                Directory.CreateDirectory(shadowPath);
            }

            var existing = _libraryManager.GetVirtualFolders()?.FirstOrDefault(vf =>
                string.Equals(vf.Name, mapping.LocalLibraryName, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                if (IsFederationFolder(existing, federationRoot))
                {
                    // Already plugin-managed (either standalone or merged). Make sure the
                    // shadow path is registered as a media path (handles upgrades from the
                    // old federation://-URI scheme that never successfully provisioned).
                    if (!HasLocation(existing, shadowPath))
                    {
                        try
                        {
                            _libraryManager.AddMediaPath(existing.Name, new MediaPathInfo { Path = shadowPath });
                            _logger.LogInformation("[Federation] Attached shadow path to existing federation library {Name}", mapping.LocalLibraryName);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "[Federation] Could not attach shadow path to {Name}", mapping.LocalLibraryName);
                        }
                    }

                    // Clean up any legacy federation:// location that an older plugin version
                    // may have registered on this library (no-op when none is present).
                    RemoveLegacyFederationLocations(existing);

                    _logger.LogDebug("[Federation] Library {Name} already provisioned", mapping.LocalLibraryName);
                    return;
                }

                // A non-federation library with the same name already exists: merge by
                // adding the shadow path as an additional media location on it.
                if (HasLocation(existing, shadowPath))
                {
                    _logger.LogDebug("[Federation] Library {Name} already merged with federation", mapping.LocalLibraryName);
                    return;
                }

                try
                {
                    _libraryManager.AddMediaPath(existing.Name, new MediaPathInfo { Path = shadowPath });
                    _logger.LogInformation("[Federation] Merged federation content into existing library {Name}", mapping.LocalLibraryName);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Federation] Could not merge into existing library {Name}", mapping.LocalLibraryName);
                }

                return;
            }

            var libraryOptions = new LibraryOptions
            {
                PathInfos = new[]
                {
                    new MediaPathInfo
                    {
                        Path = shadowPath
                    }
                }
            };

            await _libraryManager.AddVirtualFolder(
                mapping.LocalLibraryName,
                GetCollectionType(mapping.MediaType),
                libraryOptions,
                refreshLibrary: true).ConfigureAwait(false);

            _logger.LogInformation("[Federation] Provisioned library {Name} ({Type})", mapping.LocalLibraryName, mapping.MediaType);
        }

        private async Task RemoveDisabledAsync(System.Collections.Generic.List<string> names)
        {
            var virtualFolders = _libraryManager.GetVirtualFolders();
            if (virtualFolders == null || names.Count == 0)
            {
                return;
            }

            var federationRoot = GetFederationRoot();

            foreach (var name in names)
            {
                var vf = virtualFolders.FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));
                if (vf == null || !IsFederationFolder(vf, federationRoot))
                {
                    // Never remove libraries the plugin did not create or merge into.
                    continue;
                }

                try
                {
                    await RemoveLibraryAsync(name).ConfigureAwait(false);
                    _logger.LogInformation("[Federation] Removed disabled library {Name}", name);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Federation] Could not remove disabled library {Name}", name);
                }
            }
        }

        private async Task RemoveLibraryAsync(string name)
        {
            var federationRoot = GetFederationRoot();
            if (string.IsNullOrEmpty(federationRoot))
            {
                return;
            }

            var vf = _libraryManager.GetVirtualFolders()?.FirstOrDefault(v =>
                string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));
            if (vf == null || !IsFederationFolder(vf, federationRoot))
            {
                return;
            }

            var shadowPath = Path.Combine(federationRoot, SafeFolderName(name));
            var ownLocations = vf.Locations?
                .Where(l => IsUnderFederationRoot(l, federationRoot))
                .ToList() ?? new System.Collections.Generic.List<string>();

            if (ownLocations.Count == 0)
            {
                return;
            }

            // If the library has any non-federation locations, it's a user library we
            // merged into: only detach our shadow path(s), never delete the library.
            var hasUserLocations = vf.Locations != null
                && vf.Locations.Any(l => !IsUnderFederationRoot(l, federationRoot));

            if (hasUserLocations)
            {
                foreach (var loc in ownLocations)
                {
                    try
                    {
                        _libraryManager.RemoveMediaPath(vf.Name, loc);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[Federation] Could not detach federation media path {Path} from {Name}", loc, name);
                    }
                }

                _logger.LogInformation("[Federation] Detached federation media path(s) from merged library {Name}", name);
                return;
            }

            // All locations are plugin-owned: safe to delete the whole virtual folder.
            await _libraryManager.RemoveVirtualFolder(name, refreshLibrary: true).ConfigureAwait(false);
        }

        /// <summary>
        /// Returns true when the virtual folder is plugin-managed: any of its locations
        /// lives under <paramref name="federationRoot"/> OR carries the legacy
        /// <c>federation://</c> URI scheme from a prior plugin version.
        /// </summary>
        internal static bool IsFederationFolder(VirtualFolderInfo vf, string federationRoot)
        {
            if (vf.Locations == null)
            {
                return false;
            }

            foreach (var l in vf.Locations)
            {
                if (string.IsNullOrEmpty(l))
                {
                    continue;
                }

                if (l.StartsWith("federation://", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (!string.IsNullOrEmpty(federationRoot) && IsUnderFederationRoot(l, federationRoot))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// True when <paramref name="path"/> is the same as or a descendant of
        /// <paramref name="federationRoot"/>. Uses normalized full paths with
        /// trailing separators trimmed to avoid false prefix matches
        /// (e.g. <c>/x/federation2</c> vs <c>/x/federation</c>).
        /// </summary>
        internal static bool IsUnderFederationRoot(string path, string federationRoot)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(federationRoot))
            {
                return false;
            }

            string normalizedPath;
            string normalizedRoot;
            try
            {
                normalizedPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                normalizedRoot = Path.GetFullPath(federationRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch (Exception)
            {
                // Path.GetFullPath can throw on invalid chars or URI schemes; fall back to
                // ordinal comparison with separator boundary.
                normalizedPath = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                normalizedRoot = federationRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }

            if (string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var rootWithSep = normalizedRoot + Path.DirectorySeparatorChar;
            return normalizedPath.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase)
                || normalizedPath.StartsWith(normalizedRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Returns true when the virtual folder already has <paramref name="path"/> as one
        /// of its media locations (ordinal, case-insensitive).
        /// </summary>
        private static bool HasLocation(VirtualFolderInfo vf, string path)
        {
            if (vf.Locations == null || string.IsNullOrEmpty(path))
            {
                return false;
            }

            foreach (var l in vf.Locations)
            {
                if (string.Equals(l, path, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Detaches any legacy <c>federation://</c> URI locations from a virtual folder
        /// that is otherwise plugin-managed. Best-effort; failures are logged.
        /// </summary>
        private void RemoveLegacyFederationLocations(VirtualFolderInfo vf)
        {
            if (vf.Locations == null)
            {
                return;
            }

            foreach (var l in vf.Locations)
            {
                if (string.IsNullOrEmpty(l) || !l.StartsWith("federation://", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    _libraryManager.RemoveMediaPath(vf.Name, l);
                    _logger.LogInformation("[Federation] Removed legacy federation:// location {Path} from {Name}", l, vf.Name);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Federation] Could not remove legacy location {Path} from {Name}", l, vf.Name);
                }
            }
        }

        /// <summary>
        /// Converts a mapping's library name into a safe filesystem folder name.
        /// <see cref="ConfigValidator"/> already rejects <c>/</c> and <c>:</c>, so this
        /// is a defensive pass that also strips other OS-reserved path separators and
        /// trims whitespace. Throws on empty/whitespace-only names.
        /// </summary>
        internal static string SafeFolderName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("Library mapping name must not be empty.", nameof(name));
            }

            var trimmed = name.Trim();
            var chars = trimmed.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                var c = chars[i];
                if (c == Path.DirectorySeparatorChar
                    || c == Path.AltDirectorySeparatorChar
                    || c == ':'
                    || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Control)
                {
                    chars[i] = '_';
                }
            }

            return new string(chars);
        }

        private static CollectionTypeOptions? GetCollectionType(string mediaType)
        {
            return mediaType?.ToLowerInvariant() switch
            {
                "movie" => CollectionTypeOptions.movies,
                "series" or "season" or "episode" => CollectionTypeOptions.tvshows,
                "audio" or "musicalbum" or "musicvideo" => CollectionTypeOptions.music,
                "photo" or "photoalbum" => CollectionTypeOptions.homevideos,
                "book" => CollectionTypeOptions.books,
                _ => null
            };
        }
    }
}
