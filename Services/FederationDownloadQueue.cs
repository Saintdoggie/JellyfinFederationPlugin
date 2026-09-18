using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// One in-flight "download to this server" job, persisted so a Jellyfin
    /// restart can restore progress and resume from the partial file.
    /// </summary>
    internal sealed class DownloadJob
    {
        public const string KindFederated = "federated";
        public const string KindBrowse = "browse";
        public const string KindQualityReplace = "quality-replace";
        public const string StateRunning = "running";
        public const string StatePaused = "paused";

        public string OperationId { get; set; } = string.Empty;

        public string Kind { get; set; } = KindFederated;

        public string State { get; set; } = StateRunning;

        public string DedupeKey { get; set; } = string.Empty;

        public string ItemName { get; set; } = string.Empty;

        public string? ServerId { get; set; }

        public string? RemoteItemId { get; set; }

        public string? FederationKey { get; set; }

        public string? LocalItemId { get; set; }

        public string? DestinationPath { get; set; }

        public string? PartialPath { get; set; }

        public bool Bulk { get; set; }

        public DateTime StartedUtc { get; set; } = DateTime.UtcNow;

        public long BytesDownloaded { get; set; }

        public long? TotalBytes { get; set; }

        public string? PauseReason { get; set; }

        [JsonIgnore]
        public DateTime LastPersistUtc { get; set; }
    }

    /// <summary>
    /// JSON queue next to the plugin data folder. Atomic replace so a crash
    /// mid-write cannot leave a truncated file as the only copy.
    /// </summary>
    internal sealed class FederationDownloadQueue
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private readonly object _sync = new();

        internal static string QueuePath()
        {
            var dataPath = Plugin.Instance?.DataFolderPath;
            return string.IsNullOrEmpty(dataPath)
                ? string.Empty
                : Path.Combine(dataPath, "federation-download-queue.json");
        }

        public IReadOnlyList<DownloadJob> Load()
        {
            lock (_sync)
            {
                return LoadUnlocked();
            }
        }

        public DownloadJob? Get(string operationId)
        {
            if (string.IsNullOrEmpty(operationId))
            {
                return null;
            }

            lock (_sync)
            {
                return LoadUnlocked().FirstOrDefault(j =>
                    string.Equals(j.OperationId, operationId, StringComparison.OrdinalIgnoreCase));
            }
        }

        public DownloadJob? FindByDedupeKey(string dedupeKey)
        {
            if (string.IsNullOrEmpty(dedupeKey))
            {
                return null;
            }

            lock (_sync)
            {
                return LoadUnlocked().FirstOrDefault(j =>
                    string.Equals(j.DedupeKey, dedupeKey, StringComparison.OrdinalIgnoreCase));
            }
        }

        public void Upsert(DownloadJob job)
        {
            if (job == null || string.IsNullOrEmpty(job.OperationId))
            {
                return;
            }

            lock (_sync)
            {
                var all = LoadUnlocked();
                all.RemoveAll(j => string.Equals(j.OperationId, job.OperationId, StringComparison.OrdinalIgnoreCase));
                all.Add(job);
                SaveUnlocked(all);
            }
        }

        public void Remove(string operationId)
        {
            if (string.IsNullOrEmpty(operationId))
            {
                return;
            }

            lock (_sync)
            {
                var all = LoadUnlocked();
                var next = all.Where(j =>
                    !string.Equals(j.OperationId, operationId, StringComparison.OrdinalIgnoreCase)).ToList();
                if (next.Count == all.Count)
                {
                    return;
                }

                SaveUnlocked(next);
            }
        }

        private static List<DownloadJob> LoadUnlocked()
        {
            var path = QueuePath();
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return new List<DownloadJob>();
            }

            try
            {
                var json = File.ReadAllText(path);
                var jobs = JsonSerializer.Deserialize<List<DownloadJob>>(json, JsonOptions);
                return jobs ?? new List<DownloadJob>();
            }
            catch (Exception)
            {
                return new List<DownloadJob>();
            }
        }

        private static void SaveUnlocked(List<DownloadJob> jobs)
        {
            var path = QueuePath();
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(jobs, JsonOptions));
            File.Move(tmp, path, overwrite: true);
        }
    }
}
