using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.Federation.Entities
{
    /// <summary>
    /// Federated (remote, pathless) episode. See <see cref="FederatedItem"/>. Without this
    /// override an episode's LocationType would resolve to Virtual (Path is null), and the
    /// Jellyfin web client would paint a "Missing" badge on it.
    /// </summary>
    public class FederatedEpisode : Episode
    {
        /// <inheritdoc />
        public override LocationType LocationType => LocationType.Remote;
    }
}
