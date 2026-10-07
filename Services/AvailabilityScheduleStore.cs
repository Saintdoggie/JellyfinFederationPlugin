using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Federation.Services
{
    public sealed class FriendAvailabilityRecord
    {
        public byte[] Online { get; set; } = new byte[AvailabilitySchedule.BucketCount];

        public byte[] Total { get; set; } = new byte[AvailabilitySchedule.BucketCount];

        public string? FederationId { get; set; }

        public DateTime? LastShareUtc { get; set; }

        public DateTime? LastHourTickUtc { get; set; }

        public ServerReachability LastReachability { get; set; }

        public DateTime LastCheckedUtc { get; set; }

        public DateTime? LastOnlineUtc { get; set; }

        public AvailabilityScheduleBlob? ReceivedSelf { get; set; }
    }

    public sealed class AvailabilityScheduleState
    {
        public int Version { get; set; } = 1;

        public string? SelfFederationId { get; set; }

        public byte[] SelfOnline { get; set; } = new byte[AvailabilitySchedule.BucketCount];

        public byte[] SelfTotal { get; set; } = new byte[AvailabilitySchedule.BucketCount];

        public DateTime? SelfHourTickUtc { get; set; }

        public Dictionary<string, FriendAvailabilityRecord> Friends { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Sidecar JSON for hour-of-week histograms. Kept out of plugin XML so an
    /// admin save cannot wipe 168-bucket arrays.
    /// </summary>
    public sealed class AvailabilityScheduleStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private readonly object _sync = new();
        private AvailabilityScheduleState _state = new();
        private string _path = string.Empty;
        private DateTime _lastWriteUtc = DateTime.MinValue;

        public void Initialize(string path)
        {
            _path = path ?? string.Empty;
            if (string.IsNullOrEmpty(_path) || !File.Exists(_path))
            {
                _state = new AvailabilityScheduleState();
                return;
            }

            try
            {
                var loaded = JsonSerializer.Deserialize<AvailabilityScheduleState>(File.ReadAllText(_path), JsonOptions);
                _state = loaded ?? new AvailabilityScheduleState();
                Normalize(_state);
            }
            catch (Exception)
            {
                _state = new AvailabilityScheduleState();
            }
        }

        public AvailabilityScheduleState Snapshot()
        {
            lock (_sync)
            {
                return _state;
            }
        }

        public FriendAvailabilityRecord Friend(string serverId)
        {
            lock (_sync)
            {
                if (!_state.Friends.TryGetValue(serverId, out var row))
                {
                    row = new FriendAvailabilityRecord();
                    _state.Friends[serverId] = row;
                }

                return row;
            }
        }

        public void RecordObservation(string serverId, bool online, DateTime utc)
        {
            lock (_sync)
            {
                var row = FriendUnlocked(serverId);
                var hour = FloorHour(utc);
                if (row.LastHourTickUtc == hour)
                {
                    return;
                }

                row.LastHourTickUtc = hour;
                AvailabilitySchedule.Increment(row.Online, row.Total, AvailabilitySchedule.BucketIndex(utc), online);
            }
        }

        public void RecordSelf(DateTime utc)
        {
            lock (_sync)
            {
                var hour = FloorHour(utc);
                if (_state.SelfHourTickUtc == hour)
                {
                    return;
                }

                _state.SelfHourTickUtc = hour;
                AvailabilitySchedule.Increment(_state.SelfOnline, _state.SelfTotal, AvailabilitySchedule.BucketIndex(utc), true);
            }
        }

        public void RememberReachability(string serverId, ServerAvailability state)
        {
            lock (_sync)
            {
                var row = FriendUnlocked(serverId);
                row.LastReachability = state.Reachability;
                row.LastCheckedUtc = state.LastCheckedUtc;
                row.LastOnlineUtc = state.LastOnlineUtc;
            }
        }

        public void AcceptReceivedSelf(string serverId, string? federationId, AvailabilityScheduleBlob blob)
        {
            lock (_sync)
            {
                var row = FriendUnlocked(serverId);
                row.FederationId = federationId ?? row.FederationId;
                row.ReceivedSelf = blob;
            }
        }

        public void MarkShared(string serverId, DateTime utc)
        {
            lock (_sync)
            {
                FriendUnlocked(serverId).LastShareUtc = utc;
            }
        }

        public void Forget(string serverId)
        {
            lock (_sync)
            {
                _state.Friends.Remove(serverId);
            }
        }

        public AvailabilityScheduleBlob EncodeSelf(string federationId, DateTime utc)
        {
            lock (_sync)
            {
                _state.SelfFederationId = federationId;
                return AvailabilitySchedule.EncodeSelf(federationId, _state.SelfOnline, _state.SelfTotal, utc);
            }
        }

        public (AvailabilityForecast Forecast, int Confidence) Forecast(string serverId, DateTime utc)
        {
            lock (_sync)
            {
                if (!_state.Friends.TryGetValue(serverId, out var row))
                {
                    return (AvailabilityForecast.Unknown, 0);
                }

                var prior = AvailabilitySchedule.DecodeFractions(row.ReceivedSelf);
                return AvailabilitySchedule.Forecast(
                    row.Online,
                    row.Total,
                    prior,
                    row.ReceivedSelf?.SampleHours ?? 0,
                    utc);
            }
        }

        public void SaveIfDue(bool force = false)
        {
            if (string.IsNullOrEmpty(_path))
            {
                return;
            }

            lock (_sync)
            {
                if (!force && DateTime.UtcNow - _lastWriteUtc < TimeSpan.FromHours(1))
                {
                    return;
                }

                var directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_state, JsonOptions));
                File.Move(tmp, _path, overwrite: true);
                _lastWriteUtc = DateTime.UtcNow;
            }
        }

        private FriendAvailabilityRecord FriendUnlocked(string serverId)
        {
            if (!_state.Friends.TryGetValue(serverId, out var row))
            {
                row = new FriendAvailabilityRecord();
                _state.Friends[serverId] = row;
            }

            return row;
        }

        private static DateTime FloorHour(DateTime utc)
        {
            var u = utc.ToUniversalTime();
            return new DateTime(u.Year, u.Month, u.Day, u.Hour, 0, 0, DateTimeKind.Utc);
        }

        private static void Normalize(AvailabilityScheduleState state)
        {
            state.SelfOnline = Fit(state.SelfOnline);
            state.SelfTotal = Fit(state.SelfTotal);
            foreach (var row in state.Friends.Values)
            {
                row.Online = Fit(row.Online);
                row.Total = Fit(row.Total);
            }
        }

        private static byte[] Fit(byte[]? values)
        {
            var next = new byte[AvailabilitySchedule.BucketCount];
            if (values == null || values.Length == 0)
            {
                return next;
            }

            Array.Copy(values, next, Math.Min(values.Length, next.Length));
            return next;
        }
    }
}
