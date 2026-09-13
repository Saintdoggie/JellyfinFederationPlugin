using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FederationCompanion;

/// <summary>Expose owned Plex movies/episodes through the same scoped import
/// protocol as Jellyfin. The existing importer and read-only mount work unchanged.</summary>
public sealed class PlexPeerProtocol(CompanionState state, PlexFederationRelay relay)
{
    public CompanionPeer? Authenticate(string supplied)
        => state.Peers.FirstOrDefault(p => p.CompanionConnection && !p.PendingConnection && MediaMount.Authorized("Bearer " + supplied, p.AccessToken));

    public static string ItemId(string nativeId)
    {
        if (!ulong.TryParse(nativeId, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value == 0)
            throw new JsonException("Plex returned an invalid item id.");
        Span<byte> bytes = stackalloc byte[16];
        Encoding.ASCII.GetBytes("PLEXITEM", bytes);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[8..], value);
        return new Guid(bytes).ToString("N");
    }

    internal static string? NativeId(string itemId)
    {
        if (!Guid.TryParse(itemId, out var id)) return null;
        var bytes = id.ToByteArray();
        var value = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(8));
        return bytes.AsSpan(0, 8).SequenceEqual("PLEXITEM"u8) && value != 0
            ? value.ToString(CultureInfo.InvariantCulture) : null;
    }

    public async Task<List<PeerLibrary>> LibrariesAsync(CompanionPeer peer, CancellationToken ct)
    {
        using var doc = await ReadAsync(peer, "/library/sections", "", ct);
        var container = doc.RootElement.GetProperty("MediaContainer");
        if (!container.TryGetProperty("Directory", out var directories) && Long(container, "size") == 0) return new();
        return directories.EnumerateArray().Where(d => Text(d, "type") is "movie" or "show")
            .Select(d => new PeerLibrary { Id = Required(d, "key"), Name = Required(d, "title"), CollectionType = Text(d, "type") == "movie" ? "movies" : "tvshows" }).ToList();
    }

    public async Task<List<PeerItem>> ItemsAsync(CompanionPeer peer, string parentId, string mediaType, int start, int limit, CancellationToken ct)
    {
        if (!parentId.All(char.IsAsciiDigit) || parentId.Length is < 1 or > 20 || start < 0 || limit is < 1 or > 200 || mediaType is not ("Movie" or "Episode"))
            throw new ArgumentException("Choose a valid library and page.");
        using var doc = await ReadAsync(peer, "/library/sections/" + parentId + "/all",
            $"?type={(mediaType == "Movie" ? 1 : 4)}&X-Plex-Container-Start={start}&X-Plex-Container-Size={limit}", ct);
        var container = doc.RootElement.GetProperty("MediaContainer");
        if (!container.TryGetProperty("Metadata", out var metadata) && Long(container, "size") == 0) return new();
        if (metadata.ValueKind != JsonValueKind.Array) throw new JsonException("Plex returned an incomplete catalog.");
        var rows = metadata.EnumerateArray().ToArray();
        var items = new PeerItem[rows.Length];
        await Parallel.ForEachAsync(Enumerable.Range(0, rows.Length), new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (index, token) =>
        {
            var key = Required(rows[index], "ratingKey");
            _ = ItemId(key); // Validate before constructing any upstream path.
            using var detail = await ReadAsync(peer, "/library/metadata/" + key, "", token);
            var item = detail.RootElement.GetProperty("MediaContainer").GetProperty("Metadata").EnumerateArray().Single();
            items[index] = ConvertItem(item, key);
        });
        return items.ToList();
    }

    internal static PeerItem ConvertItem(JsonElement item, string key)
    {
        var mediaSources = new List<PeerMediaSource>();
        if (item.TryGetProperty("Media", out var media))
            foreach (var source in media.EnumerateArray())
            {
                if (!source.TryGetProperty("Part", out var parts)) continue;
                var list = parts.EnumerateArray().ToArray();
                // The mount represents one complete file; never mislabel one part of a split movie.
                if (list.Length != 1) continue;
                mediaSources.Add(new PeerMediaSource { Container = Text(list[0], "container") ?? Text(source, "container"), Size = Long(list[0], "size") });
            }
        return new PeerItem
        {
            Id = ItemId(key),
            Name = Required(item, "title"),
            Type = Text(item, "type") == "episode" ? "Episode" : "Movie",
            ProductionYear = Number(item, "year"),
            SeriesName = Text(item, "grandparentTitle"),
            ParentIndexNumber = Number(item, "parentIndex"),
            IndexNumber = Number(item, "index"),
            IndexNumberEnd = Number(item, "indexEnd"),
            MediaSources = mediaSources,
            DateCreated = Long(item, "addedAt") is { } epoch && epoch is >= 0 and <= 253402300799 ? DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime : null
        };
    }

    public async Task<(string Token, DateTime ExpiresUtc)> MintAsync(CompanionPeer peer, string itemId, CancellationToken ct)
    {
        await PartAsync(peer, itemId, ct); // Current ownership is required even to mint.
        var expiry = DateTimeOffset.UtcNow.AddMinutes(10);
        var epoch = expiry.ToUnixTimeSeconds();
        return (epoch + "." + Signature(peer, itemId, epoch), expiry.UtcDateTime);
    }

    internal static string Signature(CompanionPeer peer, string itemId, long expiry)
        => Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(peer.AccessToken), Encoding.UTF8.GetBytes($"plex-play\n{peer.Id}\n{itemId}\n{expiry}")));

    public async Task StreamAsync(string itemId, string token, HttpContext context, CancellationToken ct)
    {
        var parts = token.Split('.');
        if (parts.Length != 2 || !long.TryParse(parts[0], out var expiry) || expiry < DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            || expiry > DateTimeOffset.UtcNow.AddMinutes(11).ToUnixTimeSeconds() || NativeId(itemId) == null)
        { context.Response.StatusCode = 403; return; }
        var peer = state.Peers.FirstOrDefault(p => p.CompanionConnection && !p.PendingConnection && MediaMount.Authorized("Bearer " + parts[1], Signature(p, itemId, expiry)));
        if (peer == null) { context.Response.StatusCode = 403; return; }
        var part = await PartAsync(peer, itemId, ct);
        if (!state.Peers.Contains(peer)) { context.Response.StatusCode = 403; return; }
        // Relay rechecks current ownership and selected libraries at this actual byte request.
        await relay.RelayAsync(peer, part, context.Request, context.Response, ct);
    }

    private async Task<string> PartAsync(CompanionPeer peer, string itemId, CancellationToken ct)
    {
        var native = NativeId(itemId) ?? throw new ArgumentException("Invalid Plex item.");
        using var doc = await ReadAsync(peer, "/library/metadata/" + native, "", ct);
        var item = doc.RootElement.GetProperty("MediaContainer").GetProperty("Metadata").EnumerateArray().Single();
        foreach (var source in item.GetProperty("Media").EnumerateArray())
        {
            var parts = source.GetProperty("Part").EnumerateArray().ToArray();
            if (parts.Length != 1) continue;
            if (Long(parts[0], "size") is not > 0) throw new InvalidOperationException("Source video size is missing.");
            var key = Required(parts[0], "key");
            if (!key.StartsWith("/library/parts/", StringComparison.Ordinal) || key.Contains('?') || key.Contains('#')) continue;
            return key;
        }
        throw new InvalidOperationException("No complete video file is available for this item.");
    }

    private async Task<JsonDocument> ReadAsync(CompanionPeer peer, string path, string query, CancellationToken ct)
    {
        if (!state.Peers.Contains(peer)) throw new UnauthorizedAccessException();
        var context = new DefaultHttpContext();
        context.Request.Method = "GET"; context.Request.QueryString = new QueryString(query);
        using var body = new MemoryStream(); context.Response.Body = body;
        await relay.RelayAsync(peer, path, context.Request, context.Response, ct);
        if (context.Response.StatusCode is 401 or 403) throw new UnauthorizedAccessException();
        if (context.Response.StatusCode != 200) throw new HttpRequestException("Plex is unavailable.");
        return JsonDocument.Parse(body.ToArray());
    }

    private static string? Text(JsonElement item, string name) => item.TryGetProperty(name, out var value) ? value.ToString() : null;
    private static string Required(JsonElement item, string name) => Text(item, name) ?? throw new JsonException("Plex returned incomplete metadata.");
    private static long? Long(JsonElement item, string name) => long.TryParse(Text(item, name), out var value) ? value : null;
    private static int? Number(JsonElement item, string name) => int.TryParse(Text(item, name), out var value) ? value : null;
}
