using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Federation.Entities
{
    /// <summary>Federated (remote, pathless) photo album. See <see cref="FederatedItem"/>.</summary>
    public class FederatedPhotoAlbum : PhotoAlbum
    {
        /// <inheritdoc />
        public override LocationType LocationType => LocationType.Remote;
    }
}
