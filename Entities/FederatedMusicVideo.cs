using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using MediaBrowser.Controller.Entities.Audio;

namespace Jellyfin.Plugin.Federation.Entities
{
    /// <summary>Federated (remote, pathless) music video. See <see cref="FederatedItem"/>.</summary>
    public class FederatedMusicVideo : MusicVideo
    {
        /// <inheritdoc />
        public override LocationType LocationType => LocationType.Remote;
    }
}
