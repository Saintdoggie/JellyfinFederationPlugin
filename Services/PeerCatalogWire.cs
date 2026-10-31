using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
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
        "MediaSources", "MediaStreams", "Album", "AlbumId", "AlbumArtist", "Artists", "BackdropImageTags",
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

    /// <summary>Most ids one Peer/Items?ids= request may name.</summary>
    public const int MaxIdsPerRequest = 200;

    /// <summary>
    /// Fingerprint of one compacted item exactly as a compact caller would
    /// receive it. Computed per caller, after sharing rules, so it never
    /// reflects a field or item that caller is not allowed to see.
    /// </summary>
    public static string ItemHash(JsonObject compactedItem)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(compactedItem.ToJsonString()));
        return Convert.ToBase64String(digest, 0, 16).TrimEnd('=');
    }

    /// <summary>
    /// Parses a Peer/Items ids= list. Null when it is malformed or too long.
    /// </summary>
    public static List<Guid>? ParseIds(string? ids)
    {
        if (string.IsNullOrWhiteSpace(ids))
        {
            return null;
        }

        var parsed = new List<Guid>();
        foreach (var part in ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Guid.TryParse(part, out var id) || parsed.Count == MaxIdsPerRequest)
            {
                return null;
            }

            parsed.Add(id);
        }

        return parsed.Count == 0 ? null : parsed.Distinct().ToList();
    }

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
    /// Compresses <paramref name="json"/> with the best encoding the caller
    /// accepts. Older callers keep the unchanged payload.
    /// </summary>
    public static (byte[] Body, string? ContentEncoding) Encode(string json, string? acceptEncoding)
    {
        var raw = Encoding.UTF8.GetBytes(json);
        if (raw.Length < MinimumCompressedLength)
        {
            return (raw, null);
        }

        var encoding = AcceptsEncoding(acceptEncoding, "br") ? "br" : AcceptsGzip(acceptEncoding) ? "gzip" : null;
        if (encoding == null)
        {
            return (raw, null);
        }

        using var output = new MemoryStream();
        using (Stream compressor = encoding == "br"
            ? new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true)
            : new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            compressor.Write(raw, 0, raw.Length);
        }

        return (output.ToArray(), encoding);
    }

    /// <summary>Reads a bounded response body, undoing Brotli or gzip.</summary>
    public static async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken, int maximumBytes = 256 * 1024 * 1024)
    {
        var encodings = response.Content.Headers.ContentEncoding;
        if (encodings.Count == 0)
        {
            var bytes = await HttpContentReader.ReadBytesAsync(response.Content, maximumBytes, cancellationToken).ConfigureAwait(false);
            return Encoding.UTF8.GetString(bytes);
        }

        if (encodings.Count != 1 || !(encodings.First() is "gzip" or "br" || string.Equals(encodings.First(), "gzip", StringComparison.OrdinalIgnoreCase) || string.Equals(encodings.First(), "br", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("Unsupported response encoding: " + string.Join(", ", encodings));
        }

        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using Stream decompressed = string.Equals(encodings.First(), "br", StringComparison.OrdinalIgnoreCase)
            ? new BrotliStream(body, CompressionMode.Decompress)
            : new GZipStream(body, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[32 * 1024];
        while (true)
        {
            var read = await decompressed.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return Encoding.UTF8.GetString(output.GetBuffer(), 0, (int)output.Length);
            }

            if (output.Length + read > maximumBytes)
            {
                throw new InvalidDataException("The remote catalog exceeds the permitted size.");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    internal static bool AcceptsGzip(string? acceptEncoding) => AcceptsEncoding(acceptEncoding, "gzip");

    private static bool AcceptsEncoding(string? acceptEncoding, string encoding)
    {
        if (string.IsNullOrWhiteSpace(acceptEncoding))
        {
            return false;
        }

        foreach (var part in acceptEncoding.Split(','))
        {
            var pieces = part.Split(';');
            if (!string.Equals(pieces[0].Trim(), encoding, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

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
