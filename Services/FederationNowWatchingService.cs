using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Session;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Reports which active Jellyfin sessions are currently playing a federated item -
    /// backs the "Now watching" indicator on the dashboard. Reads live off
    /// <see cref="ISessionManager.Sessions"/> on every call rather than tracking its own
    /// state, since Jellyfin already maintains that list and playback start/stop can
    /// happen well outside anything this plugin controls.
    /// </summary>
    public class FederationNowWatchingService
    {
        private readonly ISessionManager _sessionManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="FederationNowWatchingService"/> class.
        /// </summary>
        public FederationNowWatchingService(ISessionManager sessionManager)
        {
            _sessionManager = sessionManager;
        }

        /// <summary>
        /// Lists every session currently playing an item that was federated in from a
        /// friend's server. A session's <c>NowPlayingItem</c> is only populated while
        /// playback is actually active, so this is naturally empty most of the time.
        /// </summary>
        public IReadOnlyList<NowWatchingEntry> GetNowWatching()
        {
            var result = new List<NowWatchingEntry>();

            foreach (var session in _sessionManager.Sessions)
            {
                var item = session.NowPlayingItem;
                if (item?.ProviderIds == null
                    || !item.ProviderIds.TryGetValue("FederationKey", out var key)
                    || string.IsNullOrEmpty(key))
                {
                    continue;
                }

                var itemName = item.SeriesName != null
                    ? item.SeriesName + " - " + item.Name
                    : item.Name ?? "Unknown item";

                result.Add(new NowWatchingEntry
                {
                    SessionId = session.Id,
                    UserName = session.UserName ?? "Unknown user",
                    ItemName = itemName,
                    ServerName = FederationLibraryManager.GetServerNameFromTags(item.Tags) ?? "Unknown server",
                    DeviceName = session.DeviceName,
                    Client = session.Client,
                    IsPaused = session.PlayState?.IsPaused ?? false,
                    PlayMethod = session.PlayState?.PlayMethod?.ToString(),
                    PositionTicks = session.PlayState?.PositionTicks,
                    RuntimeTicks = item.RunTimeTicks
                });
            }

            return result;
        }
    }

    /// <summary>
    /// One session currently playing a federated item - see
    /// <see cref="FederationNowWatchingService.GetNowWatching"/>.
    /// </summary>
    public class NowWatchingEntry
    {
        public string SessionId { get; set; } = string.Empty;

        public string UserName { get; set; } = string.Empty;

        public string ItemName { get; set; } = string.Empty;

        public string ServerName { get; set; } = string.Empty;

        public string? DeviceName { get; set; }

        public string? Client { get; set; }

        public bool IsPaused { get; set; }

        public string? PlayMethod { get; set; }

        public long? PositionTicks { get; set; }

        public long? RuntimeTicks { get; set; }
    }
}
