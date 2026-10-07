using System;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Compact "when this process tends to be up" blob. 168 UTC hour-of-week
    /// buckets, no URLs, tokens, or friend names.
    /// </summary>
    public sealed class AvailabilityScheduleBlob
    {
        [JsonPropertyName("v")]
        public int Version { get; set; } = 1;

        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("utc")]
        public DateTime Utc { get; set; }

        [JsonPropertyName("n")]
        public int SampleHours { get; set; }

        [JsonPropertyName("p")]
        public string Probabilities { get; set; } = string.Empty;
    }

    public enum AvailabilityForecast
    {
        Unknown = 0,
        LikelyOnline = 1,
        LikelyOffline = 2
    }

    /// <summary>
    /// Hour-of-week histogram math. Saturating byte counters with a right-shift
    /// decay; encode unknown as 255 so "never sampled" is not "always down".
    /// </summary>
    public static class AvailabilitySchedule
    {
        public const int BucketCount = 168;
        public const byte UnknownFraction = 255;
        public const int Version = 1;

        public static int BucketIndex(DateTime utc)
        {
            var u = utc.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(utc, DateTimeKind.Utc) : utc.ToUniversalTime();
            return ((int)u.DayOfWeek * 24) + u.Hour;
        }

        public static void Increment(byte[] online, byte[] total, int bucket, bool wasOnline)
        {
            if (online == null || total == null || online.Length != BucketCount || total.Length != BucketCount)
            {
                throw new ArgumentException("Histogram must have 168 buckets.");
            }

            if (bucket < 0 || bucket >= BucketCount)
            {
                return;
            }

            if (total[bucket] == 255)
            {
                online[bucket] = (byte)(online[bucket] >> 1);
                total[bucket] = (byte)(total[bucket] >> 1);
            }

            if (wasOnline && online[bucket] < 255)
            {
                online[bucket]++;
            }

            if (total[bucket] < 255)
            {
                total[bucket]++;
            }
        }

        public static byte EncodeFraction(byte online, byte total)
        {
            if (total == 0)
            {
                return UnknownFraction;
            }

            return (byte)Math.Clamp((int)Math.Round(254.0 * online / total), 0, 254);
        }

        public static AvailabilityScheduleBlob EncodeSelf(string federationId, byte[] online, byte[] total, DateTime utc)
        {
            var packed = new byte[BucketCount];
            var sampled = 0;
            for (var i = 0; i < BucketCount; i++)
            {
                packed[i] = EncodeFraction(online[i], total[i]);
                if (packed[i] != UnknownFraction)
                {
                    sampled++;
                }
            }

            return new AvailabilityScheduleBlob
            {
                Version = Version,
                Id = federationId ?? string.Empty,
                Utc = utc.ToUniversalTime(),
                SampleHours = sampled,
                Probabilities = Convert.ToBase64String(packed)
            };
        }

        public static byte[] DecodeFractions(AvailabilityScheduleBlob? blob)
        {
            var packed = new byte[BucketCount];
            Array.Fill(packed, UnknownFraction);
            if (blob == null || blob.Version != Version || string.IsNullOrEmpty(blob.Probabilities))
            {
                return packed;
            }

            try
            {
                var raw = Convert.FromBase64String(blob.Probabilities);
                var n = Math.Min(BucketCount, raw.Length);
                Array.Copy(raw, packed, n);
            }
            catch (FormatException)
            {
            }

            return packed;
        }

        public static (AvailabilityForecast Forecast, int Confidence) Forecast(
            byte[] localOnline,
            byte[] localTotal,
            byte[]? priorFractions,
            int priorSampleHours,
            DateTime utc)
        {
            var bucket = BucketIndex(utc);
            var nObs = localTotal != null && bucket < localTotal.Length ? localTotal[bucket] : 0;
            double pObs = 0.5;
            if (nObs > 0 && localOnline != null)
            {
                pObs = localOnline[bucket] / (double)nObs;
            }

            var prior = priorFractions != null && bucket < priorFractions.Length ? priorFractions[bucket] : UnknownFraction;
            var w = prior == UnknownFraction ? 0 : Math.Min(8.0, Math.Max(0, priorSampleHours) / 20.0);
            var pPrior = prior == UnknownFraction ? 0.5 : prior / 254.0;
            var denom = nObs + w;
            if (denom < 4)
            {
                return (AvailabilityForecast.Unknown, nObs);
            }

            var p = ((nObs * pObs) + (w * pPrior)) / denom;
            if (p >= 0.70)
            {
                return (AvailabilityForecast.LikelyOnline, nObs);
            }

            if (p <= 0.30)
            {
                return (AvailabilityForecast.LikelyOffline, nObs);
            }

            return (AvailabilityForecast.Unknown, nObs);
        }

        public static DateTime? NextLikelyOnlineUtc(byte[] fractions, DateTime utc)
        {
            if (fractions == null || fractions.Length != BucketCount)
            {
                return null;
            }

            var start = BucketIndex(utc);
            for (var i = 1; i <= BucketCount; i++)
            {
                var b = (start + i) % BucketCount;
                if (fractions[b] != UnknownFraction && fractions[b] / 254.0 >= 0.70)
                {
                    return utc.ToUniversalTime().Date.AddHours(utc.ToUniversalTime().Hour + i);
                }
            }

            return null;
        }
    }
}
