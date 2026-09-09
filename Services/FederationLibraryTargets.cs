using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Federation.Configuration;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Resolves where a friend's library should land on this server: existing
    /// Movies/Shows (or Music, …) folders by collection type, not a new virtual
    /// library named after the friend's folder ("TV Shows", "The PSP Experience",
    /// "Federated Movies").
    /// </summary>
    public static class FederationLibraryTargets
    {
        internal const string LegacyMoviesName = "Federated Movies";
        internal const string LegacyShowsName = "Federated Shows";

        /// <summary>
        /// Default local library name when this server has no folder of the
        /// matching collection type yet.
        /// </summary>
        public static string DefaultName(string mediaType) => Normalize(mediaType) switch
        {
            "series" or "season" or "episode" => "Shows",
            "musicalbum" or "audio" => "Music",
            "musicvideo" => "Music Videos",
            "book" => "Books",
            "photo" or "photoalbum" => "Photos",
            "boxset" => "Collections",
            _ => "Movies"
        };

        /// <summary>
        /// True for the split libraries an older plugin version auto-created
        /// instead of merging into Movies/Shows.
        /// </summary>
        public static bool IsLegacySplitName(string? name) =>
            string.Equals(name, LegacyMoviesName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, LegacyShowsName, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Picks an existing local folder of the matching collection type,
        /// skipping leftover Federated Movies/Shows tiles. Falls back to
        /// <see cref="DefaultName"/>.
        /// </summary>
        public static string Resolve(string mediaType, IEnumerable<VirtualFolderInfo>? localFolders)
        {
            var wanted = CollectionTypeFor(mediaType);
            if (wanted != null && localFolders != null)
            {
                var match = localFolders.FirstOrDefault(folder =>
                    folder != null
                    && !string.IsNullOrWhiteSpace(folder.Name)
                    && !IsLegacySplitName(folder.Name)
                    && folder.CollectionType == wanted);
                if (match != null)
                {
                    return match.Name;
                }
            }

            return DefaultName(mediaType);
        }

        /// <summary>
        /// Rewrites auto-managed and legacy split mappings onto existing
        /// Movies/Shows folders and merges duplicates. Returns retired local
        /// names so leftover virtual folders can be removed. When
        /// <paramref name="cache"/> is provided, catalog keys and MappingName
        /// follow the new local library name so prune/upsert keep hitting the
        /// same entries.
        /// </summary>
        public static List<string> Collapse(
            PluginConfiguration config,
            IEnumerable<VirtualFolderInfo>? localFolders,
            FederationItemCache? cache = null)
        {
            var retired = new List<string>();
            var remaps = new List<(string From, string To)>();
            config.LibraryMappings ??= new List<LibraryMapping>();
            foreach (var mapping in config.LibraryMappings)
            {
                if (!mapping.AutoManaged && !IsLegacySplitName(mapping.LocalLibraryName))
                {
                    continue;
                }

                var target = Resolve(mapping.MediaType, localFolders);
                if (string.Equals(mapping.LocalLibraryName, target, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                retired.Add(mapping.LocalLibraryName);
                remaps.Add((mapping.LocalLibraryName, target));
                mapping.LocalLibraryName = target;
            }

            MergeSameName(config);

            if (cache != null)
            {
                foreach (var (from, to) in remaps)
                {
                    cache.RemapMapping(from, to);
                }

                // 0.0.134 already rewrote mappings on some installs but left
                // Federated Movies/Shows cache keys behind. Collapse is then a
                // no-op on config; still move those leftovers.
                foreach (var legacy in new[] { LegacyMoviesName, LegacyShowsName })
                {
                    if (config.LibraryMappings.Any(m =>
                            string.Equals(m.LocalLibraryName, legacy, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    var mediaType = string.Equals(legacy, LegacyShowsName, StringComparison.OrdinalIgnoreCase)
                        ? "Series"
                        : "Movie";
                    cache.RemapMapping(legacy, Resolve(mediaType, localFolders));
                }
            }

            return retired
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(name => config.LibraryMappings.All(m =>
                    !string.Equals(m.LocalLibraryName, name, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }

        /// <summary>
        /// Moves cache entries whose MappingName is no longer a configured
        /// mapping (Plex section names left behind after a previous collapse)
        /// onto the current Movies/Shows target for their item type.
        /// </summary>
        public static int RemapStaleCacheEntries(
            PluginConfiguration config,
            IEnumerable<VirtualFolderInfo>? localFolders,
            FederationItemCache cache)
        {
            var current = new HashSet<string>(
                (config.LibraryMappings ?? new List<LibraryMapping>())
                    .Select(m => m.LocalLibraryName)
                    .Where(n => !string.IsNullOrWhiteSpace(n)),
                StringComparer.OrdinalIgnoreCase);

            var staleNames = cache.GetAllEntries()
                .Select(e => e.MappingName)
                .Where(n => !string.IsNullOrWhiteSpace(n) && !current.Contains(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var remapped = 0;
            foreach (var oldName in staleNames)
            {
                var sample = cache.GetEntriesForMapping(oldName).FirstOrDefault();
                if (sample == null)
                {
                    continue;
                }

                var target = Resolve(sample.ItemType, localFolders);
                if (string.Equals(oldName, target, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                remapped += cache.RemapMapping(oldName, target);
            }

            return remapped;
        }

        internal static CollectionTypeOptions? CollectionTypeFor(string mediaType) => Normalize(mediaType) switch
        {
            "movie" or "video" => CollectionTypeOptions.movies,
            "series" or "season" or "episode" => CollectionTypeOptions.tvshows,
            "musicalbum" or "audio" or "musicvideo" => CollectionTypeOptions.music,
            "book" => CollectionTypeOptions.books,
            "photo" or "photoalbum" => CollectionTypeOptions.homevideos,
            "boxset" => CollectionTypeOptions.boxsets,
            _ => null
        };

        private static void MergeSameName(PluginConfiguration config)
        {
            var groups = config.LibraryMappings
                .GroupBy(m => (m.LocalLibraryName ?? string.Empty).Trim(), StringComparer.OrdinalIgnoreCase)
                .Where(g => !string.IsNullOrEmpty(g.Key) && g.Count() > 1)
                .ToList();

            foreach (var group in groups)
            {
                var keep = group.First();
                foreach (var extra in group.Skip(1))
                {
                    foreach (var serverId in extra.RemoteServerIds ?? new List<string>())
                    {
                        if (!keep.RemoteServerIds.Contains(serverId))
                        {
                            keep.RemoteServerIds.Add(serverId);
                        }
                    }

                    foreach (var source in extra.RemoteLibrarySources ?? new List<RemoteLibrarySource>())
                    {
                        if (!keep.RemoteLibrarySources.Any(s =>
                                s.ServerId == source.ServerId
                                && string.Equals(s.RemoteLibraryId, source.RemoteLibraryId, StringComparison.Ordinal)))
                        {
                            keep.RemoteLibrarySources.Add(source);
                        }
                    }

                    keep.Enabled = keep.Enabled || extra.Enabled;
                    keep.AutoProvision = keep.AutoProvision || extra.AutoProvision;
                    config.LibraryMappings.Remove(extra);
                }
            }
        }

        private static string Normalize(string mediaType) => (mediaType ?? string.Empty).Trim().ToLowerInvariant();
    }
}
