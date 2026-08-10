using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.Federation.Entities
{
    /// <summary>Federated (remote, pathless) series. See <see cref="FederatedItem"/>.</summary>
    public class FederatedSeries : Series
    {
        /// <inheritdoc />
        public override LocationType LocationType => LocationType.Remote;
    }
}
