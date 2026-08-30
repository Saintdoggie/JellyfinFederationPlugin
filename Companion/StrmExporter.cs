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
    /// Returns the count actually written (an unchanged file, per
    /// <see cref="WriteIfChanged"/>, still counts - only items skipped for
    /// having no stable name/episode number do not).
    /// </summary>
    public static int Export(string basePath, IEnumerable<(PeerItem Item, string Url)> entries)
    {
        Directory.CreateDirectory(basePath);

        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var count = 0;

        foreach (var (item, url) in entries)
        {
            var relativePath = item.Type == "Movie" ? BuildMoviePath(item) : BuildEpisodePath(item);
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

            WriteIfChanged(fullPath, url);
            written.Add(fullPath);
            count++;
        }

        RemoveStale(basePath, written);
        return count;
    }

    private static string? BuildMoviePath(PeerItem item)
    {
        var name = SafeFileName(item.Name);
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        var folder = item.ProductionYear.HasValue ? $"{name} ({item.ProductionYear.Value})" : name;
        return Path.Combine(MoviesFolderName, folder, folder + ".strm");
    }

    private static string? BuildEpisodePath(PeerItem item)
    {
        if (item.IndexNumber is not int episodeNumber)
        {
            return null;
        }

        var seasonNumber = item.ParentIndexNumber ?? 0;
        var series = SafeFileName(string.IsNullOrWhiteSpace(item.SeriesName) ? "Unknown Show" : item.SeriesName);
        var seasonFolder = $"Season {seasonNumber:D2}";
        var episodeTitle = SafeFileName(item.Name);
        var fileBase = string.IsNullOrEmpty(episodeTitle)
            ? $"{series} - S{seasonNumber:D2}E{episodeNumber:D2}"
            : $"{series} - S{seasonNumber:D2}E{episodeNumber:D2} - {episodeTitle}";

        return Path.Combine(ShowsFolderName, series, seasonFolder, fileBase + ".strm");
    }

    private static void WriteIfChanged(string path, string url)
    {
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path).TrimEnd('\r', '\n');
            if (string.Equals(existing, url, StringComparison.Ordinal))
            {
                return;
            }
        }

        File.WriteAllText(path, url + "\n");
    }

    private static void RemoveStale(string basePath, HashSet<string> written)
    {
        List<string> existing;
        try
        {
            existing = Directory.EnumerateFiles(basePath, "*.strm", SearchOption.AllDirectories).ToList();
        }
        catch (IOException)
        {
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        var removedDirs = new HashSet<string>();
        foreach (var path in existing)
        {
            if (written.Contains(path))
            {
                continue;
            }

            try
            {
                File.Delete(path);
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                {
                    removedDirs.Add(dir);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        foreach (var dir in removedDirs)
        {
            RemoveIfEmpty(dir, basePath);
        }
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

    private static string SafeFileName(string? name)
    {
        var trimmed = string.IsNullOrWhiteSpace(name) ? string.Empty : name.Trim();
        var chars = trimmed.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(Path.GetInvalidFileNameChars(), chars[i]) >= 0)
            {
                chars[i] = '_';
            }
        }

        return new string(chars);
    }
}
