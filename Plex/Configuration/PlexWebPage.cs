using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Federation.Configuration
{
    /// <summary>Provides the optional Plex and Companion dashboard page.</summary>
    public sealed class PlexWebPage : IFederationWebPageProvider
    {
        /// <inheritdoc />
        public IEnumerable<PluginPageInfo> GetPages()
        {
            yield return new PluginPageInfo
            {
                Name = "FederationPlex",
                EmbeddedResourcePath = "Jellyfin.Plugin.Federation.Plex.Web.admin.plexPage.html",
                EnableInMainMenu = true,
                DisplayName = "Federation Plex",
                MenuIcon = "video_library"
            };
        }
    }
}
