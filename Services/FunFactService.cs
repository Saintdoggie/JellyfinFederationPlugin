using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Supplies a short fun fact for the loading screen, fetched server-side from a free
    /// public fact API so the browser never contacts a third party and viewers' addresses
    /// are not exposed to it. If the API is slow, down, or switched off in settings, a
    /// built-in list is used instead, so the loading screen never waits on it.
    /// </summary>
    public class FunFactService
    {
        private const string ApiUrl = "https://uselessfacts.jsph.pl/api/v2/facts/random?language=en";
        private const int MaxFactLength = 280;

        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3);

        // Never call the API more than once in this window, however many viewers ask.
        private static readonly TimeSpan MinFetchInterval = TimeSpan.FromSeconds(4);

        private static readonly HttpClient DefaultHttpClient = new() { Timeout = RequestTimeout };

        private static readonly string[] Fallbacks =
        {
            "The first feature-length film, The Story of the Kelly Gang (1906), ran about an hour.",
            "Early films were shot on nitrate stock, which is so flammable that projection booths were built from fireproof materials.",
            "The Lumiere brothers held the first paid public film screening in Paris in 1895.",
            "A movie shown at 24 frames per second flashes 24 still pictures at you every second.",
            "The Wilhelm scream sound effect has appeared in hundreds of films since 1951.",
            "Sound film became mainstream with The Jazz Singer in 1927.",
            "Many film trailers are cut by outside editing houses rather than the film's own editor.",
            "The word cinematography comes from the Greek for writing with movement.",
            "A standard feature film reel holds about 10 to 20 minutes of film.",
            "Blu-ray discs store up to 50 GB on a dual-layer disc, which is why remux files are so large.",
            "A single hour of 4K Blu-ray video can be 40 GB or more.",
            "Streaming a 90 Mbps video needs about 11 megabytes to arrive every second.",
        };

        private readonly ILogger<FunFactService> _logger;
        private readonly object _gate = new();
        private readonly Queue<string> _recent = new();
        private DateTime _lastFetchUtc = DateTime.MinValue;
        private int _fallbackCursor;

        /// <summary>
        /// Initializes a new instance of the <see cref="FunFactService"/> class.
        /// </summary>
        public FunFactService(ILogger<FunFactService> logger)
        {
            _logger = logger;
        }

        /// <summary>Test-only seam: when set, used instead of the shared client.</summary>
        internal static HttpClient? HttpClientOverride { get; set; }

        /// <summary>
        /// Returns one fun fact. Never throws and never returns an empty string.
        /// </summary>
        /// <param name="cancellationToken">Cancellation.</param>
        /// <returns>A single plain-text sentence.</returns>
        public async Task<string> GetAsync(CancellationToken cancellationToken = default)
        {
            var enabled = Plugin.Instance?.Configuration?.LoadingFunFacts != false;
            if (enabled && TryClaimFetch())
            {
                var fetched = await FetchAsync(cancellationToken).ConfigureAwait(false);
                if (fetched != null)
                {
                    Remember(fetched);
                    return fetched;
                }
            }

            lock (_gate)
            {
                // Prefer a fact the API already gave us recently over a canned one.
                if (enabled && _recent.Count > 0)
                {
                    var pool = _recent.ToArray();
                    return pool[Random.Shared.Next(pool.Length)];
                }

                return Fallbacks[_fallbackCursor++ % Fallbacks.Length];
            }
        }

        private bool TryClaimFetch()
        {
            lock (_gate)
            {
                if (DateTime.UtcNow - _lastFetchUtc < MinFetchInterval)
                {
                    return false;
                }

                _lastFetchUtc = DateTime.UtcNow;
                return true;
            }
        }

        private void Remember(string fact)
        {
            lock (_gate)
            {
                _recent.Enqueue(fact);
                while (_recent.Count > 20)
                {
                    _recent.Dequeue();
                }
            }
        }

        private async Task<string?> FetchAsync(CancellationToken cancellationToken)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(RequestTimeout);
                var client = HttpClientOverride ?? DefaultHttpClient;
                using var response = await client.GetAsync(ApiUrl, timeout.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(body);
                return doc.RootElement.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String
                    ? Clean(text.GetString())
                    : null;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or OperationCanceledException)
            {
                _logger.LogDebug(ex, "[Federation] Fun-fact service unavailable; using built-in facts");
                return null;
            }
        }

        // Plain text only, bounded, no control characters. The browser also inserts it
        // as text (never markup), so this is defence in depth, not the only guard.
        internal static string? Clean(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            var cleaned = new string(raw.Where(c => !char.IsControl(c)).ToArray()).Trim();
            if (cleaned.Length == 0)
            {
                return null;
            }

            return cleaned.Length <= MaxFactLength ? cleaned : cleaned[..(MaxFactLength - 1)].TrimEnd() + "…";
        }
    }
}
