using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Runs the support report (<see cref="PlaySupportReport"/>) in the background:
    /// every movie and episode this server holds, plus every friend's copy in the
    /// catalog. One run at a time; the last result is kept in memory until the
    /// next run or a restart. Reads only; nothing is changed or sent anywhere.
    /// </summary>
    public sealed class PlaySupportReportService
    {
        /// <summary>Most files looked at in one run.</summary>
        internal const int MaxFiles = 60_000;

        private readonly ILibraryManager _library;
        private readonly FederationLibraryManager _federationManager;
        private readonly ILogger<PlaySupportReportService> _logger;
        private readonly object _lock = new();
        private bool _running;
        private int _checked;
        private string? _error;
        private PlaySupportResult? _result;

        /// <summary>Initializes a new instance of the <see cref="PlaySupportReportService"/> class.</summary>
        public PlaySupportReportService(ILibraryManager library, FederationLibraryManager federationManager, ILogger<PlaySupportReportService> logger)
        {
            _library = library;
            _federationManager = federationManager;
            _logger = logger;
        }

        /// <summary>Test seam: an own file's tracks (Jellyfin reads them from its database).</summary>
        internal Func<BaseItem, IReadOnlyList<MediaStream>> OwnStreams { get; set; } = item => item.GetMediaStreams();

        /// <summary>Test seam: runs the scan (default: on the thread pool).</summary>
        internal Func<Action, Task> Run { get; set; } = work => Task.Run(work);

        /// <summary>Where the report stands: running, how far, the last result, the last error.</summary>
        internal (bool Running, int Checked, PlaySupportResult? Result, string? Error) State()
        {
            lock (_lock)
            {
                return (_running, _checked, _result, _error);
            }
        }

        /// <summary>Starts a run unless one is going. Returns false when one already was.</summary>
        internal bool Start()
        {
            lock (_lock)
            {
                if (_running)
                {
                    return false;
                }

                _running = true;
                _checked = 0;
                _error = null;
            }

            _ = Run(Scan);
            return true;
        }

        private void Scan()
        {
            try
            {
                var truncated = false;
                var files = new List<PlaySupportFile>();
                var own = _library.GetItemList(new InternalItemsQuery
                {
                    Recursive = true,
                    IsVirtualItem = false,
                    IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode }
                });
                foreach (var item in own)
                {
                    if (FederationLibraryManager.GetFederationKey(item) != null)
                    {
                        // A friend's title: counted below, once per copy.
                        continue;
                    }

                    if (files.Count >= MaxFiles)
                    {
                        truncated = true;
                        break;
                    }

                    files.Add(new PlaySupportFile(Title(item), "This server", item.Container, OwnStreams(item)));
                    Interlocked.Increment(ref _checked);
                }

                foreach (var entry in _federationManager.Cache.GetAllEntries())
                {
                    if (!string.Equals(entry.ItemType, "Movie", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(entry.ItemType, "Episode", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    foreach (var source in entry.GetSourcesSnapshot())
                    {
                        var server = _federationManager.GetServer(source.ServerId);
                        if (server == null || !server.Enabled || LinkCatalog.IsHiddenLinkedSource(server))
                        {
                            continue;
                        }

                        if (files.Count >= MaxFiles)
                        {
                            truncated = true;
                            break;
                        }

                        files.Add(new PlaySupportFile(
                            entry.Metadata.Name,
                            server.Name,
                            source.Container ?? entry.Metadata.Container,
                            source.MediaStreams ?? entry.Metadata.MediaStreams));
                        Interlocked.Increment(ref _checked);
                    }

                    if (truncated)
                    {
                        break;
                    }
                }

                var result = PlaySupportReport.Build(files, DateTime.UtcNow, truncated);
                lock (_lock)
                {
                    _result = result;
                }

                _logger.LogInformation("[Federation] Support report: {Count} files checked against {Players} kinds of player", result.FilesChecked, result.Players.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Federation] The support report could not be made");
                lock (_lock)
                {
                    _error = "The report could not be made: " + ex.GetType().Name;
                }
            }
            finally
            {
                lock (_lock)
                {
                    _running = false;
                }
            }
        }

        private static string Title(BaseItem item)
            => item is MediaBrowser.Controller.Entities.TV.Episode episode && !string.IsNullOrEmpty(episode.SeriesName)
                ? episode.SeriesName + " - " + item.Name
                : item.Name ?? "(untitled)";
    }
}
