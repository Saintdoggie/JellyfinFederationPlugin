using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Federation.Services;

/// <summary>
/// Wire format of a full Peer/Items sync page. A full catalog item is about
/// 14 KB of JSON, most of it fields the receiving side never reads (person
/// image data, a second copy of every media stream inside MediaSources,
/// server file paths, the serving admin's own UserData). Both trimming and
/// gzip are opt-in per request, so older callers keep the unchanged payload.
/// </summary>
internal static class PeerCatalogWire
{
    /// <summary>
    /// Top-level fields RemoteServerClient.ParseItem reads. Keep in step with it:
    /// anything not listed here never reaches a compact caller.
    /// </summary>
    private static readonly HashSet<string> ItemFields = new(StringComparer.Ordinal)
    {
        "Id", "Name", "OriginalTitle", "Type", "Overview", "CommunityRating", "OfficialRating",
        "PremiereDate", "DateCreated", "ProductionYear", "RunTimeTicks", "Container",
        "MediaSources", "MediaStreams", "Album", "AlbumArtist", "Artists", "BackdropImageTags",
        "Genres", "ImageTags", "IndexNumber", "ParentIndexNumber", "People", "ProviderIds",
        "SeasonId", "SeasonName", "SeriesId", "SeriesName", "Studios", "Tags"
    };

    private static readonly HashSet<string> MediaSourceFields = new(StringComparer.Ordinal)
    {
        "Container", "Size", "Bitrate", "RunTimeTicks"
    };

    private static readonly HashSet<string> PersonFields = new(StringComparer.Ordinal) { "Name", "Role", "Type" };

    private static readonly HashSet<string> StudioFields = new(StringComparer.Ordinal) { "Name" };

    private const int MinimumCompressedLength = 1024;

    /// <summary>Strips one Peer/Items item down to what a sync caller reads.</summary>
    public static void CompactItem(JsonObject item)
    {
        Keep(item, ItemFields);
        KeepInEach(item["MediaSources"], MediaSourceFields);
        KeepInEach(item["People"], PersonFields);
        KeepInEach(item["Studios"], StudioFields);
        if (item["MediaStreams"] is JsonArray streams)
        {
            // A stream's Path is a file on the serving server; the receiver clears it.
            foreach (var stream in streams.OfType<JsonObject>())
            {
                stream.Remove("Path");
            }
        }
    }

    /// <summary>
    /// Gzips <paramref name="json"/> when the caller accepts it. Returns the body
    /// and the Content-Encoding to send, or null for an uncompressed body.
    /// </summary>
    public static (byte[] Body, string? ContentEncoding) Encode(string json, string? acceptEncoding)
    {
        var raw = Encoding.UTF8.GetBytes(json);
        if (raw.Length < MinimumCompressedLength || !AcceptsGzip(acceptEncoding))
        {
            return (raw, null);
        }

        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(raw, 0, raw.Length);
        }

        return (output.ToArray(), "gzip");
    }

    /// <summary>Reads a response body, undoing gzip when the peer applied it.</summary>
    public static async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var encodings = response.Content.Headers.ContentEncoding;
        if (encodings.Count == 0)
        {
            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }

        if (encodings.Count != 1 || !string.Equals(encodings.First(), "gzip", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Unsupported response encoding: " + string.Join(", ", encodings));
        }

        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var gzip = new GZipStream(body, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static bool AcceptsGzip(string? acceptEncoding)
    {
        if (string.IsNullOrWhiteSpace(acceptEncoding))
        {
            return false;
        }

        foreach (var part in acceptEncoding.Split(','))
        {
            var pieces = part.Split(';');
            if (!string.Equals(pieces[0].Trim(), "gzip", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // "gzip;q=0" explicitly refuses it.
            foreach (var parameter in pieces.Skip(1))
            {
                var pair = parameter.Split('=', 2);
                if (pair.Length == 2
                    && string.Equals(pair[0].Trim(), "q", StringComparison.OrdinalIgnoreCase)
                    && double.TryParse(pair[1].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var quality)
                    && quality <= 0)
                {
                    return false;
                }
            }

            return true;
        }

        return false;
    }

    private static void Keep(JsonObject node, HashSet<string> fields)
    {
        foreach (var name in node.Select(p => p.Key).Where(k => !fields.Contains(k)).ToList())
        {
            node.Remove(name);
        }
    }

    private static void KeepInEach(JsonNode? node, HashSet<string> fields)
    {
        if (node is not JsonArray array)
        {
            return;
        }

        foreach (var entry in array.OfType<JsonObject>())
        {
            Keep(entry, fields);
        }
    }
}
