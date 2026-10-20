using System;
using System.IO;
using System.Linq;
using MediaBrowser.Controller.Entities;
using Microsoft.Net.Http.Headers;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// How a viewer's "Download" of a friend's title is answered (see
    /// <see cref="FederationDeviceDownloadFilter"/>).
    /// </summary>
    public enum DeviceDownloadRoute
    {
        /// <summary>Send the viewer's app straight to the friend's server.</summary>
        Redirect,

        /// <summary>Relay the file through this server.</summary>
        Relay
    }

    /// <summary>
    /// Pure helpers shared by both ends of a device download: the owner naming the
    /// file it sends, and the viewer's server deciding where the app fetches it.
    /// </summary>
    public static class DeviceDownload
    {
        private static readonly char[] Invalid = Path.GetInvalidFileNameChars()
            .Concat(new[] { '"', '\\', '/', ':', '*', '?', '<', '>', '|' })
            .Distinct()
            .ToArray();

        /// <summary>
        /// A <c>Content-Disposition: attachment</c> value carrying the file name both
        /// as an ASCII fallback and as RFC 5987 UTF-8, so non-English titles keep
        /// their name and no header-injection characters ever reach the header.
        /// </summary>
        public static string AttachmentHeader(string? fileName)
        {
            var header = new ContentDispositionHeaderValue("attachment");
            header.SetHttpFileName(SafeFileName(fileName));
            return header.ToString();
        }

        /// <summary>
        /// A file name with path separators, control characters and reserved
        /// characters removed; "download" when nothing usable is left.
        /// </summary>
        public static string SafeFileName(string? fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return "download";
            }

            var cleaned = new string(fileName
                .Where(c => !char.IsControl(c) && Array.IndexOf(Invalid, c) < 0)
                .ToArray())
                .Trim()
                .Trim('.')
                .Trim();
            if (cleaned.Length > 200)
            {
                var extension = Path.GetExtension(cleaned);
                cleaned = cleaned[..(200 - extension.Length)].TrimEnd() + extension;
            }

            return cleaned.Length == 0 ? "download" : cleaned;
        }

        /// <summary>
        /// The owner's name for a file it sends: the media file's own name, as
        /// Jellyfin's native download uses, or the title when the item has no file.
        /// </summary>
        public static string OwnerFileName(BaseItem? item)
        {
            if (item == null)
            {
                return "download";
            }

            if (item.IsFileProtocol && !string.IsNullOrEmpty(item.Path))
            {
                var name = Path.GetFileName(item.Path);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    return SafeFileName(name);
                }
            }

            return SafeFileName(item.Name);
        }

        /// <summary>
        /// The viewer's server's name for a relayed file: the title plus the
        /// container the friend reported, e.g. "Dune Part Two (2024).mkv".
        /// </summary>
        public static string TitleFileName(string? title, int? year, string? container, string fallbackExtension)
        {
            var name = string.IsNullOrWhiteSpace(title) ? "download" : title.Trim();
            if (year is > 1800 and < 3000 && !name.Contains("(" + year.Value + ")", StringComparison.Ordinal))
            {
                name += " (" + year.Value + ")";
            }

            var extension = (container ?? string.Empty).Split(',')[0].Trim().TrimStart('.');
            if (extension.Length == 0 || extension.Length > 8 || !extension.All(char.IsLetterOrDigit))
            {
                extension = fallbackExtension;
            }

            return SafeFileName(name + "." + extension.ToLowerInvariant());
        }

        /// <summary>
        /// Whether the app can be sent straight to the friend: the friend must name
        /// the file itself (otherwise a browser would play it in place of saving
        /// it), and the redirect must keep the scheme the app used (browsers block
        /// an https page's download from an http address; some players refuse a
        /// redirect that changes scheme).
        /// </summary>
        public static DeviceDownloadRoute ChooseRoute(bool friendNamesFile, string friendBaseUrl, string requestScheme)
        {
            if (!friendNamesFile
                || !Uri.TryCreate(friendBaseUrl, UriKind.Absolute, out var friend)
                || !string.Equals(friend.Scheme, requestScheme, StringComparison.OrdinalIgnoreCase))
            {
                return DeviceDownloadRoute.Relay;
            }

            return DeviceDownloadRoute.Redirect;
        }

        /// <summary>
        /// The friend's item-scoped download address. Carries only the download
        /// token, never this server's credentials or the viewer's.
        /// </summary>
        public static string FriendDownloadUrl(string friendBaseUrl, Guid remoteItemId, string token, bool audio)
            => friendBaseUrl.TrimEnd('/')
                + "/Plugins/Federation/DirectStream/" + remoteItemId.ToString("N")
                + "?token=" + Uri.EscapeDataString(token)
                + "&download=true"
                + (audio ? "&audio=true" : string.Empty);
    }
}
