using System;
using System.Collections.Generic;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.FederationPlex
{
    /// <summary>This plugin keeps no settings of its own: Plex settings live with Jellyfin Federation.</summary>
    public class FederationPlexConfiguration : BasePluginConfiguration
    {
    }

    /// <summary>
    /// "Jellyfin Federation Plex": Plex support for Jellyfin Federation as its own
    /// installable plugin, replacing the DLL that had to be copied by hand.
    /// </summary>
    public class FederationPlexPlugin : BasePlugin<FederationPlexConfiguration>, IHasWebPages
    {
        /// <summary>Initializes a new instance of the <see cref="FederationPlexPlugin"/> class.</summary>
        public FederationPlexPlugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
        }

        /// <inheritdoc />
        public override string Name => "Jellyfin Federation Plex";

        /// <inheritdoc />
        public override Guid Id => Guid.Parse("eae4fa49-1541-4813-af83-5802c7f6a674");

        /// <inheritdoc />
        public override string Description => "Plex libraries, Companion connections and Plex metadata for Jellyfin Federation.";

        /// <inheritdoc />
        public IEnumerable<PluginPageInfo> GetPages()
        {
            // The Plex page itself ("Federation Plex" in the menu) is listed by
            // Jellyfin Federation once the add-in runs; this is only the status
            // page behind this plugin's Settings button.
            yield return new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = "Jellyfin.Plugin.FederationPlex.Web.status.html"
            };
        }
    }

    /// <summary>Starts the add-in while Jellyfin is still collecting services.</summary>
    public sealed class FederationPlexRegistrator : IPluginServiceRegistrator
    {
        /// <inheritdoc />
        public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
            => PlexAddInHost.Start(serviceCollection, applicationHost);
    }

    /// <summary>Tells an administrator whether Plex support is running, and what to do when it is not.</summary>
    [ApiController]
    [Route("Plugins/FederationPlex")]
    public sealed class FederationPlexStatusController : ControllerBase
    {
        /// <summary>Gets the add-in status. Administrators only; contains versions and advice, no secrets.</summary>
        [HttpGet("Status")]
        [Authorize(Policy = "RequiresElevation")]
        public IActionResult GetStatus()
        {
            var status = PlexAddInHost.Status;
            return Ok(new
            {
                state = status.State.ToString(),
                running = status.State == PlexHostState.Loaded,
                message = status.Message,
                federationVersion = status.CoreVersion,
                version = status.OwnVersion
            });
        }
    }
}
