using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Jellyfin builds a federated item's own (primary) media source from its stamped path and
    /// names it after the movie, while this plugin's extra sources are named after their server.
    /// The version picker then reads "Civil War" next to "plexpal" and you cannot tell which server is
    /// which. This renames the item's own source after the server it plays from, in every item and
    /// playback-info response, so every client (web, Android, TV) shows one label per server.
    /// </summary>
    public class FederationMediaSourceNameFilter : IAsyncResultFilter
    {
        private readonly ILibraryManager _libraryManager;
        private readonly FederationLibraryManager _federation;

        /// <summary>Initializes a new instance of the <see cref="FederationMediaSourceNameFilter"/> class.</summary>
        public FederationMediaSourceNameFilter(ILibraryManager libraryManager, FederationLibraryManager federation)
        {
            _libraryManager = libraryManager;
            _federation = federation;
        }

        /// <inheritdoc />
        public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
        {
            try
            {
                if (context.Result is ObjectResult { Value: { } value })
                {
                    Visit(value);
                }
            }
            catch (Exception)
            {
                // Labelling must never break an item or playback response.
            }

            await next().ConfigureAwait(false);
        }

        private void Visit(object value)
        {
            switch (value)
            {
                case BaseItemDto dto:
                    if (Rename(dto.Id, dto.MediaSources, addAlternates: true) is { } withAlternates)
                    {
                        dto.MediaSources = withAlternates.ToArray();
                    }

                    break;
                case PlaybackInfoResponse playback:
                    // The response does not carry the item id; the item's own source's Id *is* the item id.
                    foreach (var source in playback.MediaSources ?? new List<MediaSourceInfo>())
                    {
                        if (Guid.TryParse(source.Id, out var id))
                        {
                            Rename(id, playback.MediaSources, addAlternates: false);
                            break;
                        }
                    }

                    break;
                case string:
                    break;
                case IEnumerable sequence:
                    foreach (var element in sequence)
                    {
                        if (element is BaseItemDto)
                        {
                            Visit(element);
                        }
                    }

                    break;
                default:
                    // QueryResult<BaseItemDto> and similar: look for an Items collection.
                    var items = value.GetType().GetProperty("Items")?.GetValue(value);
                    if (items is IEnumerable list && items is not string)
                    {
                        foreach (var element in list)
                        {
                            if (element is BaseItemDto)
                            {
                                Visit(element);
                            }
                        }
                    }

                    break;
            }
        }

        /// <summary>
        /// Names the item's own source after its server. With <paramref name="addAlternates"/>, also returns the
        /// source list extended with one entry per other server that has the title: the details page builds its
        /// version dropdown from the item's own sources, and the plugin's extra sources otherwise only appear once
        /// playback starts, so the dropdown never showed the other copies.
        /// </summary>
        private List<MediaSourceInfo>? Rename(Guid itemId, IEnumerable<MediaSourceInfo>? sources, bool addAlternates)
        {
            var list = sources?.ToList();
            if (list == null || list.Count == 0)
            {
                return null;
            }

            var own = list.FirstOrDefault(s => Guid.TryParse(s.Id, out var id) && id == itemId);
            if (own == null)
            {
                return null;
            }

            var item = _libraryManager.GetItemById(itemId);
            var key = FederationLibraryManager.GetFederationKey(item);
            var entry = key == null ? null : _federation.Cache.GetEntryByKey(key);
            if (entry == null)
            {
                return null;
            }

            var primary = _federation.GetPlaybackSource(entry);
            var server = primary == null ? null : _federation.GetServer(primary.ServerId);
            if (server == null)
            {
                return null;
            }

            var all = entry.GetSourcesSnapshot();
            own.Name = FederationMediaSourceProvider.PrimarySourceName(server.Name, all.Length);
            if (!addAlternates || all.Length < 2)
            {
                return null;
            }

            var serverCounts = all.GroupBy(x => x.ServerId).ToDictionary(g => g.Key, g => g.Count());
            foreach (var source in all)
            {
                if (ReferenceEquals(source, primary))
                {
                    continue;
                }

                var other = _federation.GetServer(source.ServerId);
                if (other is not { Enabled: true })
                {
                    continue;
                }

                // A copy on a server that is down right now is still listed, so you can see where else the title
                // lives; it is marked so you know it will not play until that server is back.
                var offline = FederationItemPersistenceService.AvailabilityOverride?.IsOffline(source.ServerId) == true;
                var alternate = BuildAlternate(source, other.Name, entry.Metadata.RunTimeTicks, offline, serverCounts[source.ServerId] > 1 || source.ServerId == primary!.ServerId);
                if (list.All(existing => existing.Id != alternate.Id))
                {
                    list.Add(alternate);
                }
            }

            return list;
        }

        /// <summary>
        /// A description of another server's copy built from what the catalog already knows about it. Its id is the same
        /// one the playback path uses, so choosing it in the dropdown plays exactly that server's copy.
        /// </summary>
        internal static MediaSourceInfo BuildAlternate(FederatedSource source, string serverName, long? fallbackRuntime, bool offline = false, bool needsDetail = false)
            => new()
            {
                Id = FederationMediaSourceProvider.BuildSourceId(source),
                Name = AlternateName(source, serverName, offline, needsDetail),
                Protocol = MediaBrowser.Model.MediaInfo.MediaProtocol.Http,
                Type = MediaBrowser.Model.Dto.MediaSourceType.Grouping,
                Container = source.Container,
                Size = source.Size,
                Bitrate = source.Bitrate,
                RunTimeTicks = source.RunTimeTicks ?? fallbackRuntime,
                MediaStreams = source.MediaStreams?.ToList() ?? new List<MediaBrowser.Model.Entities.MediaStream>(),
                SupportsDirectPlay = true,
                SupportsDirectStream = true,
                SupportsTranscoding = true,
                SupportsProbing = true,
            };

        /// <summary>
        /// "server", "server (2160p · 54.3 GB)" when the same server has several copies (so they can be told apart),
        /// with ", offline" added while that server is down.
        /// </summary>
        internal static string AlternateName(FederatedSource source, string serverName, bool offline, bool needsDetail)
        {
            var parts = new List<string>();
            if (needsDetail)
            {
                var height = source.MediaStreams?.FirstOrDefault(m => m.Type == MediaBrowser.Model.Entities.MediaStreamType.Video)?.Height;
                if (height > 0)
                {
                    parts.Add(height + "p");
                }

                if (source.Size > 0)
                {
                    parts.Add((source.Size.Value / 1_073_741_824d).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " GB");
                }
            }

            var detail = string.Join(" · ", parts);
            if (offline)
            {
                detail = detail.Length > 0 ? detail + ", offline" : "offline";
            }

            return detail.Length > 0 ? $"{serverName} ({detail})" : serverName;
        }
    }
}
