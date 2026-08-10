using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Federation.Entities
{
    /// <summary>Federated (remote, pathless) photo. See <see cref="FederatedItem"/>.</summary>
    public class FederatedPhoto : Photo
    {
        /// <inheritdoc />
        public override LocationType LocationType => LocationType.Remote;
    }
}
