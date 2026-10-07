using System;
using Jellyfin.Plugin.Federation.Services;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

public sealed class AvailabilityScheduleTests
{
    [Fact]
    public void BucketIndex_SundayMidnight_IsZero()
    {
        var utc = new DateTime(2026, 9, 20, 0, 15, 0, DateTimeKind.Utc); // Sunday
        Assert.Equal(DayOfWeek.Sunday, utc.DayOfWeek);
        Assert.Equal(0, AvailabilitySchedule.BucketIndex(utc));
    }

    [Fact]
    public void BucketIndex_WrapsWeek()
    {
        var utc = new DateTime(2026, 9, 19, 23, 0, 0, DateTimeKind.Utc); // Saturday 23:00
        Assert.Equal(DayOfWeek.Saturday, utc.DayOfWeek);
        Assert.Equal((6 * 24) + 23, AvailabilitySchedule.BucketIndex(utc));
    }

    [Fact]
    public void EncodeFraction_UnknownWhenNoSamples()
    {
        Assert.Equal(AvailabilitySchedule.UnknownFraction, AvailabilitySchedule.EncodeFraction(0, 0));
        Assert.Equal(0, AvailabilitySchedule.EncodeFraction(0, 10));
        Assert.Equal(254, AvailabilitySchedule.EncodeFraction(10, 10));
    }

    [Fact]
    public void Increment_DecaysWhenSaturated()
    {
        var online = new byte[AvailabilitySchedule.BucketCount];
        var total = new byte[AvailabilitySchedule.BucketCount];
        online[0] = 200;
        total[0] = 255;
        AvailabilitySchedule.Increment(online, total, 0, true);
        Assert.Equal(101, online[0]); // 200>>1 then +1
        Assert.Equal(128, total[0]);  // 255>>1 then +1
    }

    [Fact]
    public void Blob_RoundTripsWithoutSecrets()
    {
        var online = new byte[AvailabilitySchedule.BucketCount];
        var total = new byte[AvailabilitySchedule.BucketCount];
        AvailabilitySchedule.Increment(online, total, 3, true);
        AvailabilitySchedule.Increment(online, total, 3, true);
        AvailabilitySchedule.Increment(online, total, 5, false);
        var blob = AvailabilitySchedule.EncodeSelf("fed-id", online, total, DateTime.UtcNow);
        Assert.Equal(1, blob.Version);
        Assert.Equal("fed-id", blob.Id);
        Assert.Equal(2, blob.SampleHours);
        Assert.DoesNotContain("http", blob.Probabilities, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", blob.Probabilities, StringComparison.OrdinalIgnoreCase);
        var packed = AvailabilitySchedule.DecodeFractions(blob);
        Assert.Equal(AvailabilitySchedule.BucketCount, packed.Length);
        Assert.NotEqual(AvailabilitySchedule.UnknownFraction, packed[3]);
        Assert.Equal(0, packed[5]);
        Assert.Equal(AvailabilitySchedule.UnknownFraction, packed[7]);
    }

    [Fact]
    public void Forecast_UnknownUntilEnoughSamples()
    {
        var online = new byte[AvailabilitySchedule.BucketCount];
        var total = new byte[AvailabilitySchedule.BucketCount];
        var utc = new DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc);
        AvailabilitySchedule.Increment(online, total, 0, true);
        var (forecast, n) = AvailabilitySchedule.Forecast(online, total, null, 0, utc);
        Assert.Equal(AvailabilityForecast.Unknown, forecast);
        Assert.Equal(1, n);
    }

    [Fact]
    public void Forecast_LikelyOnlineWithLocalHistory()
    {
        var online = new byte[AvailabilitySchedule.BucketCount];
        var total = new byte[AvailabilitySchedule.BucketCount];
        var utc = new DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 8; i++)
        {
            AvailabilitySchedule.Increment(online, total, 0, true);
        }

        var (forecast, n) = AvailabilitySchedule.Forecast(online, total, null, 0, utc);
        Assert.Equal(AvailabilityForecast.LikelyOnline, forecast);
        Assert.Equal(8, n);
    }

    [Fact]
    public void Forecast_BlendsPriorUntilLocalCatchesUp()
    {
        var online = new byte[AvailabilitySchedule.BucketCount];
        var total = new byte[AvailabilitySchedule.BucketCount];
        var utc = new DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc);
        var prior = new byte[AvailabilitySchedule.BucketCount];
        Array.Fill(prior, AvailabilitySchedule.UnknownFraction);
        prior[0] = 0; // gifted "usually down"
        AvailabilitySchedule.Increment(online, total, 0, true);
        AvailabilitySchedule.Increment(online, total, 0, true);
        var (forecast, _) = AvailabilitySchedule.Forecast(online, total, prior, 160, utc);
        Assert.Equal(AvailabilityForecast.LikelyOffline, forecast);
    }

    [Fact]
    public void Decode_UnknownVersion_IsAllUnknown()
    {
        var packed = AvailabilitySchedule.DecodeFractions(new AvailabilityScheduleBlob
        {
            Version = 99,
            Probabilities = Convert.ToBase64String(new byte[168])
        });
        Assert.All(packed, b => Assert.Equal(AvailabilitySchedule.UnknownFraction, b));
    }
}
