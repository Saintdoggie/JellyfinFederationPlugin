using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.Federation.Configuration;
using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Security;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.DependencyInjection;
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
        private readonly LibraryProvisioningService _provisioning;
        private readonly IServiceProvider _serviceProvider;

        /// <summary>
        /// Initializes a new instance of the <see cref="Plugin"/> class.
        /// </summary>
        public Plugin(
            IApplicationPaths applicationPaths,
            IXmlSerializer xmlSerializer,
            ILogger<Plugin> logger,
            ILibraryManager libraryManager,
            WebClientInjector webClientInjector,
            LibraryProvisioningService provisioning,
            IServiceProvider serviceProvider)
            : base(applicationPaths, xmlSerializer)
        {
            _logger = logger;
            _libraryManager = libraryManager;
            _webClientInjector = webClientInjector;
            _provisioning = provisioning;
            _serviceProvider = serviceProvider;
            Instance = this;

            // Must run before anything else touches Configuration (the base
            // class's getter lazily deserializes the on-disk XML the first time
            // it's read, which happens right here): the key ring has to exist
            // before DecryptConfigurationInPlace tries to use it, and every other
            // call site in the plugin expects Configuration to already hold
            // plaintext keys by the time it can possibly see it.
            // DataFolderPath is safe to build a key ring under across plugin
            // upgrades specifically because it is NOT version-scoped in practice:
            // BasePlugin<T>'s own constructor computes it from the assembly's bare
            // file name (Jellyfin.Plugin.Federation, unchanged release to release)
            // and only appends "_<version>" if that unversioned folder doesn't
            // already exist AND Version is already set - but that check runs
            // before SetAttributes has set Version, so it's always null there and
            // the version suffix never actually triggers (verified directly
            // against MediaBrowser.Common's BasePluginOfT.cs source for the
            // Jellyfin.Controller version this project targets). If a future
            // Jellyfin release changes that, this key ring - and this plugin's
            // existing federation-cache.json (see GetDefaultCachePath) - would
            // both need revisiting together, not just this one.
            ApiKeyProtector.Initialize(Path.Combine(DataFolderPath, "keys"));
            DecryptConfigurationInPlace();

            _logger.LogInformation("=== Jellyfin Federation Plugin v{Version} Initialized ===", Version);
        }

        /// <summary>
        /// Decrypts the API key fields on the just-loaded <see cref="Configuration"/>
        /// in place. Safe to mutate directly (rather than needing a clone, the way
        /// <see cref="SaveConfiguration(PluginConfiguration)"/> does) because this
        /// runs once, synchronously, before the constructor returns - nothing else
        /// in the process can have a reference to this object yet. A value that
        /// isn't valid ciphertext is left as-is by <see cref="ApiKeyProtector.Unprotect"/>
        /// rather than throwing, so an existing install upgrading from a version
        /// before this feature existed keeps working with no explicit migration -
        /// the plaintext keys it already has just get encrypted on the next save.
        /// </summary>
        private void DecryptConfigurationInPlace()
        {
            var config = Configuration;

            foreach (var server in config.RemoteServers ?? new List<RemoteServer>())
            {
                server.ApiKey = ApiKeyProtector.Unprotect(server.ApiKey);
                server.IssuedApiKey = ApiKeyProtector.Unprotect(server.IssuedApiKey);
            }

            config.InternalRelayApiKey = ApiKeyProtector.Unprotect(config.InternalRelayApiKey);

            foreach (var request in config.OutgoingFriendRequests ?? new List<FriendRequest>())
            {
                request.ApiKey = ApiKeyProtector.Unprotect(request.ApiKey);
            }

            foreach (var request in config.IncomingFriendRequests ?? new List<FriendRequest>())
            {
                request.ApiKey = ApiKeyProtector.Unprotect(request.ApiKey);
            }
        }

        /// <inheritdoc />
        /// <remarks>
        /// Encrypts the API key fields before they hit disk, on a deep clone -
        /// never the live <see cref="Configuration"/> object itself, which every
        /// other call site in the plugin (~40 of them) expects to keep holding
        /// plaintext for the rest of the process's life. Overriding this one
        /// method is sufficient for the whole write path: the base class's own
        /// parameterless <c>SaveConfiguration()</c> and <c>UpdateConfiguration(...)</c>
        /// both funnel into this same virtual method, so no other call site needs
        /// to change. The clone goes through JSON rather than a hand-written
        /// field-by-field copy specifically so a future property added to
        /// <see cref="PluginConfiguration"/> or its nested types can never be
        /// silently dropped by this step forgetting to copy it - the same JSON
        /// round-trip the settings page's save flow already puts this exact type
        /// through on every request.
        /// </remarks>
        public override void SaveConfiguration(PluginConfiguration config)
        {
            base.SaveConfiguration(CloneWithEncryptedApiKeys(config));
        }

        private static PluginConfiguration CloneWithEncryptedApiKeys(PluginConfiguration source)
        {
            var clone = JsonSerializer.Deserialize<PluginConfiguration>(JsonSerializer.Serialize(source)) ?? new PluginConfiguration();

            foreach (var server in clone.RemoteServers ?? new List<RemoteServer>())
            {
                server.ApiKey = ApiKeyProtector.Protect(server.ApiKey);
                server.IssuedApiKey = ApiKeyProtector.Protect(server.IssuedApiKey);
            }

            clone.InternalRelayApiKey = ApiKeyProtector.Protect(clone.InternalRelayApiKey);

            foreach (var request in clone.OutgoingFriendRequests ?? new List<FriendRequest>())
            {
                request.ApiKey = ApiKeyProtector.Protect(request.ApiKey);
            }

            foreach (var request in clone.IncomingFriendRequests ?? new List<FriendRequest>())
            {
                request.ApiKey = ApiKeyProtector.Protect(request.ApiKey);
            }

            return clone;
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

            // Auto-provisioned libraries (and shadow media paths merged into a
            // pre-existing user library) were never cleaned up here - only the
            // items inside them were. LibraryProvisioningService.RemoveAllAsync
            // already existed for exactly this ("used on uninstall / reset" per
            // its own doc comment) but was never actually wired in, so Jellyfin
            // was left holding a library definition pointing at this plugin's
            // data-folder shadow path right up until Jellyfin's own post-uninstall
            // cleanup deletes that folder out from under it - a library with a
            // location that no longer exists on disk, which is what actually broke.
            // OnUninstalling is synchronous (Jellyfin's own plugin-manager contract),
            // so this blocks on the async call rather than leaving library cleanup
            // to race the folder deletion that follows immediately after this method
            // returns; safe to block here since this runs on the plugin manager's
            // own background uninstall flow, not a request thread with a captured
            // SynchronizationContext.
            try
            {
                _provisioning.RemoveAllAsync().GetAwaiter().GetResult();
                _logger.LogInformation("[Federation] Plugin uninstall: removed auto-provisioned libraries and detached shadow media paths");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Federation] Plugin uninstall: failed to remove provisioned libraries; Jellyfin may be left with a library pointing at a path this uninstall is about to delete");
            }

            // Undo WebClientInjector's file-write injection too, so uninstalling
            // doesn't leave a <script> tag permanently baked into jellyfin-web
            // pointing at a route (/Plugins/Federation/ClientScript) that no
            // longer exists once this plugin is gone. The serve-time middleware
            // injection needs no equivalent cleanup - it stops running the moment
            // this plugin's assembly is unloaded.
            _webClientInjector.RemoveBadgeScriptInjection();

            // The "Federation internal relay" key is a real, admin-equivalent
            // Jellyfin API key - the only one this plugin ever creates. It lives
            // in Jellyfin's own key store (not this plugin's config folder, which
            // Jellyfin core deletes right after this method returns), so without
            // this it would survive the uninstall as a standing admin credential
            // nobody remembers minting. IAuthenticationManager is DI-scoped while
            // this plugin instance is a singleton, so resolve it through a
            // short-lived scope - the same pattern FederationSyncService uses for
            // FederationFriendService.
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var authManager = scope.ServiceProvider.GetService(typeof(IAuthenticationManager)) as IAuthenticationManager;
                if (authManager != null)
                {
                    FederationFriendService.RevokeInternalRelayApiKeysAsync(authManager).GetAwaiter().GetResult();
                    _logger.LogInformation("[Federation] Plugin uninstall: revoked the internal relay API key(s)");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Federation] Plugin uninstall: could not revoke the internal relay API key; delete it manually under Dashboard > API Keys");
            }

            // A custom CachePath points outside the plugin data folder that
            // Jellyfin core removes on its own after this - delete the cache file
            // there too so uninstalling really does leave nothing behind.
            try
            {
                var customCachePath = Configuration.CachePath;
                if (!string.IsNullOrEmpty(customCachePath)
                    && !string.Equals(customCachePath, GetDefaultCachePath(), StringComparison.OrdinalIgnoreCase)
                    && File.Exists(customCachePath))
                {
                    File.Delete(customCachePath);
                    _logger.LogInformation("[Federation] Plugin uninstall: deleted custom cache file {Path}", customCachePath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Federation] Plugin uninstall: could not delete the custom cache file");
            }

            base.OnUninstalling();
        }
    }
}
