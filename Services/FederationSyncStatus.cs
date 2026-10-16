using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Per friend library sync state for the settings page: what a sync is doing
    /// right now, how much it found to download before downloading anything, and
    /// how the last attempt ended. The last outcome is kept on disk so a restart
    /// does not reset every library to "never synced".
    /// </summary>
    public sealed class FederationSyncStatus
    {
        public const string Waiting = "waiting";
        public const string Checking = "checking";
        public const string Ready = "ready";
        public const string Downloading = "downloading";
        public const string Saving = "saving";
        public const string Done = "done";
        public const string Failed = "failed";

        private static readonly JsonSerializerOptions FileJson = new() { WriteIndented = false };

        private readonly object _sync = new();
        private readonly ConcurrentDictionary<string, LibrarySyncState> _libraries = new(StringComparer.OrdinalIgnoreCase);
        private string? _filePath;
        private bool _pending;

        public bool Running { get; private set; }

        public DateTime? StartedUtc { get; private set; }

        public DateTime? FinishedUtc { get; private set; }

        public static string KeyFor(string mappingName, string serverId, string remoteLibraryId)
            => $"{mappingName}|{serverId}|{remoteLibraryId}";

        /// <summary>Loads the last outcomes saved next to the item cache.</summary>
        public void Initialize(string cacheFilePath)
        {
            var dir = Path.GetDirectoryName(cacheFilePath);
            _filePath = string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, "federation-sync-status.json");
            if (_filePath == null || !File.Exists(_filePath))
            {
                return;
            }

            try
            {
                var saved = JsonSerializer.Deserialize<List<LibrarySyncState>>(File.ReadAllText(_filePath), FileJson);
                foreach (var state in saved ?? new List<LibrarySyncState>())
                {
                    if (!string.IsNullOrEmpty(state.Key))
                    {
                        // A sync interrupted by a restart is not still running.
                        if (state.State is not (Done or Failed))
                        {
                            state.State = state.LastSuccessUtc.HasValue ? Done : Waiting;
                        }

                        _libraries[state.Key] = state;
                    }
                }
            }
            catch (Exception)
            {
                // A damaged status file only costs the "last synced" history.
            }
        }

        /// <summary>
        /// Reports a requested sync as running before its background task has
        /// begun, so the page switches to fast polling straight away.
        /// </summary>
        public void MarkPending()
        {
            lock (_sync)
            {
                _pending = true;
                Running = true;
                StartedUtc = DateTime.UtcNow;
            }
        }

        /// <summary>Undoes <see cref="MarkPending"/> when the requested sync never began.</summary>
        public void ClearPending()
        {
            lock (_sync)
            {
                if (_pending)
                {
                    _pending = false;
                    Running = false;
                }
            }
        }

        /// <summary>
        /// Starts a run over <paramref name="sources"/>: each becomes waiting, keeps
        /// its last outcome, and libraries no longer configured are dropped.
        /// </summary>
        public void BeginRun(IEnumerable<LibrarySyncState> sources, bool replaceAll)
        {
            lock (_sync)
            {
                var list = sources.ToList();
                if (replaceAll)
                {
                    foreach (var key in _libraries.Keys.Except(list.Select(s => s.Key), StringComparer.OrdinalIgnoreCase).ToList())
                    {
                        _libraries.TryRemove(key, out _);
                    }
                }

                foreach (var source in list)
                {
                    var state = _libraries.GetOrAdd(source.Key, _ => source);
                    state.ServerId = source.ServerId;
                    state.ServerName = source.ServerName;
                    state.RemoteLibraryId = source.RemoteLibraryId;
                    state.RemoteLibraryName = source.RemoteLibraryName;
                    state.LocalLibraryName = source.LocalLibraryName;
                    state.State = Waiting;
                    state.Mode = null;
                    state.Total = null;
                    state.ToDownload = 0;
                    state.Downloaded = 0;
                    state.ToRemove = 0;
                    state.BytesReceived = 0;
                    state.Removed = 0;
                    state.WaitingRemovals = 0;
                    state.Error = null;
                }

                _pending = false;
                Running = true;
                StartedUtc = DateTime.UtcNow;
            }
        }

        public void Update(string key, Action<LibrarySyncState> change)
        {
            if (_libraries.TryGetValue(key, out var state))
            {
                lock (_sync)
                {
                    change(state);
                }
            }
        }

        /// <summary>Marks a library failed with a reason an admin can act on.</summary>
        public void Fail(string key, string reason)
            => Update(key, s =>
            {
                s.State = Failed;
                s.Error = reason;
                s.LastAttemptUtc = DateTime.UtcNow;
            });

        public void Succeed(string key)
            => Update(key, s =>
            {
                s.State = Done;
                s.Error = null;
                s.LastAttemptUtc = DateTime.UtcNow;
                s.LastSuccessUtc = s.LastAttemptUtc;
                s.LastTotal = s.Total ?? s.Downloaded;
            });

        /// <summary>Ends the run; anything still in progress did not finish.</summary>
        public void EndRun()
        {
            lock (_sync)
            {
                foreach (var state in _libraries.Values.Where(s => s.State is not (Done or Failed)))
                {
                    state.State = Failed;
                    state.Error ??= "The sync stopped before this library was finished.";
                    state.LastAttemptUtc = DateTime.UtcNow;
                }

                Running = false;
                FinishedUtc = DateTime.UtcNow;
            }

            Save();
        }

        public IReadOnlyList<LibrarySyncState> Snapshot()
        {
            lock (_sync)
            {
                return _libraries.Values
                    .Select(s => s.Clone())
                    .OrderBy(s => s.ServerName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(s => s.RemoteLibraryName, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }

        private void Save()
        {
            if (string.IsNullOrEmpty(_filePath))
            {
                return;
            }

            try
            {
                var tempPath = _filePath + ".tmp";
                File.WriteAllText(tempPath, JsonSerializer.Serialize(Snapshot(), FileJson));
                File.Move(tempPath, _filePath, true);
            }
            catch (Exception)
            {
                // Status history is best effort; the sync itself already finished.
            }
        }
    }

    /// <summary>One friend library feeding one local library.</summary>
    public sealed class LibrarySyncState
    {
        public string Key { get; set; } = string.Empty;

        public string ServerId { get; set; } = string.Empty;

        public string ServerName { get; set; } = string.Empty;

        public string RemoteLibraryId { get; set; } = string.Empty;

        public string RemoteLibraryName { get; set; } = string.Empty;

        public string LocalLibraryName { get; set; } = string.Empty;

        /// <summary>waiting, checking, ready (checked, queued for download), downloading, saving, done or failed.</summary>
        public string State { get; set; } = FederationSyncStatus.Waiting;

        /// <summary>
        /// changes: only new or changed titles; full: every title (first sync,
        /// changed settings or the daily refresh); legacy: an older friend that
        /// cannot list its titles first; external: a Plex source.
        /// </summary>
        public string? Mode { get; set; }

        /// <summary>Titles the friend shares in this library, when it said so up front.</summary>
        public int? Total { get; set; }

        public int ToDownload { get; set; }

        public int Downloaded { get; set; }

        public int ToRemove { get; set; }

        public long BytesReceived { get; set; }

        /// <summary>Entries this sync actually removed.</summary>
        public int Removed { get; set; }

        /// <summary>Titles missing for the first time; removed at the next sync if still missing.</summary>
        public int WaitingRemovals { get; set; }

        /// <summary>A large removal waiting for the admin to confirm it; kept across syncs until then.</summary>
        public int HeldRemovals { get; set; }

        public string? Error { get; set; }

        public DateTime? LastAttemptUtc { get; set; }

        public DateTime? LastSuccessUtc { get; set; }

        public int? LastTotal { get; set; }

        public LibrarySyncState Clone() => (LibrarySyncState)MemberwiseClone();
    }
}
