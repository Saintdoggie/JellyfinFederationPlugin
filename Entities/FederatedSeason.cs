using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.Federation.Entities
{
    /// <summary>Federated (remote, pathless) season. See <see cref="FederatedItem"/>.</summary>
    public class FederatedSeason : Season
    {
        /// <inheritdoc />
        public override LocationType LocationType => LocationType.Remote;
    }
}
