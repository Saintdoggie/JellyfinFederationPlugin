using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Exports federated movies/episodes as <c>.strm</c> files: plain text files
    /// containing nothing but the item's existing proxy stream URL (the same URL
    /// <see cref="FederationLibraryManager.BuildStaticPath"/> already stamps on
    /// <c>item.Path</c> for Jellyfin clients). A <c>.strm</c> file is a standard
    /// convention several media servers (Plex, Kodi, Emby) understand natively:
    /// scanned like any other video file, but on play the referenced URL is opened
    /// directly instead of reading the file as video data.
    /// <para>
    /// This exists because Plex has no equivalent of Jellyfin's plugin system any
    /// more (its third-party "Channels" ecosystem was retired years ago) - there is
    /// no way to write a Plex-side plugin that live-browses/streams from an
    /// arbitrary remote catalog the way <see cref="FederationMediaSourceProvider"/>
    /// does for Jellyfin. The only thing Plex can scan is a filesystem path, so
    /// this writes files onto one instead - no different in spirit from every other
    /// URL this plugin already hands out, just persisted to disk so a *different*
    /// server's library scanner can find it. Nothing is downloaded or duplicated;
    /// the referenced URL still streams through the same
    /// <see cref="FederationStreamHandler"/> relay used for every other federated
    /// play.
    /// </para>
    /// </summary>
    public class PlexStrmExportService
    {
        private const string MoviesFolderName = "Movies";
        private const string ShowsFolderName = "Shows";
        private const string DefaultBasePath = "/media/federated";
        private const string ManifestFileName = ".federation-strm-files.json";
        private const string PluginStreamPath = "/Plugins/Federation/Stream";

        private readonly ILogger<PlexStrmExportService> _logger;
        private readonly FederationLibraryManager _federationManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="PlexStrmExportService"/> class.
        /// </summary>
        public PlexStrmExportService(ILogger<PlexStrmExportService> logger, FederationLibraryManager federationManager)
        {
            _logger = logger;
            _federationManager = federationManager;
        }

        /// <summary>
        /// Writes/refreshes <c>.strm</c> files for every currently-cached movie and
        /// episode, then removes previously-written files that no longer correspond
        /// to a current entry. Only files this plugin wrote (ownership manifest, or
        /// first-run URL adopt of its own proxy stream) are deleted; foreign
        /// <c>.strm</c> files under the export root are left alone. No-op (does not
        /// even touch the export directory) when
        /// <see cref="PluginConfiguration.EnablePlexStrmExport"/> is off.
        /// </summary>
        public Task ExportAsync(CancellationToken cancellationToken)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null || !config.EnablePlexStrmExport)
            {
                return Task.CompletedTask;
            }

            var basePath = string.IsNullOrWhiteSpace(config.PlexStrmExportPath)
                ? DefaultBasePath
                : config.PlexStrmExportPath.TrimEnd('/', '\\');

            try
            {
                Directory.CreateDirectory(basePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Federation] Could not create Plex .strm export directory {Path}", basePath);
                return Task.CompletedTask;
            }

            basePath = Path.GetFullPath(basePath);
            var ownedPaths = ResolveOwnedPaths(basePath);
            var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var exported = 0;
            var skipped = 0;

            foreach (var entry in _federationManager.GetAllEntries())
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (entry.ItemType != "Movie" && entry.ItemType != "Episode")
                {
                    continue;
                }

                var primary = entry.GetPrimarySource();
                if (primary == null)
                {
                    continue;
                }

                // Null here means either the server is gone/disabled or it has
                // per-remote-user access rules configured - same guard
                // BuildStaticPath already applies for the item.Path it stamps for
                // Jellyfin clients. A per-user restriction can't be enforced
                // through a static file an unrelated media server just reads off
                // disk, so those sources are skipped entirely rather than exported
                // anonymously.
                var url = _federationManager.BuildStaticPath(entry.ItemType, primary);
                if (url == null)
                {
                    skipped++;
                    continue;
                }

                var relativePath = entry.ItemType == "Movie" ? BuildMoviePath(entry) : BuildEpisodePath(entry);
                if (relativePath == null)
                {
                    continue;
                }

                var fullPath = Path.GetFullPath(Path.Combine(basePath, relativePath));
                try
                {
                    if (File.Exists(fullPath) && !ownedPaths.Contains(fullPath))
                    {
                        _logger.LogWarning("[Federation] Skipping .strm export for {Name}; unmanaged file already exists at {Path}", entry.Metadata.Name, fullPath);
                        continue;
                    }

                    var dir = Path.GetDirectoryName(fullPath);
                    if (!string.IsNullOrEmpty(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    WriteIfChanged(fullPath, url);
                    written.Add(fullPath);
                    ownedPaths.Add(fullPath);
                    exported++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Federation] Could not write .strm file for {Name} ({Path})", entry.Metadata.Name, fullPath);
                }
            }

            RemoveStale(basePath, written, ownedPaths);
            SaveManifest(basePath, written);

            _logger.LogInformation(
                "[Federation] Plex .strm export: {Exported} file(s) written, {Skipped} source(s) skipped (per-remote-user access rules)",
                exported,
                skipped);
            return Task.CompletedTask;
        }

        private static string? BuildMoviePath(FederatedCacheEntry entry)
        {
            var name = SafeFileName(entry.Metadata.Name);
            if (string.IsNullOrEmpty(name))
            {
                return null;
            }

            var folder = entry.Metadata.ProductionYear.HasValue
                ? $"{name} ({entry.Metadata.ProductionYear.Value})"
                : name;
            return Path.Combine(MoviesFolderName, folder, folder + ".strm");
        }

        private static string? BuildEpisodePath(FederatedCacheEntry entry)
        {
            // Without a known episode number there is no stable, Plex-recognizable
            // name to give this file - skip rather than guessing.
            if (entry.Metadata.IndexNumber is not int episodeNumber)
            {
                return null;
            }

            var seasonNumber = entry.Metadata.ParentIndexNumber ?? 0;
            var series = SafeFileName(string.IsNullOrWhiteSpace(entry.Metadata.SeriesName) ? "Unknown Show" : entry.Metadata.SeriesName);
            var seasonFolder = $"Season {seasonNumber:D2}";
            var episodeTitle = SafeFileName(entry.Metadata.Name);
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
                    // Same URL already on disk - skip the write so the file's mtime
                    // doesn't churn on every refresh for content that hasn't changed.
                    return;
                }
            }

            File.WriteAllText(path, url + "\n");
        }

        /// <summary>
        /// Deletes owned <c>.strm</c> files this run did not rewrite, then prunes
        /// directories left empty by that cleanup. Foreign files are never deleted.
        /// </summary>
        private void RemoveStale(string basePath, HashSet<string> written, HashSet<string> owned)
        {
            var removedDirs = new HashSet<string>();
            foreach (var path in owned)
            {
                if (written.Contains(path))
                {
                    continue;
                }

                try
                {
                    if (!File.Exists(path))
                    {
                        continue;
                    }

                    File.Delete(path);
                    var dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir))
                    {
                        removedDirs.Add(dir);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Federation] Could not remove stale .strm file {Path}", path);
                }
            }

            foreach (var dir in removedDirs)
            {
                RemoveIfEmpty(dir, basePath);
            }
        }

        private HashSet<string> ResolveOwnedPaths(string basePath)
        {
            var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var relative in LoadOwned(basePath))
            {
                try
                {
                    owned.Add(SafePath(basePath, relative));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Federation] Ignoring invalid .strm ownership entry {Path}", relative);
                }
            }

            return owned;
        }

        private List<string> LoadOwned(string basePath)
        {
            var manifestPath = Path.Combine(basePath, ManifestFileName);
            if (File.Exists(manifestPath))
            {
                try
                {
                    var parsed = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(manifestPath));
                    if (parsed != null)
                    {
                        return parsed;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Federation] Could not read Plex .strm ownership manifest {Path}; adopting by URL", manifestPath);
                }
            }

            // First run (or unreadable manifest): claim only files whose URL is
            // this plugin's proxy stream. Never sweep arbitrary .strm files.
            var owned = new List<string>();
            try
            {
                foreach (var path in Directory.EnumerateFiles(
                    basePath,
                    "*.strm",
                    new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                {
                    try
                    {
                        if (IsPluginStreamUrl(File.ReadAllText(path)))
                        {
                            owned.Add(Path.GetRelativePath(basePath, path));
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[Federation] Could not inspect .strm file {Path} for ownership", path);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Federation] Could not enumerate Plex .strm export directory {Path} for ownership", basePath);
            }

            return owned;
        }

        private void SaveManifest(string basePath, HashSet<string> written)
        {
            var manifestPath = Path.Combine(basePath, ManifestFileName);
            try
            {
                var relative = written.Select(p => Path.GetRelativePath(basePath, p)).ToList();
                File.WriteAllText(manifestPath, JsonSerializer.Serialize(relative));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Federation] Could not write Plex .strm ownership manifest {Path}", manifestPath);
            }
        }

        internal static bool IsPluginStreamUrl(string content)
        {
            var line = content.Trim();
            return Uri.TryCreate(line, UriKind.Absolute, out var uri)
                && uri.AbsolutePath.Contains(PluginStreamPath, StringComparison.OrdinalIgnoreCase);
        }

        private static string SafePath(string root, string relative)
        {
            if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
            {
                throw new IOException("Export path is outside its managed folder.");
            }

            var fullRoot = Path.GetFullPath(root);
            var full = Path.GetFullPath(Path.Combine(fullRoot, relative));
            var prefix = fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, StringComparison.Ordinal))
            {
                throw new IOException("Export path is outside its managed folder.");
            }

            return full;
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
}
