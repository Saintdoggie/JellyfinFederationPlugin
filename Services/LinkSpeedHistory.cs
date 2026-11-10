using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>One timed download from a friend's server.</summary>
    public sealed class LinkSpeedSample
    {
        public DateTime Utc { get; set; }

        public double Mbps { get; set; }
    }

    /// <summary>Persisted shape of <see cref="LinkSpeedHistory"/>.</summary>
    public sealed class LinkSpeedState
    {
        public int Version { get; set; } = 1;

        public Dictionary<string, List<LinkSpeedSample>> Servers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Remembers how fast each friend's server really delivers, so copies are ranked
    /// on a day's worth of readings instead of one lucky or unlucky sample. A new
    /// friend is sampled every hour until a full day is covered; after that one
    /// sample a day keeps the average current. Kept in a sidecar file so a restart
    /// does not start the day over.
    /// </summary>
    public sealed class LinkSpeedHistory
    {
        /// <summary>Samples that make up "a day": one per hour.</summary>
        internal const int FullDaySamples = 24;

        /// <summary>Readings older than this describe a connection that may have changed.</summary>
        internal static readonly TimeSpan MaxSampleAge = TimeSpan.FromDays(14);

        internal static readonly TimeSpan LearningInterval = TimeSpan.FromHours(1);

        internal static readonly TimeSpan SettledInterval = TimeSpan.FromHours(24);

        private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

        private readonly object _sync = new();
        private LinkSpeedState _state = new();
        private string _path = string.Empty;

        /// <summary>Loads earlier readings; a missing or damaged file just starts empty.</summary>
        public void Initialize(string path)
        {
            lock (_sync)
            {
                _path = path ?? string.Empty;
                _state = new LinkSpeedState();
                if (string.IsNullOrEmpty(_path) || !File.Exists(_path) || new FileInfo(_path).Length > 1_000_000)
                {
                    return;
                }

                try
                {
                    var loaded = JsonSerializer.Deserialize<LinkSpeedState>(File.ReadAllText(_path), JsonOptions);
                    if (loaded?.Servers != null)
                    {
                        foreach (var pair in loaded.Servers)
                        {
                            var clean = (pair.Value ?? new List<LinkSpeedSample>())
                                .Where(s => s != null && Valid(s.Mbps))
                                .OrderBy(s => s.Utc)
                                .ToList();
                            if (clean.Count > 0)
                            {
                                _state.Servers[pair.Key] = Trim(clean);
                            }
                        }
                    }
                }
                catch (Exception)
                {
                    _state = new LinkSpeedState();
                }
            }
        }

        /// <summary>Adds a reading and saves. Nonsense values are ignored.</summary>
        public void Record(string serverId, double mbps, DateTime utc)
        {
            if (string.IsNullOrEmpty(serverId) || !Valid(mbps))
            {
                return;
            }

            lock (_sync)
            {
                if (!_state.Servers.TryGetValue(serverId, out var samples))
                {
                    samples = new List<LinkSpeedSample>();
                }

                samples.Add(new LinkSpeedSample { Utc = utc, Mbps = Math.Round(mbps, 2) });
                _state.Servers[serverId] = Trim(samples);
                Save();
            }
        }

        /// <summary>
        /// The average of the remembered readings that are still recent, or null when
        /// there are none.
        /// </summary>
        public double? AverageMbps(string serverId, DateTime utcNow)
        {
            lock (_sync)
            {
                return _state.Servers.TryGetValue(serverId, out var samples) ? Average(samples, utcNow) : null;
            }
        }

        /// <summary>How many recent readings back the average.</summary>
        public int SampleCount(string serverId, DateTime utcNow)
        {
            lock (_sync)
            {
                return _state.Servers.TryGetValue(serverId, out var samples) ? Recent(samples, utcNow).Count() : 0;
            }
        }

        /// <summary>How long to wait before sampling this server again.</summary>
        public TimeSpan NextDelay(string serverId, DateTime utcNow)
            => NextDelay(SampleCount(serverId, utcNow));

        /// <summary>Forgets a removed server.</summary>
        public void Remove(string serverId)
        {
            lock (_sync)
            {
                if (_state.Servers.Remove(serverId))
                {
                    Save();
                }
            }
        }

        /// <summary>Hourly until a day is covered, then daily.</summary>
        internal static TimeSpan NextDelay(int recentSamples)
            => recentSamples < FullDaySamples ? LearningInterval : SettledInterval;

        internal static double? Average(IEnumerable<LinkSpeedSample> samples, DateTime utcNow)
        {
            var recent = Recent(samples, utcNow).Select(s => s.Mbps).ToList();
            return recent.Count == 0 ? null : Math.Round(recent.Average(), 2);
        }

        private static IEnumerable<LinkSpeedSample> Recent(IEnumerable<LinkSpeedSample> samples, DateTime utcNow)
            => samples.Where(s => utcNow - s.Utc <= MaxSampleAge);

        private static List<LinkSpeedSample> Trim(List<LinkSpeedSample> samples)
            => samples.Count <= FullDaySamples ? samples : samples.GetRange(samples.Count - FullDaySamples, FullDaySamples);

        private static bool Valid(double mbps)
            => mbps > 0 && mbps < 100_000 && !double.IsNaN(mbps);

        // Caller holds the lock. A failed write only costs remembered readings.
        private void Save()
        {
            if (string.IsNullOrEmpty(_path))
            {
                return;
            }

            try
            {
                var temp = _path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(_state, JsonOptions));
                File.Move(temp, _path, true);
            }
            catch (Exception)
            {
            }
        }
    }
}
