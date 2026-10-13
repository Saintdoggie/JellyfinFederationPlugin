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
                    Rename(dto.Id, dto.MediaSources);
                    break;
                case PlaybackInfoResponse playback:
                    // The response does not carry the item id; the item's own source's Id *is* the item id.
                    foreach (var source in playback.MediaSources ?? new List<MediaSourceInfo>())
                    {
                        if (Guid.TryParse(source.Id, out var id))
                        {
                            Rename(id, playback.MediaSources);
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

        private void Rename(Guid itemId, IEnumerable<MediaSourceInfo>? sources)
        {
            var list = sources?.ToList();
            if (list == null || list.Count == 0)
            {
                return;
            }

            var own = list.FirstOrDefault(s => Guid.TryParse(s.Id, out var id) && id == itemId);
            if (own == null)
            {
                return;
            }

            var item = _libraryManager.GetItemById(itemId);
            var key = FederationLibraryManager.GetFederationKey(item);
            var entry = key == null ? null : _federation.Cache.GetEntryByKey(key);
            if (entry == null)
            {
                return;
            }

            var primary = _federation.GetPlaybackSource(entry);
            var server = primary == null ? null : _federation.GetServer(primary.ServerId);
            if (server == null)
            {
                return;
            }

            own.Name = FederationMediaSourceProvider.PrimarySourceName(server.Name, entry.GetSourcesSnapshot().Length);
        }
    }
}
