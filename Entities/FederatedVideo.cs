using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Federation.Entities
{
    /// <summary>Federated (remote, pathless) video. See <see cref="FederatedItem"/>.</summary>
    public class FederatedVideo : Video
    {
        /// <inheritdoc />
        public override LocationType LocationType => LocationType.Remote;
    }
}
