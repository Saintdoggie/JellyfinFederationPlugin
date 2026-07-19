using System;
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
    /// Replaces the old <c>FederationVirtualFolderManager</c>.
    /// </summary>
    public class LibraryProvisioningService
    {
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
            var existing = _libraryManager.GetVirtualFolders()?.FirstOrDefault(vf =>
                string.Equals(vf.Name, mapping.LocalLibraryName, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                _logger.LogDebug("[Federation] Library {Name} already provisioned", mapping.LocalLibraryName);
                return;
            }

            var libraryOptions = new LibraryOptions
            {
                PathInfos = new[]
                {
                    new MediaPathInfo
                    {
                        Path = $"federation://{mapping.LocalLibraryName}"
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

            foreach (var name in names)
            {
                var vf = virtualFolders.FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));
                if (vf == null)
                {
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
            await _libraryManager.RemoveVirtualFolder(name, refreshLibrary: true).ConfigureAwait(false);
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
