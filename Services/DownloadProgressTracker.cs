using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Tracks "download to server" progress for real-time UI updates. Same shape as
    /// <see cref="SyncProgressTracker"/>, kept separate since downloads and syncs are
    /// unrelated operations with their own operation id namespace.
    /// </summary>
    public static class DownloadProgressTracker
    {
        // Speed is recomputed at most this often - the underlying byte-progress
        // callback fires on every ~80KB chunk (see RemoteServerClient.
        // DownloadToFileAsync), far too often to derive a stable rate from
        // consecutive samples; throttling smooths it into something a speed
        // readout can actually show without jitter.
        private static readonly TimeSpan SpeedSampleInterval = TimeSpan.FromSeconds(1);

        private static readonly ConcurrentDictionary<string, DownloadProgress> _progress = new();

        /// <summary>
        /// Starts tracking progress for a download operation.
        /// </summary>
        public static void Start(string operationId, string localItemId, string itemName)
        {
            Cleanup();
            _progress[operationId] = new DownloadProgress
            {
                OperationId = operationId,
                LocalItemId = localItemId,
                ItemName = itemName,
                Status = "Starting...",
                StartTime = DateTime.UtcNow
            };
        }

        /// <summary>
        /// Updates progress for a download operation from a byte count - the
        /// authoritative source, since percent/speed are both derived from it.
        /// </summary>
        public static void UpdateBytes(string operationId, long bytesDownloaded, long? totalBytes, string status)
        {
            if (!_progress.TryGetValue(operationId, out var progress))
            {
                return;
            }

            var now = DateTime.UtcNow;
            progress.BytesDownloaded = bytesDownloaded;
            progress.TotalBytes = totalBytes;
            progress.Status = status;
            progress.LastUpdate = now;
            if (totalBytes.HasValue && totalBytes.Value > 0)
            {
                progress.PercentComplete = Math.Min(100.0, bytesDownloaded * 100.0 / totalBytes.Value);
            }

            if (progress.LastSampleUtc == null)
            {
                progress.LastSampleUtc = now;
                progress.LastSampleBytes = bytesDownloaded;
                return;
            }

            var elapsed = now - progress.LastSampleUtc.Value;
            if (elapsed < SpeedSampleInterval)
            {
                return;
            }

            progress.BytesPerSecond = (bytesDownloaded - progress.LastSampleBytes) / elapsed.TotalSeconds;
            progress.LastSampleUtc = now;
            progress.LastSampleBytes = bytesDownloaded;
        }

        /// <summary>
        /// Updates progress for a download operation with just a status message
        /// (e.g. "Starting..."), leaving byte/percent/speed fields untouched.
        /// </summary>
        public static void Update(string operationId, string status)
        {
            if (_progress.TryGetValue(operationId, out var progress))
            {
                progress.Status = status;
                progress.LastUpdate = DateTime.UtcNow;
            }
        }

        /// <summary>
        /// Completes a download operation.
        /// </summary>
        public static void Complete(string operationId, bool success, string message)
        {
            if (_progress.TryGetValue(operationId, out var progress))
            {
                progress.Status = message;
                progress.IsComplete = true;
                progress.Success = success;
                progress.EndTime = DateTime.UtcNow;
                progress.BytesPerSecond = null;
                if (success)
                {
                    progress.PercentComplete = 100;
                }
            }
        }

        /// <summary>
        /// Gets progress for a download operation.
        /// </summary>
        public static DownloadProgress? Get(string operationId)
        {
            return _progress.TryGetValue(operationId, out var progress) ? progress : null;
        }

        /// <summary>
        /// Lists every tracked download (in progress or recently finished, see
        /// <see cref="Cleanup"/>), newest first - backs the dashboard's Downloads
        /// section.
        /// </summary>
        public static IReadOnlyList<DownloadProgress> GetAll()
        {
            return _progress.Values.OrderByDescending(p => p.StartTime).ToList();
        }

        /// <summary>
        /// True when a download for this local item id is already in flight - used to
        /// reject a duplicate concurrent request for the same item.
        /// </summary>
        public static bool IsDownloadingItem(string localItemId)
        {
            foreach (var kvp in _progress)
            {
                if (!kvp.Value.IsComplete && string.Equals(kvp.Value.LocalItemId, localItemId, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Clears old completed operations (older than 1 hour).
        /// </summary>
        public static void Cleanup()
        {
            var cutoff = DateTime.UtcNow.AddHours(-1);
            foreach (var kvp in _progress)
            {
                if (kvp.Value.IsComplete && kvp.Value.EndTime < cutoff)
                {
                    _progress.TryRemove(kvp.Key, out _);
                }
            }
        }
    }

    /// <summary>
    /// Represents download-to-server operation progress.
    /// </summary>
    public class DownloadProgress
    {
        public string OperationId { get; set; } = string.Empty;

        public string? LocalItemId { get; set; }

        public string ItemName { get; set; } = string.Empty;

        public double PercentComplete { get; set; }

        public string Status { get; set; } = string.Empty;

        public bool IsComplete { get; set; }

        public bool Success { get; set; }

        public DateTime StartTime { get; set; }

        public DateTime? LastUpdate { get; set; }

        public DateTime? EndTime { get; set; }

        public long BytesDownloaded { get; set; }

        public long? TotalBytes { get; set; }

        /// <summary>
        /// Recent transfer rate in bytes/second, recomputed at most once per
        /// second. Null before the first sample window closes, and cleared on
        /// completion.
        /// </summary>
        public double? BytesPerSecond { get; set; }

        // Bookkeeping for the throttled speed sample above - not meant to be
        // read by anything outside DownloadProgressTracker.
        internal DateTime? LastSampleUtc { get; set; }

        internal long LastSampleBytes { get; set; }
    }
}
