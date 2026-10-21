using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>What a native Jellyfin media request asks for.</summary>
    public enum RemoteMediaKind
    {
        /// <summary><c>{type}/{id}/master.m3u8</c>.</summary>
        HlsMaster,

        /// <summary><c>{type}/{id}/main.m3u8</c>.</summary>
        HlsVariant,

        /// <summary><c>{type}/{id}/hls1/{playlist}/{segment}.{ext}</c>.</summary>
        HlsSegment,

        /// <summary><c>{type}/{id}/stream[.{container}]</c>, static or progressive transcode.</summary>
        Stream,

        /// <summary><c>Audio/{id}/universal</c>.</summary>
        UniversalAudio,

        /// <summary><c>Videos/{id}/{source}/Subtitles/{index}[/{ticks}]/Stream.{format}</c>.</summary>
        Subtitle,

        /// <summary><c>Videos/{id}/{source}/Subtitles/{index}/subtitles.m3u8</c>.</summary>
        SubtitlePlaylist,

        /// <summary><c>Videos/{id}/{source}/Attachments/{index}</c> (fonts for styled subtitles).</summary>
        Attachment
    }

    /// <summary>
    /// The native Jellyfin media requests the remote-transcode handoff may carry,
    /// and nothing else. The receiving server uses this to decide which of its own
    /// requests to hand to the owner; the owner uses the same parser as the
    /// allow-list for its gateway, so a playback token can never be turned into
    /// a request for any other endpoint or any other item.
    /// </summary>
    /// <param name="Kind">What is asked for.</param>
    /// <param name="Audio">True for <c>Audio/...</c>, false for <c>Videos/...</c>.</param>
    /// <param name="ItemId">The item in the path.</param>
    /// <param name="MediaSourceId">The media source in the path (subtitles/attachments only).</param>
    /// <param name="Suffix">Everything after the item (or source) segment, original case.</param>
    public sealed record RemoteMediaPath(RemoteMediaKind Kind, bool Audio, Guid ItemId, Guid? MediaSourceId, string Suffix)
    {
        private const string Id = "[0-9a-fA-F]{32}|[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}";

        private static readonly Regex ItemScoped = new(
            "^(?<type>videos|audio)/(?<id>" + Id + ")/(?<rest>master\\.m3u8|main\\.m3u8|universal|stream(?:\\.[a-z0-9]{1,10})?|hls1/[a-z0-9_-]{1,64}/-?[0-9]{1,9}\\.[a-z0-9]{1,10})$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
            TimeSpan.FromMilliseconds(100));

        private static readonly Regex SourceScoped = new(
            "^videos/(?<id>" + Id + ")/(?<source>" + Id + ")/(?<rest>subtitles/[0-9]{1,5}/(?:[0-9]{1,19}/)?stream\\.[a-z0-9]{1,10}|subtitles/[0-9]{1,5}/subtitles\\.m3u8|attachments/[0-9]{1,5})$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
            TimeSpan.FromMilliseconds(100));

        /// <summary>
        /// Query parameters never forwarded to the owner: credentials of the
        /// receiving server (or anything credential-shaped), identities that only
        /// mean something there, and live streams, which federation never serves.
        /// </summary>
        private static readonly HashSet<string> DroppedQueryKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            "api_key", "apikey", "x-emby-token", "x-mediabrowser-token", "token", "accesstoken",
            "userid", "liveStreamId", "deviceProfileId"
        };

        /// <summary>
        /// The key a work-starting request is counted under for the per-friend limit. Normally the
        /// PlaySessionId; a request without one (some audio/stream URLs) must not slip past the
        /// limit, so with a limit set it is counted under a key derived from the item and device,
        /// which stays the same across that play's follow-up requests.
        /// </summary>
        public static string? SessionKeyForLimit(string? playSessionId, string? deviceId, Guid itemId, int limit)
        {
            if (!string.IsNullOrEmpty(playSessionId))
            {
                return playSessionId;
            }

            return limit > 0 ? $"nosession-{itemId:N}-{deviceId}" : null;
        }

        /// <summary>True when the request starts a transcode or stream on the owner (counted against its limit).</summary>
        public bool StartsWork => Kind is RemoteMediaKind.HlsMaster or RemoteMediaKind.HlsVariant or RemoteMediaKind.HlsSegment or RemoteMediaKind.Stream or RemoteMediaKind.UniversalAudio;

        /// <summary>
        /// True for small responses the receiving server fetches from the owner
        /// itself (so it can still fall back to serving the request locally),
        /// false for media bytes, which the client is redirected to.
        /// </summary>
        public bool FetchedByReceiver => Kind is RemoteMediaKind.HlsMaster or RemoteMediaKind.HlsVariant
            or RemoteMediaKind.Subtitle or RemoteMediaKind.SubtitlePlaylist or RemoteMediaKind.Attachment;

        /// <summary>
        /// Parses a request path (with or without a leading slash, without the
        /// server's base URL) into one of the allowed shapes.
        /// </summary>
        public static bool TryParse(string? path, out RemoteMediaPath? result)
        {
            result = null;
            if (string.IsNullOrEmpty(path) || path.Length > 400 || path.Contains("..", StringComparison.Ordinal) || path.Contains('\\', StringComparison.Ordinal))
            {
                return false;
            }

            var trimmed = path.TrimStart('/');
            try
            {
                var match = ItemScoped.Match(trimmed);
                if (match.Success)
                {
                    var audio = string.Equals(match.Groups["type"].Value, "audio", StringComparison.OrdinalIgnoreCase);
                    var rest = match.Groups["rest"].Value;
                    var lower = rest.ToLowerInvariant();
                    RemoteMediaKind kind;
                    if (lower == "master.m3u8")
                    {
                        kind = RemoteMediaKind.HlsMaster;
                    }
                    else if (lower == "main.m3u8")
                    {
                        kind = RemoteMediaKind.HlsVariant;
                    }
                    else if (lower == "universal")
                    {
                        if (!audio)
                        {
                            return false;
                        }

                        kind = RemoteMediaKind.UniversalAudio;
                    }
                    else if (lower.StartsWith("hls1/", StringComparison.Ordinal))
                    {
                        kind = RemoteMediaKind.HlsSegment;
                    }
                    else
                    {
                        kind = RemoteMediaKind.Stream;
                    }

                    result = new RemoteMediaPath(kind, audio, Guid.Parse(match.Groups["id"].Value), null, rest);
                    return true;
                }

                match = SourceScoped.Match(trimmed);
                if (match.Success)
                {
                    var rest = match.Groups["rest"].Value;
                    var lower = rest.ToLowerInvariant();
                    var kind = lower.StartsWith("attachments/", StringComparison.Ordinal)
                        ? RemoteMediaKind.Attachment
                        : lower.EndsWith("subtitles.m3u8", StringComparison.Ordinal) ? RemoteMediaKind.SubtitlePlaylist : RemoteMediaKind.Subtitle;
                    result = new RemoteMediaPath(kind, false, Guid.Parse(match.Groups["id"].Value), Guid.Parse(match.Groups["source"].Value), rest);
                    return true;
                }
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }

            return false;
        }

        /// <summary>
        /// This request's path on another server, where the item (and source) have
        /// other ids. Always lower-case type segment, "N" ids, original suffix.
        /// </summary>
        public string ToNativePath(Guid itemId, Guid? mediaSourceId)
        {
            var type = Audio ? "audio" : "videos";
            return MediaSourceId.HasValue
                ? $"videos/{itemId:N}/{(mediaSourceId ?? itemId):N}/{Suffix}"
                : $"{type}/{itemId:N}/{Suffix}";
        }

        /// <summary>
        /// Rebuilds a query string without credential/identity parameters,
        /// optionally replacing values. <paramref name="replace"/> receives each
        /// remaining key and value and returns the value to send (null drops it).
        /// </summary>
        public static string FilterQuery(IEnumerable<KeyValuePair<string, StringValues>> query, Func<string, string, string?>? replace = null)
        {
            var sb = new StringBuilder();
            foreach (var (key, values) in query)
            {
                if (string.IsNullOrEmpty(key) || DroppedQueryKeys.Contains(key))
                {
                    continue;
                }

                foreach (var raw in values)
                {
                    var value = replace == null ? raw : replace(key, raw ?? string.Empty);
                    if (value == null)
                    {
                        continue;
                    }

                    sb.Append(sb.Length == 0 ? '?' : '&')
                        .Append(Uri.EscapeDataString(key))
                        .Append('=')
                        .Append(Uri.EscapeDataString(value));
                }
            }

            return sb.ToString();
        }

        /// <summary>Parses "a=1&amp;b=2" (with or without '?') into pairs, keeping order.</summary>
        public static List<KeyValuePair<string, StringValues>> ParseQuery(string? query)
            => Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(query).ToList();

        /// <summary>Reads one query value case-insensitively.</summary>
        public static string? QueryValue(IEnumerable<KeyValuePair<string, StringValues>> query, string key)
        {
            foreach (var (k, v) in query)
            {
                if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
                {
                    return v.FirstOrDefault();
                }
            }

            return null;
        }

        /// <summary>Reads one integer query value case-insensitively.</summary>
        public static long? QueryLong(IEnumerable<KeyValuePair<string, StringValues>> query, string key)
            => long.TryParse(QueryValue(query, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
    }
}
