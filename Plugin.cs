using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Federation.Configuration;
using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation
{
    /// <summary>
    /// Jellyfin Federation Plugin - aggregate content from multiple Jellyfin servers.
    /// </summary>
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        private readonly ILogger<Plugin> _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly WebClientInjector _webClientInjector;

        /// <summary>
        /// Initializes a new instance of the <see cref="Plugin"/> class.
        /// </summary>
        public Plugin(
            IApplicationPaths applicationPaths,
            IXmlSerializer xmlSerializer,
            ILogger<Plugin> logger,
            ILibraryManager libraryManager,
            WebClientInjector webClientInjector)
            : base(applicationPaths, xmlSerializer)
        {
            _logger = logger;
            _libraryManager = libraryManager;
            _webClientInjector = webClientInjector;
            Instance = this;
            _logger.LogInformation("=== Jellyfin Federation Plugin v{Version} Initialized ===", Version);
        }

        /// <inheritdoc />
        public override string Name => "Jellyfin Federation";

        /// <inheritdoc />
        public override Guid Id => Guid.Parse("495feadb-d27f-46c3-bb9b-0732ae8926fa");

        /// <inheritdoc />
        public override string Description => "Aggregate content from multiple Jellyfin servers into unified virtual libraries.";

        /// <summary>
        /// Gets the plugin singleton instance.
        /// </summary>
        public static Plugin? Instance { get; private set; }

        /// <summary>
        /// Resolves the default cache path inside the plugin data directory.
        /// </summary>
        public string GetDefaultCachePath() => System.IO.Path.Combine(DataFolderPath, "federation-cache.json");

        /// <inheritdoc />
        public IEnumerable<PluginPageInfo> GetPages()
        {
            yield return new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html",
                EnableInMainMenu = true,
                DisplayName = "Federation",
                MenuIcon = "public"
            };
        }

        /// <summary>
        /// Called by Jellyfin's plugin manager right before this plugin's assembly
        /// directory is removed. Every Movie/Series/Season/Episode this plugin ever
        /// created is a virtual item with no real file underneath it (see
        /// <see cref="FederationLibraryManager.MaterializeItem"/>) - once the plugin
        /// is gone, nothing will ever provide a MediaSource for them or clean them
        /// up on the next sync (that sync will never run again), so they would sit
        /// in the library forever as dead, unplayable entries. Deleted here,
        /// synchronously, so the library is clean by the time uninstall finishes.
        /// The Jellyfin core handles removing this plugin's own DLL/meta.json/data
        /// folder after this method returns - that part is not this plugin's job
        /// and nothing here touches it.
        /// </summary>
        public override void OnUninstalling()
        {
            try
            {
                var mappings = Configuration.LibraryMappings ?? new List<LibraryMapping>();
                var root = _libraryManager.GetUserRootFolder();
                var removed = 0;

                foreach (var mapping in mappings)
                {
                    var libraryFolder = root.Children.OfType<Folder>()
                        .FirstOrDefault(f => string.Equals(f.Name, mapping.LocalLibraryName, StringComparison.OrdinalIgnoreCase));
                    if (libraryFolder == null)
                    {
                        continue;
                    }

                    var federatedItems = libraryFolder.GetRecursiveChildren()
                        .Where(i => FederationLibraryManager.GetFederationKey(i) != null)
                        .ToList();

                    foreach (var item in federatedItems)
                    {
                        _libraryManager.DeleteItem(item, new DeleteOptions { DeleteFileLocation = false });
                        removed++;
                    }
                }

                _logger.LogInformation(
                    "[Federation] Plugin uninstall: removed {Count} federated item(s) across {MappingCount} mapped librar{Suffix} before the plugin is unloaded",
                    removed,
                    mappings.Count,
                    mappings.Count == 1 ? "y" : "ies");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Federation] Plugin uninstall: failed to remove federated items; some virtual items may be left behind");
            }

            // Undo WebClientInjector's file-write injection too, so uninstalling
            // doesn't leave a <script> tag permanently baked into jellyfin-web
            // pointing at a route (/Plugins/Federation/ClientScript) that no
            // longer exists once this plugin is gone. The serve-time middleware
            // injection needs no equivalent cleanup - it stops running the moment
            // this plugin's assembly is unloaded.
            _webClientInjector.RemoveBadgeScriptInjection();

            base.OnUninstalling();
        }
    }
}
