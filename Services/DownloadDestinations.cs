using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Data.Enums;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Where a "download to this server" lands. A choice is one of:
    /// <c>auto</c> (the library matching the title: Movies for films, Shows for
    /// episodes), <c>library:&lt;name&gt;</c>, <c>folder:&lt;path or name&gt;</c>
    /// (a new folder that becomes its own library) or <c>separate</c> (the old
    /// "Federation Downloads" library). Pure: everything it needs is passed in.
    /// </summary>
    public static class DownloadDestinations
    {
        /// <summary>The library matching the title's type.</summary>
        public const string Auto = "auto";

        /// <summary>The plugin's own "Federation Downloads" library.</summary>
        public const string Separate = "separate";

        internal const string LibraryPrefix = "library:";
        internal const string FolderPrefix = "folder:";

        /// <summary>
        /// Every library a download can go to, with the folder it would be written
        /// in. Libraries whose only folders are the plugin's own (federated titles,
        /// Federation Downloads) are left out.
        /// </summary>
        public static List<DownloadDestinationOption> List(IEnumerable<VirtualFolderInfo>? folders, string? pluginDataRoot)
        {
            var result = new List<DownloadDestinationOption>();
            foreach (var folder in folders ?? Array.Empty<VirtualFolderInfo>())
            {
                if (folder == null || string.IsNullOrWhiteSpace(folder.Name))
                {
                    continue;
                }

                var root = WritableLocation(folder, pluginDataRoot);
                if (root == null || !IsVideoCollection(folder.CollectionType))
                {
                    continue;
                }

                result.Add(new DownloadDestinationOption
                {
                    Id = LibraryPrefix + folder.Name,
                    Name = folder.Name,
                    Path = root,
                    CollectionType = folder.CollectionType?.ToString() ?? "mixed"
                });
            }

            return result;
        }

        /// <summary>
        /// Resolves a choice for one title. Errors are messages for the admin.
        /// </summary>
        public static DownloadTarget Resolve(
            string? choice,
            string? itemType,
            IEnumerable<VirtualFolderInfo>? folders,
            string? pluginDataRoot,
            string downloadsRoot)
        {
            var folderList = (folders ?? Array.Empty<VirtualFolderInfo>()).Where(f => f != null && !string.IsNullOrWhiteSpace(f.Name)).ToList();
            var value = string.IsNullOrWhiteSpace(choice) ? Auto : choice.Trim();
            var wanted = CollectionFor(itemType);

            if (string.Equals(value, Separate, StringComparison.OrdinalIgnoreCase))
            {
                return DownloadTarget.SeparateLibrary(downloadsRoot);
            }

            if (string.Equals(value, Auto, StringComparison.OrdinalIgnoreCase))
            {
                var match = folderList.FirstOrDefault(f => f.CollectionType == wanted && !FederationLibraryTargets.IsLegacySplitName(f.Name) && WritableLocation(f, pluginDataRoot) != null);
                return match == null
                    ? DownloadTarget.SeparateLibrary(downloadsRoot)
                    : DownloadTarget.Library(match.Name, WritableLocation(match, pluginDataRoot)!);
            }

            if (value.StartsWith(LibraryPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var name = value.Substring(LibraryPrefix.Length).Trim();
                var folder = folderList.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
                if (folder == null)
                {
                    return DownloadTarget.Failed($"There is no library called \"{name}\" on this server.");
                }

                var root = WritableLocation(folder, pluginDataRoot);
                return root == null
                    ? DownloadTarget.Failed($"The {folder.Name} library has no folder of its own to download into.")
                    : DownloadTarget.Library(folder.Name, root);
            }

            if (value.StartsWith(FolderPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var requested = value.Substring(FolderPrefix.Length).Trim();
                string path;
                if (Path.IsPathRooted(requested))
                {
                    path = requested;
                }
                else
                {
                    if (!IsPlainFolderName(requested))
                    {
                        return DownloadTarget.Failed("Give the new folder a plain name (no slashes), or a full path starting with /.");
                    }

                    // A bare name goes next to the matching library's folder:
                    // "Anime" beside /media/movies is /media/Anime.
                    var sibling = folderList.FirstOrDefault(f => f.CollectionType == wanted && WritableLocation(f, pluginDataRoot) != null)
                        ?? folderList.FirstOrDefault(f => WritableLocation(f, pluginDataRoot) != null);
                    var parent = sibling == null ? null : Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(WritableLocation(sibling, pluginDataRoot)!));
                    if (string.IsNullOrEmpty(parent))
                    {
                        return DownloadTarget.Failed("Could not tell where to make a new folder. Give a full path starting with /.");
                    }

                    path = Path.Combine(parent, requested);
                }

                string full;
                try
                {
                    full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
                }
                catch (Exception)
                {
                    return DownloadTarget.Failed("That folder path is not valid.");
                }

                if (full.Length <= 1 || IsInside(full, pluginDataRoot) || IsInside(pluginDataRoot, full))
                {
                    return DownloadTarget.Failed("Pick a folder outside the plugin's own data folder.");
                }

                // Already one of a library's folders: download into that library.
                var owner = folderList.FirstOrDefault(f => (f.Locations ?? Array.Empty<string>()).Any(l => SamePath(l, full)));
                if (owner != null)
                {
                    return DownloadTarget.Library(owner.Name, full);
                }

                var nested = folderList.FirstOrDefault(f => (f.Locations ?? Array.Empty<string>()).Any(l => IsInside(full, l)));
                if (nested != null)
                {
                    // Inside an existing library's folder: its scan already covers it.
                    return DownloadTarget.Library(nested.Name, full);
                }

                var libraryName = UniqueLibraryName(Path.GetFileName(full), folderList);
                return DownloadTarget.NewLibrary(libraryName, full, wanted ?? CollectionTypeOptions.movies);
            }

            return DownloadTarget.Failed("Unknown download destination.");
        }

        /// <summary>
        /// Folder and file name inside the destination, in the layout Jellyfin
        /// identifies: "Name (Year)/Name (Year).ext" for films,
        /// "Series/Season 01/Series S01E02.ext" for episodes. The separate
        /// downloads library keeps its old flat names.
        /// </summary>
        public static (string Directory, string FileName) Layout(DownloadNaming naming, bool flat)
        {
            var ext = "." + naming.Extension;
            var name = SafeComponent(naming.Name, "download");
            if (flat)
            {
                return (string.Empty, name + ext);
            }

            if (IsEpisode(naming.ItemType) && !string.IsNullOrWhiteSpace(naming.SeriesName))
            {
                var series = SafeComponent(naming.SeriesName, "Show");
                var season = naming.Season ?? 1;
                var seasonDir = season == 0 ? "Specials" : "Season " + season.ToString("00", System.Globalization.CultureInfo.InvariantCulture);
                var file = naming.Episode is int episode
                    ? series + " S" + season.ToString("00", System.Globalization.CultureInfo.InvariantCulture) + "E" + episode.ToString("00", System.Globalization.CultureInfo.InvariantCulture)
                    : name;
                return (Path.Combine(series, seasonDir), file + ext);
            }

            if (IsMovie(naming.ItemType))
            {
                var title = naming.Year is int year and > 1800 ? name + " (" + year.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")" : name;
                return (title, title + ext);
            }

            return (string.Empty, name + ext);
        }

        /// <summary>
        /// Collection type a title belongs in. Null for anything a video library
        /// can't hold.
        /// </summary>
        internal static CollectionTypeOptions? CollectionFor(string? itemType)
        {
            if (IsEpisode(itemType))
            {
                return CollectionTypeOptions.tvshows;
            }

            return string.Equals(itemType, nameof(BaseItemKind.MusicVideo), StringComparison.OrdinalIgnoreCase)
                ? CollectionTypeOptions.musicvideos
                : CollectionTypeOptions.movies;
        }

        /// <summary>The library's first folder that isn't one of the plugin's own.</summary>
        internal static string? WritableLocation(VirtualFolderInfo folder, string? pluginDataRoot)
            => (folder.Locations ?? Array.Empty<string>())
                .FirstOrDefault(l => !string.IsNullOrWhiteSpace(l) && !IsInside(l, pluginDataRoot) && !SamePath(l, pluginDataRoot));

        internal static bool IsInside(string? path, string? root)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root))
            {
                return false;
            }

            try
            {
                var fullPath = Path.GetFullPath(path);
                var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
                return fullPath.StartsWith(fullRoot, StringComparison.Ordinal);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool SamePath(string? a, string? b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
            {
                return false;
            }

            try
            {
                return string.Equals(
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
                    StringComparison.Ordinal);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool IsVideoCollection(CollectionTypeOptions? type)
            => type is null or CollectionTypeOptions.movies or CollectionTypeOptions.tvshows or CollectionTypeOptions.homevideos or CollectionTypeOptions.musicvideos or CollectionTypeOptions.mixed;

        private static bool IsEpisode(string? itemType)
            => string.Equals(itemType, nameof(BaseItemKind.Episode), StringComparison.OrdinalIgnoreCase);

        private static bool IsMovie(string? itemType)
            => string.IsNullOrWhiteSpace(itemType)
                || string.Equals(itemType, nameof(BaseItemKind.Movie), StringComparison.OrdinalIgnoreCase)
                || string.Equals(itemType, nameof(BaseItemKind.Video), StringComparison.OrdinalIgnoreCase);

        private static bool IsPlainFolderName(string name)
            => !string.IsNullOrWhiteSpace(name)
                && name != "."
                && name != ".."
                && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
                && name.IndexOfAny(new[] { '/', '\\' }) < 0;

        private static string UniqueLibraryName(string baseName, IReadOnlyCollection<VirtualFolderInfo> folders)
        {
            var name = string.IsNullOrWhiteSpace(baseName) ? "Downloads" : baseName.Trim();
            var candidate = name;
            for (var n = 2; folders.Any(f => string.Equals(f.Name, candidate, StringComparison.OrdinalIgnoreCase)); n++)
            {
                candidate = name + " " + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            return candidate;
        }

        /// <summary>A file or folder name from friend-supplied metadata.</summary>
        internal static string SafeComponent(string? name, string fallback)
        {
            var trimmed = string.IsNullOrWhiteSpace(name) ? fallback : name.Trim();
            var invalid = Path.GetInvalidFileNameChars();
            var chars = trimmed.Select(c => Array.IndexOf(invalid, c) >= 0 || c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' ? '_' : c).ToArray();
            var safe = new string(chars).Trim().Trim('.').Trim();
            if (string.IsNullOrEmpty(safe))
            {
                safe = fallback;
            }

            return safe.Length <= 120 ? safe : safe.Substring(0, 120).TrimEnd();
        }
    }

    /// <summary>One library a download can go to.</summary>
    public sealed class DownloadDestinationOption
    {
        /// <summary>Gets or sets the choice value ("library:Movies").</summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>Gets or sets the library name.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Gets or sets the folder files are written in.</summary>
        public string Path { get; set; } = string.Empty;

        /// <summary>Gets or sets the library type ("movies", "tvshows").</summary>
        public string CollectionType { get; set; } = "mixed";
    }

    /// <summary>What a title needs to be named on disk.</summary>
    public sealed record DownloadNaming(string Name, int? Year, string? ItemType, string? SeriesName, int? Season, int? Episode, string Extension);

    /// <summary>A resolved destination: the folder written in and the library that scans it.</summary>
    public sealed record DownloadTarget(string? Error, string Root, string? LibraryName, bool IsSeparate, string? CreateLibrary, CollectionTypeOptions? CreateType)
    {
        /// <summary>Gets a value indicating whether the choice resolved.</summary>
        public bool Ok => Error == null;

        internal static DownloadTarget Failed(string error) => new(error, string.Empty, null, false, null, null);

        internal static DownloadTarget SeparateLibrary(string downloadsRoot) => new(null, downloadsRoot, null, true, null, null);

        internal static DownloadTarget Library(string name, string root) => new(null, root, name, false, null, null);

        internal static DownloadTarget NewLibrary(string name, string root, CollectionTypeOptions type) => new(null, root, name, false, name, type);
    }

    /// <summary>
    /// Files this server downloaded from friends into ordinary libraries. They
    /// are never shared onward; inside the downloads folder the path already
    /// says so, elsewhere this list does until the item's own stamp lands.
    /// Kept next to the plugin data as JSON.
    /// </summary>
    internal static class DownloadedFileRegistry
    {
        private static readonly object Sync = new();
        private static HashSet<string>? _paths;

        /// <summary>Test hook: where the list is kept; null uses the plugin data folder.</summary>
        internal static string? StorePathOverride { get; set; }

        internal static void Add(string path)
        {
            var key = Normalize(path);
            if (key == null)
            {
                return;
            }

            lock (Sync)
            {
                var paths = Load();
                if (paths.Add(key))
                {
                    Save(paths);
                }
            }
        }

        internal static bool Contains(string? path)
        {
            var key = Normalize(path);
            if (key == null)
            {
                return false;
            }

            lock (Sync)
            {
                return Load().Contains(key);
            }
        }

        /// <summary>Test hook.</summary>
        internal static void Reset()
        {
            lock (Sync)
            {
                _paths = null;
            }
        }

        private static string? StorePath()
        {
            if (StorePathOverride != null)
            {
                return StorePathOverride;
            }

            var data = Plugin.Instance?.DataFolderPath;
            return string.IsNullOrEmpty(data) ? null : System.IO.Path.Combine(data, "federation-downloaded-files.json");
        }

        private static HashSet<string> Load()
        {
            if (_paths != null)
            {
                return _paths;
            }

            _paths = new HashSet<string>(StringComparer.Ordinal);
            var store = StorePath();
            try
            {
                if (store != null && File.Exists(store))
                {
                    var list = System.Text.Json.JsonSerializer.Deserialize<List<string>>(File.ReadAllText(store));
                    foreach (var item in list ?? new List<string>())
                    {
                        _paths.Add(item);
                    }
                }
            }
            catch (Exception)
            {
                // A damaged list only loses the pre-scan guard; the item stamps still apply.
            }

            return _paths;
        }

        private static void Save(HashSet<string> paths)
        {
            var store = StorePath();
            if (store == null)
            {
                return;
            }

            try
            {
                var temp = store + ".tmp";
                File.WriteAllText(temp, System.Text.Json.JsonSerializer.Serialize(paths.OrderBy(p => p, StringComparer.Ordinal).ToList()));
                File.Move(temp, store, overwrite: true);
            }
            catch (Exception)
            {
                // Same as above: best effort.
            }
        }

        private static string? Normalize(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            try
            {
                return System.IO.Path.GetFullPath(path);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
