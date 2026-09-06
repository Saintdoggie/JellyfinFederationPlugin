using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace FederationCompanion;

public sealed record ImportCatalogItem(string ItemId, string Title, string? Series, int? Season, int? Episode, int? EpisodeEnd, string? File, string? Issue);
public sealed record MountedCatalog(List<MountedMediaFile> Files, List<ImportCatalogItem> Items);

public sealed record MountedMediaFile(string Path, string ItemId, long Size, DateTime ModifiedUtc);

/// <summary>
/// Read-only WebDAV filesystem. A local rclone mount presents actual media
/// files to Plex, which cannot analyze a text .strm as a video. Only committed
/// import snapshots are listed; every read still obtains upstream permission.
/// </summary>
public static class MediaMount
{
    private const string Marker = "Federation Companion media mount v1";

    public static bool IsMounted(string? root, string clientIdentifier)
    {
        try { return root != null && File.ReadAllText(Path.Combine(root, ".companion-mount")) == Marker + "\n" + clientIdentifier; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    public static List<MountedMediaFile> BuildFiles(IEnumerable<PeerItem> items) => BuildCatalog(items).Files;

    public static MountedCatalog BuildCatalog(IEnumerable<PeerItem> items)
    {
        var files = new List<MountedMediaFile>();
        var catalog = new List<ImportCatalogItem>();
        foreach (var item in items.DistinctBy(i => i.Id))
        {
            var source = item.MediaSources?.FirstOrDefault();
            var relative = item.Type == "Movie" ? StrmExporter.BuildMoviePath(item) : StrmExporter.BuildEpisodePath(item);
            var extension = source?.Container?.Split(',')[0].Trim().ToLowerInvariant();
            string? issue = null;
            if (!Guid.TryParse(item.Id, out var id)) issue = "Invalid source item identifier.";
            else if (item.Type == "Episode" && (item.ParentIndexNumber == null || item.ParentIndexNumber < 0 || item.IndexNumber == null || item.IndexNumber < 0))
                issue = "Source season or episode number is missing. Correct the source library metadata and sync again.";
            else if (relative == null) issue = "Source episode numbering could not be read.";
            else if (source?.Size is not > 0) issue = "Source video size is missing. Update the source Federation plugin and refresh the media information.";
            else if (string.IsNullOrWhiteSpace(extension) || extension.Length > 10 || !extension.All(char.IsAsciiLetterOrDigit) || extension == "strm")
                issue = "Source has no supported video container; a text stream link cannot be mounted as a video.";
            if (issue == null)
            {
                // Item suffix prevents equal titles/years from overwriting one another.
                relative = Path.ChangeExtension(relative, null) + " [" + id.ToString("N") + "]." + extension;
                relative = relative.Replace('\\', '/');
                files.Add(new MountedMediaFile(relative, item.Id, source!.Size!.Value, item.DateCreated ?? DateTime.UnixEpoch));
            }
            catalog.Add(new ImportCatalogItem(item.Id, item.Name, item.SeriesName, item.ParentIndexNumber,
                item.IndexNumber, item.IndexNumberEnd, issue == null ? relative : null, issue));
        }
        return new MountedCatalog(files, catalog);
    }

    public static bool Authorized(string supplied, string key)
    {
        var expected = "Bearer " + key;
        return !string.IsNullOrWhiteSpace(key) && CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(expected));
    }

    public static async Task HandleAsync(CompanionState state, JellyfinImportService jellyfin, HttpContext context, string? path, CancellationToken ct)
    {
        if (!Authorized(context.Request.Headers.Authorization.ToString(), state.MediaAccessKey))
        {
            context.Response.StatusCode = 401;
            context.Response.Headers.WWWAuthenticate = "Bearer";
            return;
        }
        var relative = (path ?? "").Trim('/');
        if (relative.Split('/').Any(s => s is "." or "..") || relative.Contains('\\'))
        {
            context.Response.StatusCode = 400;
            return;
        }
        context.Response.Headers.CacheControl = "private, no-store";
        if (context.Request.Method == "OPTIONS")
        {
            context.Response.Headers["DAV"] = "1";
            context.Response.Headers.Allow = "OPTIONS, PROPFIND, GET, HEAD";
            return;
        }
        // Snapshot each peer's immutable file list. Never resolve disk paths or
        // a caller-supplied upstream URL from the WebDAV request.
        var files = state.ImportPeers.ToArray().SelectMany(p => p.MountedFiles.Select(f => (Peer: p, File: f, Path: p.Id + "/" + f.Path))).ToList();
        var marker = Marker + "\n" + state.ClientIdentifier;
        var markerFile = new MountedMediaFile(".companion-mount", "", Encoding.UTF8.GetByteCount(marker), DateTime.UnixEpoch);
        if (relative == ".companion-mount" && context.Request.Method is "GET" or "HEAD")
        {
            context.Response.ContentLength = markerFile.Size;
            if (context.Request.Method == "GET") await context.Response.WriteAsync(marker, ct);
            return;
        }
        var file = files.FirstOrDefault(f => f.Path == relative);
        if (context.Request.Method is "GET" or "HEAD")
        {
            if (file.File == null) { context.Response.StatusCode = 404; return; }
            await jellyfin.RelayStreamAsync(file.Peer, file.File.ItemId, context.Request, context.Response, ct).ConfigureAwait(false);
            return;
        }
        if (context.Request.Method != "PROPFIND") { context.Response.StatusCode = 405; return; }
        var depth = context.Request.Headers["Depth"].ToString();
        if (depth != "0" && depth != "1") { context.Response.StatusCode = 403; return; }
        var prefix = relative.Length == 0 ? "" : relative + "/";
        var descendants = files.Where(f => f.Path.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        var peerFolder = state.ImportPeers.Any(p => p.Id == relative);
        if (relative.Length > 0 && file.File == null && descendants.Count == 0 && !peerFolder && relative != ".companion-mount")
        { context.Response.StatusCode = 404; return; }
        XNamespace dav = "DAV:";
        XElement Entry(string name, MountedMediaFile? media)
        {
            var directory = media == null;
            var href = context.Request.PathBase + "/media/" + string.Join('/', name.Split('/').Select(Uri.EscapeDataString)) + (directory && name.Length > 0 ? "/" : "");
            return new XElement(dav + "response",
                new XElement(dav + "href", href),
                new XElement(dav + "propstat",
                    new XElement(dav + "prop",
                        new XElement(dav + "displayname", name.Split('/').Last()),
                        new XElement(dav + "resourcetype", directory ? new XElement(dav + "collection") : null),
                        new XElement(dav + "getcontentlength", media?.Size ?? 0),
                        new XElement(dav + "getlastmodified", (media?.ModifiedUtc ?? DateTime.UnixEpoch).ToUniversalTime().ToString("R", CultureInfo.InvariantCulture))),
                    new XElement(dav + "status", "HTTP/1.1 200 OK")));
        }
        var entries = new List<XElement> { Entry(relative, relative == ".companion-mount" ? markerFile : file.File) };
        if (depth == "1" && file.File == null)
        {
            if (relative.Length == 0) entries.Add(Entry(".companion-mount", markerFile));
            var children = descendants.Select(f => prefix + f.Path[prefix.Length..].Split('/')[0])
                .Concat(relative.Length == 0 ? state.ImportPeers.Select(p => p.Id) : Array.Empty<string>())
                .Distinct(StringComparer.Ordinal);
            foreach (var child in children) entries.Add(Entry(child, files.FirstOrDefault(f => f.Path == child).File));
        }
        context.Response.StatusCode = 207;
        context.Response.ContentType = "application/xml; charset=utf-8";
        await context.Response.WriteAsync(new XElement(dav + "multistatus", entries).ToString(), ct).ConfigureAwait(false);
    }
}
