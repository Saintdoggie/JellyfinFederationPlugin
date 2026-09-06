using System.Text.Json;

namespace FederationCompanion;

/// <summary>
/// Writes/prunes <c>.strm</c> files for a Jellyfin peer's movies/episodes,
/// mirroring the path-building and add/remove diffing the Federation plugin's
/// own <c>PlexStrmExportService</c> uses for its local (same-disk) export -
/// deliberately kept in the same shape (<c>Movies/Title (Year)/Title.strm</c>,
/// <c>Shows/Series/Season NN/...strm</c>) so Plex sees an identical layout
/// whether the source is a local Jellyfin server or one reached over the
/// network through this app. Can't share the actual file between the two
/// separate .csproj's, so this is a deliberate, minimal port rather than a
/// reference.
/// </summary>
public static class StrmExporter
{
    private const string MoviesFolderName = "Movies";
    private const string ShowsFolderName = "Shows";

    /// <summary>
    /// Writes one <c>.strm</c> file per entry, then deletes any previously
    /// written file under <paramref name="basePath"/> that this run didn't
    /// (re)write, and prunes any directory left empty by that cleanup.
    /// Returns both the exported item count and whether the filesystem changed.
    /// The change count matters to Plex: replacing an expired URL without
    /// adding/removing an item still requires a section refresh.
    /// </summary>
    public static ExportResult Export(string basePath, IEnumerable<(PeerItem Item, string Url)> entries, string? peerId = null)
    {
        basePath = Path.GetFullPath(basePath);
        Directory.CreateDirectory(basePath);
        EnsureNoLinks(basePath, basePath);
        var manifestPath = Path.Combine(basePath, ".companion-files.json");
        EnsureNoLinks(basePath, manifestPath);
        var owned = File.Exists(manifestPath)
            ? JsonSerializer.Deserialize<List<string>>(File.ReadAllText(manifestPath)) ?? throw new IOException("Invalid export manifest")
            : new List<string>();
        // Adopt old exports only when their signed relay URL belongs to this
        // exact peer. Never sweep arbitrary .strm files from a selected folder.
        if (!File.Exists(manifestPath) && peerId != null)
        {
            foreach (var path in Directory.EnumerateFiles(basePath, "*.strm", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            {
                if (Uri.TryCreate(File.ReadAllText(path).Trim(), UriKind.Absolute, out var uri)
                    && uri.AbsolutePath.StartsWith("/stream/" + peerId + "/", StringComparison.Ordinal))
                    owned.Add(Path.GetRelativePath(basePath, path));
            }
        }
        var plan = entries.GroupBy(e => e.Item.Id).Select(g => g.First()).Select(entry =>
        {
            var relative = entry.Item.Type == "Movie" ? BuildMoviePath(entry.Item) : BuildEpisodePath(entry.Item);
            return (entry.Item, entry.Url, Relative: relative);
        }).Where(e => e.Relative != null).ToList();
        var collisions = plan.GroupBy(e => e.Relative!, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        plan = plan.Select(e => (e.Item, e.Url, Relative: collisions.Contains(e.Relative!)
            ? Path.Combine(Path.GetDirectoryName(e.Relative!)!, Path.GetFileNameWithoutExtension(e.Relative) + " [" + SafeFileName(e.Item.Id) + "].strm")
            : e.Relative)).ToList();
        var ownedPaths = owned.Select(relative => SafePath(basePath, relative)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        plan = plan.Select(e =>
        {
            var relative = e.Relative!;
            var full = SafePath(basePath, relative);
            if (File.Exists(full) && !ownedPaths.Contains(full))
            {
                relative = Path.Combine(Path.GetDirectoryName(relative)!, Path.GetFileNameWithoutExtension(relative) + " [" + SafeFileName(e.Item.Id) + "].strm");
                full = SafePath(basePath, relative);
                if (File.Exists(full) && !ownedPaths.Contains(full))
                    throw new IOException("An unmanaged file occupies this item's export path. Move it before retrying.");
            }
            return (e.Item, e.Url, Relative: (string?)relative);
        }).ToList();
        foreach (var relative in owned.Concat(plan.Select(e => e.Relative!)))
            EnsureNoLinks(basePath, SafePath(basePath, relative));
        // Record ownership before writing so a retry can clean interrupted runs.
        AtomicWrite(manifestPath, JsonSerializer.Serialize(owned.Concat(plan.Select(e => e.Relative!)).Distinct().ToList()));

        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var count = 0;
        var changed = 0;

        foreach (var (item, url, relativePath) in plan)
        {
            if (relativePath == null)
            {
                continue;
            }

            var fullPath = Path.Combine(basePath, relativePath);
            var dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            if (WriteIfChanged(fullPath, url))
            {
                changed++;
            }

            written.Add(fullPath);
            count++;
        }

        changed += RemoveStale(basePath, written, owned);
        AtomicWrite(manifestPath, JsonSerializer.Serialize(written.Select(p => Path.GetRelativePath(basePath, p)).ToList()));
        return new ExportResult(count, changed);
    }

    internal static string? BuildMoviePath(PeerItem item)
    {
        var name = SafeFileName(item.Name);
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        var folder = MovieFolderName(name, item.ProductionYear);
        return Path.Combine(MoviesFolderName, folder, folder + ".strm");
    }

    /// <summary>
    /// Avoids "Title (2021) (2021)" when Jellyfin already put the year in the name.
    /// </summary>
    public static string MovieFolderName(string name, int? year)
    {
        if (year is int y)
        {
            var suffix = $" ({y})";
            if (!name.EndsWith(suffix, StringComparison.Ordinal))
            {
                return name + suffix;
            }
        }

        return name;
    }

    internal static string? BuildEpisodePath(PeerItem item)
    {
        if (item.IndexNumber is not int episodeNumber)
        {
            return null;
        }

        var seasonNumber = item.ParentIndexNumber ?? 0;
        var series = SafeFileName(string.IsNullOrWhiteSpace(item.SeriesName) ? "Unknown Show" : item.SeriesName);
        var seasonFolder = $"Season {seasonNumber:D2}";
        var episodeRange = $"S{seasonNumber:D2}E{episodeNumber:D2}";
        if (item.IndexNumberEnd is int end && end > episodeNumber) episodeRange += $"-E{end:D2}";
        var episodeTitle = SafeFileName(item.Name);
        var fileBase = string.IsNullOrEmpty(episodeTitle)
            ? $"{series} - {episodeRange}"
            : $"{series} - {episodeRange} - {episodeTitle}";

        return Path.Combine(ShowsFolderName, series, seasonFolder, fileBase + ".strm");
    }

    private static bool WriteIfChanged(string path, string url)
    {
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path).TrimEnd('\r', '\n');
            if (string.Equals(existing, url, StringComparison.Ordinal))
            {
                return false;
            }
        }

        AtomicWrite(path, url + "\n");
        return true;
    }

    private static int RemoveStale(string basePath, HashSet<string> written, List<string> owned)
    {
        var existing = owned.Select(relative => SafePath(basePath, relative)).ToList();
        var removedDirs = new HashSet<string>();
        var removed = 0;
        foreach (var path in existing)
        {
            if (written.Contains(path))
            {
                continue;
            }

            File.Delete(path);
            removed++;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) removedDirs.Add(dir);
        }

        foreach (var dir in removedDirs)
        {
            RemoveIfEmpty(dir, basePath);
        }

        return removed;
    }

    private static void RemoveIfEmpty(string? dir, string basePath)
    {
        while (!string.IsNullOrEmpty(dir)
            && !string.Equals(Path.GetFullPath(dir).TrimEnd('/', '\\'), Path.GetFullPath(basePath).TrimEnd('/', '\\'), StringComparison.Ordinal))
        {
            if (!Directory.Exists(dir) || Directory.EnumerateFileSystemEntries(dir).Any())
            {
                return;
            }

            Directory.Delete(dir);
            dir = Path.GetDirectoryName(dir);
        }
    }

    private static string SafePath(string root, string relative)
    {
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (Path.IsPathRooted(relative) || !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new IOException("Export path is outside its managed folder.");
        return full;
    }

    private static void EnsureNoLinks(string root, string path)
    {
        for (var current = path; current != null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Export paths cannot contain symbolic links.");
            if (current == root) break;
        }
    }

    private static void AtomicWrite(string path, string content)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, content); File.Move(temp, path, overwrite: true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static string SafeFileName(string? name)
    {
        var trimmed = string.IsNullOrWhiteSpace(name) ? string.Empty : name.Trim();
        var chars = trimmed.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (char.IsControl(chars[i]) || "<>:\"/\\|?*".Contains(chars[i]) || Array.IndexOf(Path.GetInvalidFileNameChars(), chars[i]) >= 0)
            {
                chars[i] = '_';
            }
        }

        var safe = new string(chars).TrimEnd(' ', '.');
        if (string.IsNullOrEmpty(safe)) return "Untitled";
        var stem = safe.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL"
            || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] is >= '1' and <= '9'))
            safe = "_" + safe;
        return safe;
    }
}

public readonly record struct ExportResult(int ItemCount, int ChangedFileCount);
