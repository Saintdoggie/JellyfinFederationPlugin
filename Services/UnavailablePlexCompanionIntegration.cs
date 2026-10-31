using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>Plex Companion behavior when the optional add-in is not installed.</summary>
    public sealed class UnavailablePlexCompanionIntegration : IPlexCompanionIntegration
    {
        private const string UnavailableMessage = "Plex Companion support is not installed.";

        public Task<(bool Success, string Message, RemoteServer? Server)> ConnectAsync(string? code, CancellationToken cancellationToken)
            => Task.FromResult<(bool, string, RemoteServer?)>((false, UnavailableMessage, null));

        public (bool Success, string Message) ReceiveOffer(string? code)
            => (false, UnavailableMessage);

        public IReadOnlyList<IncomingPlexOffer> ListOffers()
            => Array.Empty<IncomingPlexOffer>();

        public Task<(bool Success, string Message, RemoteServer? Server)> AcceptOfferAsync(string offerId, CancellationToken cancellationToken)
            => Task.FromResult<(bool, string, RemoteServer?)>((false, UnavailableMessage, null));

        public (bool Success, string Message) RejectOffer(string offerId)
            => (false, UnavailableMessage);

        public (bool Success, string Message, string? ConnectCode) CreateReceiver(string name, bool shareAllLibraries, List<string>? sharedLibraryFolderIds)
            => (false, UnavailableMessage, null);

        public (bool Success, string Message, string? ConnectCode) GetConnectCode(RemoteServer server)
            => (false, UnavailableMessage, null);

        public Task<string?> TryGetVersionAsync(RemoteServer friend, CancellationToken cancellationToken)
            => Task.FromResult<string?>(null);

        public string? GetPoolInviteUrl(RemoteServer friend)
            => null;
    }
}
