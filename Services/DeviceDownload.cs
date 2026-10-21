using System;
using System.Collections.Generic;
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
        /// Whether the app can be sent straight to the friend, and why not when it
        /// can't. The friend must name the file itself (otherwise a browser would
        /// play it in place of saving it) and the address must not downgrade the
        /// app: browsers block an https page's download from an http address. A
        /// friend on https is fine for an http app (that is an upgrade). A friend
        /// only known by an http address is tried on https first when the app used
        /// https (<paramref name="httpsBase"/>, the same host once seen reachable).
        /// </summary>
        /// <returns>The route, the base address to redirect to, and the reason for a relay.</returns>
        public static (DeviceDownloadRoute Route, string BaseUrl, string? Reason) Decide(
            bool friendNamesFile,
            string friendBaseUrl,
            string requestScheme,
            string? httpsBase = null)
        {
            if (!friendNamesFile)
            {
                return (DeviceDownloadRoute.Relay, friendBaseUrl, "the friend runs an older Federation plugin that can't send a named file to a device");
            }

            if (!Uri.TryCreate(friendBaseUrl, UriKind.Absolute, out var friend))
            {
                return (DeviceDownloadRoute.Relay, friendBaseUrl, "the friend's address isn't a valid URL");
            }

            if (string.Equals(friend.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(requestScheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                return (DeviceDownloadRoute.Redirect, friendBaseUrl, null);
            }

            return string.IsNullOrEmpty(httpsBase)
                ? (DeviceDownloadRoute.Relay, friendBaseUrl, "the app uses https but the friend has no reachable https address")
                : (DeviceDownloadRoute.Redirect, httpsBase, null);
        }

        /// <summary>
        /// Https addresses to try for an http friend address: the same port first,
        /// then the default port when the port was explicit (a TLS proxy usually
        /// serves https on 443 while http is on 8096).
        /// </summary>
        public static IReadOnlyList<string> HttpsCandidates(string friendBaseUrl)
        {
            var list = new List<string>();
            if (HttpsForm(friendBaseUrl) is { } same)
            {
                list.Add(same);
                var uri = new Uri(friendBaseUrl);
                if (!uri.IsDefaultPort && uri.Port != 443)
                {
                    list.Add(new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri.GetLeftPart(UriPartial.Authority));
                }
            }

            return list;
        }

        /// <summary>The https form of an http friend address (same host and port), or null.</summary>
        public static string? HttpsForm(string friendBaseUrl)
            => Uri.TryCreate(friendBaseUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttp
                ? new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps, Port = uri.IsDefaultPort ? -1 : uri.Port }.Uri.GetLeftPart(UriPartial.Authority)
                : null;

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
