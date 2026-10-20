using System;
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
        {
            if (string.IsNullOrEmpty(logDirectory) || !Directory.Exists(logDirectory) || itemId == Guid.Empty)
            {
                return null;
            }

            var n = itemId.ToString("N");
            var d = itemId.ToString("D");
            var since = startedUtc - TimeSpan.FromMinutes(1);
            var files = new DirectoryInfo(logDirectory)
                .EnumerateFiles("FFmpeg.Transcode-*.log")
                .Where(f => (f.Name.Contains(n, StringComparison.OrdinalIgnoreCase) || f.Name.Contains(d, StringComparison.OrdinalIgnoreCase))
                    && f.LastWriteTimeUtc >= since)
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

            var text = Redact(sb.ToString());
            return text.Length > MaxBytes ? text[^MaxBytes..] : text;
        }

        /// <summary>Blanks keys, tokens and passwords (a log can hold stream URLs).</summary>
        public static string Redact(string text) => Secrets.Replace(text, "$1REDACTED");

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
