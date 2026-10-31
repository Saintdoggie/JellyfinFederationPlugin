using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Conversion (ffmpeg) logs of handed-off plays. The friend that converts
    /// one of its titles for a viewer here hands this server the log of that play
    /// (<c>Peer/PlayLog</c>), and this server saves it in its own log folder,
    /// so it shows under Dashboard → Logs like a conversion done here.
    /// </summary>
    public static class FriendConversionLogs
    {
        /// <summary>Most of a log that is sent (the end is kept).</summary>
        internal const int MaxBytes = 2 * 1024 * 1024;

        private static readonly Regex Secrets = new(
            @"(?i)((?:api_?key|access_?token|x-emby-token|x-mediabrowser-token|token|password)[""']?\s*[=:]\s*[""']?)[^&\s""',}]+",
            RegexOptions.Compiled);

        /// <summary>
        /// Owner side: the conversion logs Jellyfin wrote for
        /// <paramref name="itemId"/> since <paramref name="startedUtc"/> (a seek
        /// restarts ffmpeg, so a play can have several), oldest first, with
        /// credentials blanked. Null when there are none.
        /// </summary>
        public static string? Collect(string logDirectory, Guid itemId, DateTime startedUtc)
            => Collect(logDirectory, itemId, startedUtc, null, null);

        /// <summary>
        /// Same, limited to logs that name one of <paramref name="conversions"/>
        /// (the output files of this session's own conversions, so another
        /// viewer's conversion of the same title is never included), with the
        /// folders in <paramref name="privateFolders"/> blanked.
        /// </summary>
        public static string? Collect(string logDirectory, Guid itemId, DateTime startedUtc, IReadOnlyCollection<string>? conversions, IEnumerable<string?>? privateFolders)
        {
            if (string.IsNullOrEmpty(logDirectory) || !Directory.Exists(logDirectory) || itemId == Guid.Empty)
            {
                return null;
            }

            var markers = conversions?
                .Select(Path.GetFileNameWithoutExtension)
                .Where(m => !string.IsNullOrEmpty(m) && m!.Length >= 8)
                .Select(m => m!)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (conversions != null && (markers == null || markers.Count == 0))
            {
                return null;
            }

            var n = itemId.ToString("N");
            var d = itemId.ToString("D");
            var since = startedUtc - TimeSpan.FromMinutes(1);
            var files = new DirectoryInfo(logDirectory)
                .EnumerateFiles("FFmpeg.Transcode-*.log")
                .Where(f => (f.Name.Contains(n, StringComparison.OrdinalIgnoreCase) || f.Name.Contains(d, StringComparison.OrdinalIgnoreCase))
                    && f.LastWriteTimeUtc >= since
                    && (markers == null || NamesAny(f, markers)))
                .OrderBy(f => f.CreationTimeUtc)
                .TakeLast(5)
                .ToList();
            if (files.Count == 0)
            {
                return null;
            }

            var sb = new StringBuilder();
            foreach (var file in files)
            {
                sb.Append("==== ").Append(file.Name).Append(" ====\n");
                try
                {
                    // ffmpeg may still be writing it.
                    using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    if (stream.Length > MaxBytes)
                    {
                        stream.Seek(-MaxBytes, SeekOrigin.End);
                    }

                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    sb.Append(reader.ReadToEnd()).Append('\n');
                }
                catch (IOException)
                {
                    sb.Append("(could not be read)\n");
                }
            }

            var text = Redact(BlankFolders(sb.ToString(), privateFolders));
            return text.Length > MaxBytes ? text[^MaxBytes..] : text;
        }

        /// <summary>
        /// Replaces each of <paramref name="folders"/> (as written in the
        /// command line and in the JSON of the media source) with "[folder]",
        /// so the friend doesn't learn how this server's disks are laid out.
        /// </summary>
        internal static string BlankFolders(string text, IEnumerable<string?>? folders)
        {
            foreach (var folder in (folders ?? Array.Empty<string?>())
                .Where(f => !string.IsNullOrWhiteSpace(f) && f!.Trim().Length > 1)
                .Select(f => f!.TrimEnd('/', '\\'))
                .Distinct(StringComparer.Ordinal)
                .OrderByDescending(f => f.Length))
            {
                var json = System.Text.Json.JsonSerializer.Serialize(folder);
                text = text.Replace(folder, "[folder]", StringComparison.Ordinal)
                    .Replace(json.Substring(1, json.Length - 2), "[folder]", StringComparison.Ordinal);
            }

            return text;
        }

        /// <summary>Whether the head of a log (its media source and command line) names one of the markers.</summary>
        private static bool NamesAny(FileInfo file, IReadOnlyCollection<string> markers)
        {
            try
            {
                using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var buffer = new byte[(int)Math.Min(stream.Length, 512 * 1024)];
                var read = stream.Read(buffer, 0, buffer.Length);
                var head = Encoding.UTF8.GetString(buffer, 0, read);
                return markers.Any(m => head.Contains(m, StringComparison.Ordinal));
            }
            catch (IOException)
            {
                return false;
            }
        }

        /// <summary>Blanks keys, tokens and passwords (a log can hold stream URLs).</summary>
        public static string Redact(string text) => Secrets.Replace(text, "$1REDACTED");

        /// <summary>A single-line, bounded attribution for a received conversion log.</summary>
        public static string DescribeSource(string friendName, string playSessionId)
        {
            var friend = new string((friendName ?? "friend").Where(c => !char.IsControl(c)).Take(80).ToArray()).Trim();
            var session = new string((playSessionId ?? string.Empty).Where(char.IsLetterOrDigit).Take(64).ToArray());
            return $"Conversion log from friend \"{friend}\" for play session {session}";
        }

        /// <summary>
        /// Receiver side: the file in this server's log folder for one play's log,
        /// named like Jellyfin's own conversion logs so the dashboard lists it with
        /// them.
        /// </summary>
        public static string LocalFileName(DateTime startedLocal, Guid localItemId, string friendName, string playSessionId)
        {
            var friend = new string((friendName ?? "friend").Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
            if (friend.Length == 0)
            {
                friend = "friend";
            }

            var session = new string((playSessionId ?? string.Empty).Where(char.IsLetterOrDigit).Take(8).ToArray());
            return $"FFmpeg.Transcode-{startedLocal:yyyy-MM-dd_HH-mm-ss}_{localItemId:N}_{friend}-{session}.log";
        }
    }
}
