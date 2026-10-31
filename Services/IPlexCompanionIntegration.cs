using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>Optional Plex Companion operations used by the core federation service.</summary>
    public interface IPlexCompanionIntegration
    {
        Task<(bool Success, string Message, RemoteServer? Server)> ConnectAsync(string? code, CancellationToken cancellationToken);

        (bool Success, string Message) ReceiveOffer(string? code);

        IReadOnlyList<IncomingPlexOffer> ListOffers();

        Task<(bool Success, string Message, RemoteServer? Server)> AcceptOfferAsync(string offerId, CancellationToken cancellationToken);

        (bool Success, string Message) RejectOffer(string offerId);

        (bool Success, string Message, string? ConnectCode) CreateReceiver(string name, bool shareAllLibraries, List<string>? sharedLibraryFolderIds);

        (bool Success, string Message, string? ConnectCode) GetConnectCode(RemoteServer server);

        Task<string?> TryGetVersionAsync(RemoteServer friend, CancellationToken cancellationToken);

        string? GetPoolInviteUrl(RemoteServer friend);
    }
}
