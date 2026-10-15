using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Federation.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// The guided "bring your Plex libraries over" flow: reads the folders each Plex library scans,
    /// works out where this Jellyfin sees the same files, creates matching ordinary Jellyfin libraries
    /// over them, and points the Plex metadata source at them so Plex's artwork and descriptions follow.
    /// The libraries it makes are plain Jellyfin libraries over real files; nothing is streamed from Plex.
    /// </summary>
    public class PlexLibraryImportService
    {
        private const int ReportUnmatchedLimit = 25;

        private readonly ILogger<PlexLibraryImportService> _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly PlexMetadataSourceService _plex;

        /// <summary>Initializes a new instance of the <see cref="PlexLibraryImportService"/> class.</summary>
        public PlexLibraryImportService(ILogger<PlexLibraryImportService> logger, ILibraryManager libraryManager, PlexMetadataSourceService plex)
        {
            _logger = logger;
            _libraryManager = libraryManager;
            _plex = plex;
        }

        /// <summary>Test seam: replaces the directory check.</summary>
        internal Func<string, bool> DirectoryExists { get; set; } = Directory.Exists;

        /// <summary>What happens to one Plex library on import.</summary>
        public enum SectionState
        {
            /// <summary>Its folders were found here; a new Jellyfin library will be created.</summary>
            Ready,

            /// <summary>A Jellyfin movie/show library already covers its folders; it is reused.</summary>
            Linked,

            /// <summary>A Jellyfin library covers its folders but is not a Movies/Shows library, so Plex art cannot apply.</summary>
            WrongType,

            /// <summary>None of its folders exist on this server; a path rewrite is needed.</summary>
            Missing,
        }

        /// <summary>One Plex folder and where this server sees it.</summary>
        public sealed record FolderPlan(string PlexPath, string LocalPath, bool Found);

        /// <summary>One Plex library and what the import would do with it.</summary>
        public sealed record SectionPlan(
            string Key,
            string Title,
            string Type,
            IReadOnlyList<FolderPlan> Folders,
            SectionState State,
            string? LibraryId,
            string? LibraryName);

        /// <summary>The outcome for one Plex library after import.</summary>
        public sealed record SectionResult(string Key, string Title, bool Ok, string? LibraryName, string Message);

        /// <summary>How many of a library's titles found their Plex twin.</summary>
        public sealed record LibraryReport(string Id, string Name, int Total, int Matched, IReadOnlyList<string> Unmatched);

        /// <summary>
        /// Rewrites a Plex path with the longest matching mapping. Prefixes match whole folders only;
        /// Windows-style prefixes compare case-insensitively. The remainder takes the target's separator.
        /// Unmapped paths are returned unchanged.
        /// </summary>
        internal static string MapPath(string plexPath, IEnumerable<PlexPathMapping>? mappings)
        {
            var normalizedPath = Normalize(plexPath);
            PlexPathMapping? best = null;
            var bestLength = -1;
            foreach (var mapping in mappings ?? Enumerable.Empty<PlexPathMapping>())
            {
                if (string.IsNullOrWhiteSpace(mapping.From) || string.IsNullOrWhiteSpace(mapping.To))
                {
                    continue;
                }

                var from = Normalize(mapping.From);
                var comparison = IsWindowsPath(mapping.From) || IsWindowsPath(plexPath) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                var matches = from.Length == 0
                    || string.Equals(normalizedPath, from, comparison)
                    || (normalizedPath.StartsWith(from, comparison) && normalizedPath[from.Length] == '/');
                if (matches && from.Length > bestLength)
                {
                    best = mapping;
                    bestLength = from.Length;
                }
            }

            if (best == null)
            {
                return plexPath;
            }

            var rest = normalizedPath.Substring(bestLength).TrimStart('/');
            var to = best.To.Trim();
            var separator = IsWindowsPath(to) ? '\\' : '/';
            to = to.TrimEnd('/', '\\');
            if (rest.Length == 0)
            {
                return to.Length == 0 ? separator.ToString() : to;
            }

            return to + separator + rest.Replace('/', separator);
        }

        /// <summary>
        /// Plans every Plex library against this server's folders and existing libraries. Pure: the
        /// directory check and folder list are passed in.
        /// </summary>
        internal static IReadOnlyList<SectionPlan> BuildPlan(
            IEnumerable<PlexSection> sections,
            IEnumerable<PlexPathMapping>? mappings,
            IReadOnlyList<VirtualFolderInfo> libraries,
            Func<string, bool> exists)
        {
            var mappingList = (mappings ?? Enumerable.Empty<PlexPathMapping>()).ToList();
            var plans = new List<SectionPlan>();
            foreach (var section in sections)
            {
                var folders = (section.Locations ?? Array.Empty<string>())
                    .Select(p =>
                    {
                        var local = MapPath(p, mappingList);
                        return new FolderPlan(p, local, SafeExists(exists, local));
                    })
                    .ToList();

                var found = folders.Where(f => f.Found).ToList();
                var owner = found
                    .Select(f => libraries.FirstOrDefault(l => Covers(l, f.LocalPath)))
                    .FirstOrDefault(l => l != null);

                SectionState state;
                if (owner != null)
                {
                    state = owner.CollectionType == ExpectedType(section.Type) ? SectionState.Linked : SectionState.WrongType;
                }
                else
                {
                    state = found.Count > 0 ? SectionState.Ready : SectionState.Missing;
                }

                plans.Add(new SectionPlan(section.Key, section.Title, section.Type, folders, state, owner?.ItemId, owner?.Name));
            }

            return plans;
        }

        /// <summary>Picks a library name not already taken, preferring the Plex library's own name.</summary>
        internal static string UniqueName(string title, IEnumerable<string> taken)
        {
            var names = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
            var baseName = string.IsNullOrWhiteSpace(title) ? "Plex" : title.Trim();
            if (!names.Contains(baseName))
            {
                return baseName;
            }

            var candidate = baseName + " (Plex)";
            for (var i = 2; names.Contains(candidate); i++)
            {
                candidate = baseName + " (Plex " + i + ")";
            }

            return candidate;
        }

        /// <summary>Plans the connected Plex server's libraries; <paramref name="mappings"/> previews unsaved rewrites.</summary>
        public async Task<IReadOnlyList<SectionPlan>> PlanAsync(IReadOnlyList<PlexPathMapping>? mappings, CancellationToken cancellationToken)
        {
            var settings = Settings;
            var sections = await _plex.ListSectionsAsync(settings.ServerUrl, settings.Token, cancellationToken).ConfigureAwait(false);
            return BuildPlan(sections, mappings ?? settings.PathMappings, _libraryManager.GetVirtualFolders(), DirectoryExists);
        }

        /// <summary>
        /// Saves the path rewrites, creates or reuses a Jellyfin library for each chosen Plex library, turns the
        /// Plex metadata source on for them and queues one library scan. Each library succeeds or fails on its own.
        /// </summary>
        public async Task<IReadOnlyList<SectionResult>> ImportAsync(IReadOnlyCollection<string> sectionKeys, IReadOnlyList<PlexPathMapping> mappings, CancellationToken cancellationToken)
        {
            var cleanMappings = mappings
                .Where(m => !string.IsNullOrWhiteSpace(m.From) && !string.IsNullOrWhiteSpace(m.To))
                .Select(m => new PlexPathMapping { From = m.From.Trim(), To = m.To.Trim() })
                .ToList();
            var plan = await PlanAsync(cleanMappings, cancellationToken).ConfigureAwait(false);
            var results = new List<SectionResult>();
            var libraryIds = new List<string>();
            var created = false;

            foreach (var section in plan.Where(p => sectionKeys.Contains(p.Key, StringComparer.Ordinal)))
            {
                switch (section.State)
                {
                    case SectionState.Linked:
                        libraryIds.Add(section.LibraryId!);
                        results.Add(new SectionResult(section.Key, section.Title, true, section.LibraryName, "Already in Jellyfin as \"" + section.LibraryName + "\". Plex artwork turned on for it."));
                        break;
                    case SectionState.WrongType:
                        results.Add(new SectionResult(section.Key, section.Title, false, section.LibraryName, "Its folders are already in the Jellyfin library \"" + section.LibraryName + "\", which is not a " + TypeLabel(section.Type) + " library. Change that library's content type or remove it, then try again."));
                        break;
                    case SectionState.Missing:
                        results.Add(new SectionResult(section.Key, section.Title, false, null, "This Jellyfin server cannot see " + string.Join(", ", section.Folders.Select(f => f.LocalPath)) + ". Add a folder rewrite, or make the drive available to Jellyfin."));
                        break;
                    default:
                        var outcome = await CreateLibraryAsync(section).ConfigureAwait(false);
                        results.Add(outcome.Result);
                        if (outcome.Id != null)
                        {
                            libraryIds.Add(outcome.Id);
                            created = true;
                        }

                        break;
                }
            }

            var importedKeys = results.Where(r => r.Ok).Select(r => r.Key).ToList();
            var settings = Settings;
            var wasAllLibraries = settings.Enabled && settings.JellyfinLibraryIds.Count == 0;
            Save(s =>
            {
                s.PathMappings = cleanMappings;
                if (importedKeys.Count == 0)
                {
                    return;
                }

                s.Enabled = true;
                // An empty section list already means "every Plex movie and show library".
                if (s.SectionKeys.Count > 0)
                {
                    s.SectionKeys = s.SectionKeys.Union(importedKeys, StringComparer.Ordinal).ToList();
                }
                s.JellyfinLibraryIds = wasAllLibraries ? s.JellyfinLibraryIds : s.JellyfinLibraryIds.Union(libraryIds, StringComparer.OrdinalIgnoreCase).ToList();
            });

            if (importedKeys.Count > 0)
            {
                _plex.Invalidate();
                await _plex.ApplyFetchersAsync(Settings.JellyfinLibraryIds, true).ConfigureAwait(false);
            }

            // New libraries pick Plex up as they are scanned; libraries that already existed need a refresh.
            if (plan.Any(p => p.State == SectionState.Linked && importedKeys.Contains(p.Key)))
            {
                _plex.QueueFullRefresh();
            }

            if (created)
            {
                _libraryManager.QueueLibraryScan();
            }

            return results;
        }

        /// <summary>For each library Plex feeds, how many movies and shows matched a Plex title.</summary>
        public async Task<IReadOnlyList<LibraryReport>> ReportAsync(CancellationToken cancellationToken)
        {
            var settings = Settings;
            var libraries = _plex.ListEligibleLibraries()
                .Where(l => settings.JellyfinLibraryIds.Count == 0 || settings.JellyfinLibraryIds.Contains(l.Id, StringComparer.OrdinalIgnoreCase))
                .ToList();

            var reports = new List<LibraryReport>();
            foreach (var library in libraries)
            {
                if (!Guid.TryParse(library.Id, out var id))
                {
                    continue;
                }

                var items = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    AncestorIds = new[] { id },
                    IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series },
                    Recursive = true,
                });

                var matched = 0;
                var unmatched = new List<string>();
                foreach (var item in items)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (await _plex.FindMatchAsync(item, cancellationToken).ConfigureAwait(false) != null)
                    {
                        matched++;
                    }
                    else if (unmatched.Count < ReportUnmatchedLimit)
                    {
                        unmatched.Add(item.ProductionYear is int year ? item.Name + " (" + year + ")" : item.Name);
                    }
                }

                reports.Add(new LibraryReport(library.Id, library.Name, items.Count, matched, unmatched));
            }

            return reports;
        }

        private async Task<(SectionResult Result, string? Id)> CreateLibraryAsync(SectionPlan section)
        {
            var name = UniqueName(section.Title, _libraryManager.GetVirtualFolders().Select(v => v.Name));
            var paths = section.Folders.Where(f => f.Found).Select(f => new MediaPathInfo { Path = f.LocalPath }).ToArray();
            try
            {
                await _libraryManager.AddVirtualFolder(name, ExpectedType(section.Type), new LibraryOptions { PathInfos = paths }, refreshLibrary: false).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Federation] Could not create a Jellyfin library for Plex library {Title}", section.Title);
                return (new SectionResult(section.Key, section.Title, false, null, "Jellyfin would not create the library: " + ex.Message), null);
            }

            var id = _libraryManager.GetVirtualFolders().FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase))?.ItemId;
            _logger.LogInformation("[Federation] Created library {Name} from Plex library {Title} ({Count} folders)", name, section.Title, paths.Length);
            var skipped = section.Folders.Count - paths.Length;
            var message = "Created the Jellyfin library \"" + name + "\"." + (skipped > 0 ? " " + skipped + " Plex folder(s) were not found here and were left out." : string.Empty);
            return (new SectionResult(section.Key, section.Title, id != null, name, id != null ? message : "The library was created but Jellyfin did not list it. Reload and check the Libraries page."), id);
        }

        private static CollectionTypeOptions ExpectedType(string plexType)
            => string.Equals(plexType, "show", StringComparison.OrdinalIgnoreCase) ? CollectionTypeOptions.tvshows : CollectionTypeOptions.movies;

        private static string TypeLabel(string plexType)
            => string.Equals(plexType, "show", StringComparison.OrdinalIgnoreCase) ? "Shows" : "Movies";

        private static bool Covers(VirtualFolderInfo library, string path)
        {
            var target = Normalize(path);
            return (library.Locations ?? Array.Empty<string>()).Any(location =>
            {
                var root = Normalize(location);
                return root.Length > 0 && (string.Equals(target, root, StringComparison.Ordinal) || target.StartsWith(root + "/", StringComparison.Ordinal));
            });
        }

        private static bool SafeExists(Func<string, bool> exists, string path)
        {
            try
            {
                return exists(path);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string Normalize(string path)
            => (path ?? string.Empty).Trim().Replace('\\', '/').TrimEnd('/');

        private static bool IsWindowsPath(string path)
            => path.Contains('\\', StringComparison.Ordinal) || (path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':');

        private PlexMetadataSourceSettings Settings
            => SettingsOverride?.Invoke() ?? Plugin.Instance?.Configuration.PlexMetadataSource ?? new PlexMetadataSourceSettings();

        /// <summary>Test seam: replaces the plugin configuration as the source of settings.</summary>
        internal Func<PlexMetadataSourceSettings>? SettingsOverride { get; set; }

        private void Save(Action<PlexMetadataSourceSettings> change)
        {
            if (SettingsOverride != null)
            {
                change(SettingsOverride());
                return;
            }

            var plugin = Plugin.Instance!;
            var config = plugin.Configuration;
            config.PlexMetadataSource ??= new PlexMetadataSourceSettings();
            change(config.PlexMetadataSource);
            plugin.UpdateConfiguration(config);
        }
    }
}
