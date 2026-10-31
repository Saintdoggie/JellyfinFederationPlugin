using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>Optional work performed after a successful catalog refresh.</summary>
    public interface IFederationPostSyncExport
    {
        Task ExportAsync(CancellationToken cancellationToken);
    }

    /// <summary>Core-only behavior when no export add-in is installed.</summary>
    public sealed class NoopFederationPostSyncExport : IFederationPostSyncExport
    {
        public Task ExportAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
