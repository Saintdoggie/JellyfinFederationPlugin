using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using MediaBrowser.Controller.Entities.Audio;

namespace Jellyfin.Plugin.Federation.Entities
{
    /// <summary>Federated (remote, pathless) music album. See <see cref="FederatedItem"/>.</summary>
    public class FederatedMusicAlbum : MusicAlbum
    {
        /// <inheritdoc />
        public override LocationType LocationType => LocationType.Remote;
    }
}
