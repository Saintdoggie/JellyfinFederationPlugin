using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Federation.Entities
{
    /// <summary>Federated (remote, pathless) book. See <see cref="FederatedItem"/>.</summary>
    public class FederatedBook : Book
    {
        /// <inheritdoc />
        public override LocationType LocationType => LocationType.Remote;
    }
}
