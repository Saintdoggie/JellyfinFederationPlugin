using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Resolvers;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Resolvers
{
    /// <summary>
    /// Resolves federation:// paths to virtual Jellyfin items by consulting the live cache.
    /// </summary>
    public class FederationItemResolver : IItemResolver
    {
        private readonly ILogger<FederationItemResolver> _logger;
        private readonly Services.FederationLibraryManager _federationManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="FederationItemResolver"/> class.
        /// </summary>
        public FederationItemResolver(
            ILogger<FederationItemResolver> logger,
            Services.FederationLibraryManager federationManager)
        {
            _logger = logger;
            _federationManager = federationManager;
        }

        /// <inheritdoc />
        public ResolverPriority Priority => ResolverPriority.Second;

        /// <inheritdoc />
        public BaseItem? ResolvePath(ItemResolveArgs args)
        {
            if (args == null || string.IsNullOrEmpty(args.Path))
            {
                return null;
            }

            if (!args.Path.StartsWith("federation://", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            try
            {
                var item = _federationManager.ResolvePath(args.Path);
                if (item == null)
                {
                    _logger.LogDebug("[Federation] No cache entry for {Path}", args.Path);
                    return null;
                }

                _logger.LogDebug("[Federation] Resolved {Path} -> {Name} ({Type})", args.Path, item.Name, item.GetType().Name);
                return item;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Federation] Error resolving federation path: {Path}", args.Path);
                return null;
            }
        }
    }
}
