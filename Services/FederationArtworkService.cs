using System;
using System.IO;
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

/// <summary>Refresh source artwork on existing items without recreating
/// them or losing watch history. Never persist a credential-bearing image URL.</summary>
public sealed class FederationArtworkService
{
    internal const string StampKey = "FederationArtwork";
    internal const string LocalStampKey = "FederationArtworkLocal";
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
        if (source == null) return null;
        var (primaryTag, backdropTag) = Tags(entry, source);
        if (primaryTag == null && backdropTag == null) return null;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            source.ServerId + "\n" + source.RemoteItemId + "\n" + primaryTag + "\n" + backdropTag)));
    }

    private static (string? Primary, string? Backdrop) Tags(FederatedCacheEntry entry, FederatedSource source)
        => (source.PrimaryImageTag ?? entry.Metadata.PrimaryImageTag, source.BackdropImageTag ?? entry.Metadata.BackdropImageTag);

    internal static string LocalRevision(BaseItem item)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n",
            new[] { ImageType.Primary, ImageType.Backdrop }.Select(type =>
            {
                var image = item.GetImageInfo(type, 0);
                return image == null || !File.Exists(image.Path) ? "missing" : image.Path + "|" + image.DateModified.Ticks + "|" + File.GetLastWriteTimeUtc(image.Path).Ticks;
            })))));

    private static bool HasStoredImage(BaseItem item, ImageType type)
        => item.GetImageInfo(type, 0) is { } info && File.Exists(info.Path);

    public async Task RefreshAsync(BaseItem item, FederatedCacheEntry entry, Folder parent, CancellationToken ct)
    {
        var source = entry.GetPrimarySource();
        var server = source == null ? null : _manager.GetServer(source.ServerId);
        var revision = Revision(entry);
        if (source == null || server == null || !server.Enabled || revision == null) return;
        var (primaryTag, backdropTag) = Tags(entry, source);
        if (item.ProviderIds.TryGetValue(StampKey, out var stamp) && stamp == revision
            && item.ProviderIds.TryGetValue(LocalStampKey, out var localStamp) && localStamp == LocalRevision(item)
            && (primaryTag == null || HasStoredImage(item, ImageType.Primary))
            && (backdropTag == null || HasStoredImage(item, ImageType.Backdrop))) return;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            var imageCt = timeout.Token;
            ExternalImageSet? images;
            var external = _catalogs.For(server);
            if (external != null)
            {
                var nativeId = entry.GetNativeId(source);
                if (nativeId == null) return;
                images = await external.GetImagesAsync(server, nativeId, imageCt).ConfigureAwait(false);
            }
            else
            {
                var client = _manager.GetClient(server.Id);
                if (client == null) return;
                var id = source.RemoteItemId.ToString("N");
                var (token, _) = await client.GetImageTokenAsync(id, imageCt).ConfigureAwait(false);
                if (token == null) return;
                var url = server.Url.TrimEnd('/') + "/Plugins/Federation/Peer/Images/" + id;
                var query = "?token=" + Uri.EscapeDataString(token);
                images = new ExternalImageSet(primaryTag == null ? null : url + "/Primary" + query + "&tag=" + Uri.EscapeDataString(primaryTag),
                    backdropTag == null ? null : url + "/Backdrop/0" + query + "&tag=" + Uri.EscapeDataString(backdropTag));
            }
            if (images == null) return;
            var candidates = new[] { (images.PrimaryUrl, ImageType.Primary, primaryTag),
                (images.BackdropUrl, ImageType.Backdrop, backdropTag) };
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
            item.ProviderIds.TryGetValue(LocalStampKey, out var previousLocal);
            item.ProviderIds[StampKey] = revision;
            item.ProviderIds[LocalStampKey] = LocalRevision(item);
            try
            {
                await _libraryManager.UpdateItemsAsync(new[] { item }, parent, ItemUpdateType.MetadataEdit, ct).ConfigureAwait(false);
            }
            catch
            {
                if (previous == null) item.ProviderIds.Remove(StampKey);
                else item.ProviderIds[StampKey] = previous;
                if (previousLocal == null) item.ProviderIds.Remove(LocalStampKey);
                else item.ProviderIds[LocalStampKey] = previousLocal;
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
