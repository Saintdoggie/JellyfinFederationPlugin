using System.Text.Json;

namespace FederationCompanion;

/// <summary>
/// Records this Companion's own uptime into 168 UTC hour-of-week buckets and
/// exchanges that self-blob with a token-authenticated friend. Observations of
/// other servers are never encoded as ours. Persisted beside
/// <c>companion-state.json</c>, not inside it.
/// </summary>
public sealed class AvailabilityScheduleService : BackgroundService
{
    internal const string FileName = "companion-availability.json";
    /// <summary>Histogram is hour-of-week, so one wake per hour is enough.</summary>
    internal static readonly TimeSpan TickInterval = TimeSpan.FromHours(1);
    internal static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);
    internal static readonly TimeSpan PlexPingTimeout = TimeSpan.FromSeconds(3);

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    private readonly CompanionState _state;
    private readonly HttpClient _http;
    private readonly ILogger<AvailabilityScheduleService> _logger;
    private readonly string _filePath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private byte[] _online = new byte[AvailabilitySchedule.BucketCount];
    private byte[] _total = new byte[AvailabilitySchedule.BucketCount];
    private readonly Dictionary<string, AvailabilityScheduleBlob> _peers = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _utc = DateTime.UtcNow;
    private DateTime? _lastHourTickUtc;

    public AvailabilityScheduleService(CompanionState state, HttpClient http, ILogger<AvailabilityScheduleService> logger)
        : this(state, http, logger, Path.Combine(AppContext.BaseDirectory, FileName))
    {
    }

    internal AvailabilityScheduleService(
        CompanionState state,
        HttpClient http,
        ILogger<AvailabilityScheduleService> logger,
        string filePath)
    {
        _state = state;
        _http = http;
        _logger = logger;
        _filePath = filePath;
        Load();
    }

    /// <summary>
    /// When Plex is configured, friends consume Plex through this process, so
    /// Plex reachability is the self-signal. Otherwise Companion being alive
    /// (this tick running) is enough.
    /// </summary>
    internal static bool ChooseSelfOnline(bool plexConfigured, bool plexReachable)
        => plexConfigured ? plexReachable : true;

    internal static bool PlexIsConfigured(CompanionState state)
        => !string.IsNullOrWhiteSpace(state.ServerBaseUrl)
            && !string.IsNullOrWhiteSpace(state.ServerAccessToken);

    public AvailabilityScheduleBlob GetSelfBlob()
    {
        _gate.Wait();
        try
        {
            return EncodeSelfUnlocked();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AvailabilityScheduleBlob> ExchangeAsync(
        AvailabilityScheduleBlob? incoming,
        string peerId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (AvailabilitySchedule.IsSelfBlob(incoming) && !string.IsNullOrWhiteSpace(peerId))
            {
                _peers[peerId] = CopyBlob(incoming!);
                await SaveUnlockedAsync(cancellationToken).ConfigureAwait(false);
            }

            return EncodeSelfUnlocked();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task TickAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var hour = FloorHour(now);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_lastHourTickUtc == hour)
            {
                return;
            }
        }
        finally
        {
            _gate.Release();
        }

        var plexConfigured = PlexIsConfigured(_state);
        var plexReachable = false;
        if (plexConfigured)
        {
            plexReachable = await PingLocalPlexAsync(
                _state.ServerBaseUrl!,
                _state.ServerAccessToken!,
                cancellationToken).ConfigureAwait(false);
        }

        var online = ChooseSelfOnline(plexConfigured, plexReachable);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_lastHourTickUtc == hour)
            {
                return;
            }

            AvailabilitySchedule.Increment(_online, _total, AvailabilitySchedule.BucketIndex(now), online);
            _utc = now;
            _lastHourTickUtc = hour;
            await SaveUnlockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal AvailabilityScheduleBlob? GetReceived(string peerId)
    {
        _gate.Wait();
        try
        {
            return _peers.TryGetValue(peerId, out var blob) ? blob : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        using var timer = new PeriodicTimer(TickInterval);
        do
        {
            try
            {
                await TickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[Companion] Availability tick failed");
            }
        }
        while (!stoppingToken.IsCancellationRequested
            && await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    private static DateTime FloorHour(DateTime utc)
    {
        var u = utc.ToUniversalTime();
        return new DateTime(u.Year, u.Month, u.Day, u.Hour, 0, 0, DateTimeKind.Utc);
    }

    internal async Task<bool> PingLocalPlexAsync(string baseUrl, string token, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(PlexPingTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl.TrimEnd('/')}/identity");
            request.Headers.TryAddWithoutValidation("X-Plex-Token", token);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or UriFormatException or InvalidOperationException)
        {
            return false;
        }
    }

    private AvailabilityScheduleBlob EncodeSelfUnlocked()
        => AvailabilitySchedule.EncodeSelf(_state.ClientIdentifier, _online, _total, DateTime.UtcNow);

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return;
            }

            PrivateFile.Restrict(_filePath);
            var loaded = JsonSerializer.Deserialize<AvailabilityScheduleFile>(File.ReadAllText(_filePath));
            if (loaded?.Online is { Length: AvailabilitySchedule.BucketCount } online
                && loaded.Total is { Length: AvailabilitySchedule.BucketCount } total)
            {
                _online = online;
                _total = total;
                _utc = loaded.Utc.Kind == DateTimeKind.Utc ? loaded.Utc : loaded.Utc.ToUniversalTime();
                _lastHourTickUtc = FloorHour(_utc);
            }

            if (loaded?.Peers != null)
            {
                foreach (var pair in loaded.Peers)
                {
                    if (!string.IsNullOrWhiteSpace(pair.Key) && AvailabilitySchedule.IsSelfBlob(pair.Value))
                    {
                        _peers[pair.Key] = CopyBlob(pair.Value);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or FormatException)
        {
            _online = new byte[AvailabilitySchedule.BucketCount];
            _total = new byte[AvailabilitySchedule.BucketCount];
            _peers.Clear();
        }
    }

    private async Task SaveUnlockedAsync(CancellationToken cancellationToken)
    {
        var dto = new AvailabilityScheduleFile
        {
            Id = _state.ClientIdentifier,
            Utc = _utc,
            Online = (byte[])_online.Clone(),
            Total = (byte[])_total.Clone(),
            Peers = _peers.ToDictionary(p => p.Key, p => CopyBlob(p.Value), StringComparer.OrdinalIgnoreCase)
        };

        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = _filePath + ".tmp";
        await using (var stream = PrivateFile.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, dto, JsonOpts, cancellationToken).ConfigureAwait(false);
        }

        PrivateFile.Restrict(tempPath);
        File.Move(tempPath, _filePath, overwrite: true);
    }

    private static AvailabilityScheduleBlob CopyBlob(AvailabilityScheduleBlob blob)
        => new()
        {
            Version = blob.Version,
            Id = blob.Id ?? string.Empty,
            Utc = blob.Utc,
            SampleHours = blob.SampleHours,
            Probabilities = blob.Probabilities ?? string.Empty
        };

    private sealed class AvailabilityScheduleFile
    {
        public string Id { get; set; } = string.Empty;

        public DateTime Utc { get; set; }

        public byte[]? Online { get; set; }

        public byte[]? Total { get; set; }

        public Dictionary<string, AvailabilityScheduleBlob>? Peers { get; set; }
    }
}
