using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using MediaBrowser.Controller.Entities.Audio;

namespace Jellyfin.Plugin.Federation.Entities
{
    /// <summary>Federated (remote, pathless) audio item. See <see cref="FederatedItem"/>.</summary>
    public class FederatedAudio : Audio
    {
        /// <inheritdoc />
        public override LocationType LocationType => LocationType.Remote;
    }
}
