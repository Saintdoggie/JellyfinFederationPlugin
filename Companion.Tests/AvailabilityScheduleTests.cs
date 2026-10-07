using System.Net;
using System.Text;
using System.Text.Json;
using FederationCompanion;
using Microsoft.Extensions.Logging.Abstractions;

namespace FederationCompanion.Tests;

public sealed class AvailabilityScheduleTests
{
    [Fact]
    public void BucketIndex_IsUtcHourOfWeek()
    {
        Assert.Equal(0, AvailabilitySchedule.BucketIndex(new DateTime(2026, 9, 13, 0, 0, 0, DateTimeKind.Utc)));
        Assert.Equal(87, AvailabilitySchedule.BucketIndex(new DateTime(2026, 9, 16, 15, 0, 0, DateTimeKind.Utc)));
        Assert.Equal(167, AvailabilitySchedule.BucketIndex(new DateTime(2026, 9, 19, 23, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void EncodeFraction_UnknownIs255_AndScalesTo254()
    {
        Assert.Equal(255, AvailabilitySchedule.EncodeFraction(0, 0));
        Assert.Equal(0, AvailabilitySchedule.EncodeFraction(0, 1));
        Assert.Equal(254, AvailabilitySchedule.EncodeFraction(1, 1));
        Assert.Equal(127, AvailabilitySchedule.EncodeFraction(1, 2));
    }

    [Fact]
    public void EncodeSelf_RoundTrips168Bytes_AndLeavesUnknownAs255()
    {
        var online = new byte[168];
        var total = new byte[168];
        online[3] = 1;
        total[3] = 1;
        total[4] = 2;

        var blob = AvailabilitySchedule.EncodeSelf("self-id", online, total, new DateTime(2026, 9, 18, 12, 0, 0, DateTimeKind.Utc));

        Assert.Equal(1, blob.Version);
        Assert.Equal("self-id", blob.Id);
        Assert.Equal(2, blob.SampleHours);
        var packed = Convert.FromBase64String(blob.Probabilities);
        Assert.Equal(168, packed.Length);
        Assert.Equal(254, packed[3]);
        Assert.Equal(0, packed[4]);
        Assert.Equal(255, packed[5]);

        var decoded = AvailabilitySchedule.DecodeFractions(blob);
        Assert.Equal(packed, decoded);
    }

    [Fact]
    public void DecodeFractions_UnknownVersion_IsAllUnknown()
    {
        var packed = new byte[168];
        packed[0] = 200;
        var blob = new AvailabilityScheduleBlob
        {
            Version = 2,
            Id = "other",
            Probabilities = Convert.ToBase64String(packed)
        };

        var decoded = AvailabilitySchedule.DecodeFractions(blob);
        Assert.All(decoded, b => Assert.Equal(255, b));
    }

    [Fact]
    public void Increment_SaturatesWithRightShiftDecay()
    {
        var online = new byte[168];
        var total = new byte[168];
        online[0] = 254;
        total[0] = 255;

        AvailabilitySchedule.Increment(online, total, 0, wasOnline: true);

        Assert.Equal(128, online[0]);
        Assert.Equal(128, total[0]);
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    public void ChooseSelfOnline_UsesPlexWhenConfigured(bool plexConfigured, bool plexReachable, bool expected)
        => Assert.Equal(expected, AvailabilityScheduleService.ChooseSelfOnline(plexConfigured, plexReachable));

    [Fact]
    public async Task Tick_WithoutPlex_RecordsCompanionUp()
    {
        var (service, path, handler) = Create(plexConfigured: false, plexStatus: HttpStatusCode.OK);
        try
        {
            await service.TickAsync(CancellationToken.None);

            Assert.Equal(0, handler.Calls);
            var blob = service.GetSelfBlob();
            Assert.Equal(1, blob.SampleHours);
            var packed = AvailabilitySchedule.DecodeFractions(blob);
            Assert.Equal(254, Assert.Single(packed, b => b != 255));
            Assert.DoesNotContain("plex-secret", await File.ReadAllTextAsync(path));
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task Tick_SameHour_DoesNotPingOrDoubleCount()
    {
        var (service, path, handler) = Create(plexConfigured: true, plexStatus: HttpStatusCode.OK);
        try
        {
            await service.TickAsync(CancellationToken.None);
            await service.TickAsync(CancellationToken.None);

            Assert.Equal(1, handler.Calls);
            Assert.Equal(1, service.GetSelfBlob().SampleHours);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task Tick_WithReachablePlex_RecordsOnline()
    {
        var (service, path, handler) = Create(plexConfigured: true, plexStatus: HttpStatusCode.OK);
        try
        {
            await service.TickAsync(CancellationToken.None);

            Assert.Equal(1, handler.Calls);
            Assert.EndsWith("/identity", handler.LastPath);
            var packed = AvailabilitySchedule.DecodeFractions(service.GetSelfBlob());
            Assert.Equal(254, Assert.Single(packed, b => b != 255));
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task Tick_WithUnreachablePlex_RecordsOfflineEvenIfCompanionIsUp()
    {
        var (service, path, _) = Create(plexConfigured: true, plexStatus: HttpStatusCode.ServiceUnavailable);
        try
        {
            await service.TickAsync(CancellationToken.None);

            var packed = AvailabilitySchedule.DecodeFractions(service.GetSelfBlob());
            Assert.Equal(0, Assert.Single(packed, b => b != 255));
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task Persistence_ReloadsHistogramFromSidecarFile()
    {
        var (service, path, _) = Create(plexConfigured: false, plexStatus: HttpStatusCode.OK);
        try
        {
            await service.TickAsync(CancellationToken.None);
            var first = service.GetSelfBlob().Probabilities;
            Assert.Equal(AvailabilityScheduleService.FileName, Path.GetFileName(path));

            var reloaded = new AvailabilityScheduleService(
                new CompanionState { ClientIdentifier = "companion-id" },
                new HttpClient(new RecordingHandler(HttpStatusCode.OK)),
                NullLogger<AvailabilityScheduleService>.Instance,
                path);
            Assert.Equal(first, reloaded.GetSelfBlob().Probabilities);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task Exchange_StoresCallerSelfBlob_AndReturnsOurs_IgnoringUnknownVersion()
    {
        var (service, path, _) = Create(plexConfigured: false, plexStatus: HttpStatusCode.OK);
        try
        {
            await service.TickAsync(CancellationToken.None);
            var caller = AvailabilitySchedule.EncodeSelf(
                "friend",
                Enumerable.Repeat((byte)1, 168).ToArray(),
                Enumerable.Repeat((byte)1, 168).ToArray(),
                DateTime.UtcNow);

            var returned = await service.ExchangeAsync(caller, "peer-1", CancellationToken.None);
            Assert.Equal(AvailabilitySchedule.Version, returned.Version);
            Assert.Equal("companion-id", returned.Id);
            Assert.Equal(168, Convert.FromBase64String(returned.Probabilities).Length);
            Assert.Equal("friend", service.GetReceived("peer-1")!.Id);

            var unknown = new AvailabilityScheduleBlob
            {
                Version = 9,
                Id = "future",
                Probabilities = caller.Probabilities
            };
            await service.ExchangeAsync(unknown, "peer-2", CancellationToken.None);
            Assert.Null(service.GetReceived("peer-2"));

            var json = JsonSerializer.Serialize(returned);
            Assert.Contains("\"v\":", json);
            Assert.Contains("\"p\":", json);
            Assert.DoesNotContain("plex-secret", await File.ReadAllTextAsync(path));
        }
        finally
        {
            Cleanup(path);
        }
    }

    private static (AvailabilityScheduleService Service, string Path, RecordingHandler Handler) Create(
        bool plexConfigured,
        HttpStatusCode plexStatus)
    {
        var directory = Path.Combine(Path.GetTempPath(), "fed-avail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, AvailabilityScheduleService.FileName);
        var state = new CompanionState { ClientIdentifier = "companion-id" };
        if (plexConfigured)
        {
            state.ServerBaseUrl = "http://127.0.0.1:32400";
            state.ServerAccessToken = "plex-secret";
        }

        var handler = new RecordingHandler(plexStatus);
        var service = new AvailabilityScheduleService(
            state,
            new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) },
            NullLogger<AvailabilityScheduleService>.Instance,
            path);
        return (service, path, handler);
    }

    private static void Cleanup(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (directory != null && Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;

        public RecordingHandler(HttpStatusCode status) => _status = status;

        public int Calls { get; private set; }

        public string? LastPath { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastPath = request.RequestUri?.AbsolutePath;
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent("""{"MediaContainer":{"machineIdentifier":"x"}}""", Encoding.UTF8, "application/json")
            });
        }
    }
}
