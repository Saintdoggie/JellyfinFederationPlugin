using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        /// episode, then removes any previously-written file that no longer
        /// corresponds to a current entry. No-op (does not even touch the export
        /// directory) when <see cref="PluginConfiguration.EnablePlexStrmExport"/> is
        /// off.
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

                var fullPath = Path.Combine(basePath, relativePath);
                try
                {
                    var dir = Path.GetDirectoryName(fullPath);
                    if (!string.IsNullOrEmpty(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    WriteIfChanged(fullPath, url);
                    written.Add(fullPath);
                    exported++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Federation] Could not write .strm file for {Name} ({Path})", entry.Metadata.Name, fullPath);
                }
            }

            RemoveStale(basePath, written);

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
        /// Deletes any <c>.strm</c> file under <paramref name="basePath"/> that
        /// this run didn't (re)write - a federated item removed on this pass, a
        /// rename that changed its target path, or a source that started being
        /// skipped - then prunes any directory left empty by that cleanup.
        /// </summary>
        private void RemoveStale(string basePath, HashSet<string> written)
        {
            IEnumerable<string> existing;
            try
            {
                existing = Directory.EnumerateFiles(basePath, "*.strm", SearchOption.AllDirectories).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Federation] Could not enumerate Plex .strm export directory {Path} for cleanup", basePath);
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
