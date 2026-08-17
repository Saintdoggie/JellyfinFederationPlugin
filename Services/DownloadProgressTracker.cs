using System;
using System.Collections.Concurrent;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Tracks "download to server" progress for real-time UI updates. Same shape as
    /// <see cref="SyncProgressTracker"/>, kept separate since downloads and syncs are
    /// unrelated operations with their own operation id namespace.
    /// </summary>
    public static class DownloadProgressTracker
    {
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
        /// Updates progress for a download operation.
        /// </summary>
        public static void Update(string operationId, double percentComplete, string status)
        {
            if (_progress.TryGetValue(operationId, out var progress))
            {
                progress.PercentComplete = percentComplete;
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
    }
}
