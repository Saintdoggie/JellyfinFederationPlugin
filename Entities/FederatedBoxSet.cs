using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using MediaBrowser.Controller.Entities.Movies;

namespace Jellyfin.Plugin.Federation.Entities
{
    /// <summary>Federated (remote, pathless) box set. See <see cref="FederatedItem"/>.</summary>
    public class FederatedBoxSet : BoxSet
    {
        /// <inheritdoc />
        public override LocationType LocationType => LocationType.Remote;
    }
}
