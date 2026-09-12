using System;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services;

/// <summary>Refresh source Plex artwork on existing items without recreating
/// them or losing watch history. Never persist a credential-bearing image URL.</summary>
public sealed class FederationArtworkService
{
    internal const string StampKey = "FederationArtwork";
    private static readonly HttpClient ImageHttp = new(new SocketsHttpHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(30) };
    internal static HttpClient? HttpClientOverride { get; set; }
    private readonly FederationLibraryManager _manager;
    private readonly ExternalCatalogRegistry _catalogs;
    private readonly IProviderManager _providers;
    private readonly ILibraryManager _libraryManager;
    private readonly IItemPersistenceService _persistence;
    private readonly ILogger<FederationArtworkService> _logger;

    public FederationArtworkService(FederationLibraryManager manager, ExternalCatalogRegistry catalogs,
        IProviderManager providers, IItemPersistenceService persistence, ILogger<FederationArtworkService> logger, ILibraryManager libraryManager)
    {
        _libraryManager = libraryManager; _manager = manager; _catalogs = catalogs; _providers = providers; _persistence = persistence; _logger = logger;
    }

    internal static string? Revision(FederatedCacheEntry entry)
    {
        var source = entry.GetPrimarySource();
        if (source == null || (entry.Metadata.PrimaryImageTag == null && entry.Metadata.BackdropImageTag == null)) return null;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            source.ServerId + "\n" + source.RemoteItemId + "\n" + entry.Metadata.PrimaryImageTag + "\n" + entry.Metadata.BackdropImageTag)));
    }

    public async Task RefreshAsync(BaseItem item, FederatedCacheEntry entry, Folder parent, CancellationToken ct)
    {
        var source = entry.GetPrimarySource();
        var server = source == null ? null : _manager.GetServer(source.ServerId);
        var revision = Revision(entry);
        if (server?.Kind != ServerKind.Plex || !server.Enabled || revision == null
            || entry.Metadata.RemoteNativeId == null) return;
        if (item.ProviderIds.TryGetValue(StampKey, out var stamp) && stamp == revision
            && (entry.Metadata.PrimaryImageTag == null || item.HasImage(ImageType.Primary))
            && (entry.Metadata.BackdropImageTag == null || item.HasImage(ImageType.Backdrop))) return;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            var imageCt = timeout.Token;
            var images = await _catalogs.For(server)!.GetImagesAsync(server, entry.Metadata.RemoteNativeId, imageCt).ConfigureAwait(false);
            if (images == null) return;
            var candidates = new[] { (images.PrimaryUrl, ImageType.Primary, entry.Metadata.PrimaryImageTag),
                (images.BackdropUrl, ImageType.Backdrop, entry.Metadata.BackdropImageTag) };
            foreach (var (url, type, tag) in candidates.Where(x => x.Item3 != null))
            {
                if (url == null) return;
                using var response = await (HttpClientOverride ?? ImageHttp).GetAsync(url, HttpCompletionOption.ResponseHeadersRead, imageCt).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var mime = response.Content.Headers.ContentType?.MediaType;
                if (mime == null || !mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return;
                await response.Content.LoadIntoBufferAsync(20 * 1024 * 1024, imageCt).ConfigureAwait(false);
                await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await _providers.SaveImage(item, stream, mime, type, 0, ct).ConfigureAwait(false);
            }
            await _persistence.SaveImagesAsync(item, ct).ConfigureAwait(false);
            item.ProviderIds.TryGetValue(StampKey, out var previous);
            item.ProviderIds[StampKey] = revision;
            try
            {
                await _libraryManager.UpdateItemsAsync(new[] { item }, parent, ItemUpdateType.MetadataEdit, ct).ConfigureAwait(false);
            }
            catch
            {
                if (previous == null) item.ProviderIds.Remove(StampKey);
                else item.ProviderIds[StampKey] = previous;
                throw;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            // Http exceptions can include upstream URLs and credentials.
            _logger.LogWarning("[Federation] Source artwork refresh failed for {ItemId}; it will retry on the next sync", item.Id);
        }
    }
}
