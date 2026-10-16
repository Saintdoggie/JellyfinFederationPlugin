using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Signed addresses on this server for a friend's episode images. An episode
    /// stores one as its image path instead of the downloaded image; Jellyfin
    /// fetches it the first time the image is shown (ConvertImageToLocal), so a
    /// first sync no longer downloads thousands of episode images nobody looks
    /// at. Signed with a key kept on this server, not with the friend's token:
    /// the address lives in the database for as long as the episode, and must
    /// neither carry a credential nor break when the friend's token is replaced.
    /// </summary>
    internal static class LazyArtwork
    {
        private static readonly object KeyLock = new();
        private static byte[]? _key;

        /// <summary>Where the key file lives; the plugin data folder unless a test sets it.</summary>
        internal static string? KeyDirectory { get; set; }

        public static string BuildUrl(string localBaseUrl, string serverId, Guid remoteItemId, string tag)
            => $"{localBaseUrl.TrimEnd('/')}/Plugins/Federation/Artwork?serverId={Uri.EscapeDataString(serverId)}&itemId={remoteItemId:N}&tag={Uri.EscapeDataString(tag)}&sig={Sign(serverId, remoteItemId, tag)}";

        public static bool Verify(string? serverId, Guid remoteItemId, string? tag, string? signature)
        {
            if (string.IsNullOrEmpty(serverId) || tag == null || string.IsNullOrEmpty(signature))
            {
                return false;
            }

            var expected = Encoding.ASCII.GetBytes(Sign(serverId, remoteItemId, tag));
            var supplied = Encoding.ASCII.GetBytes(signature);
            return expected.Length == supplied.Length && CryptographicOperations.FixedTimeEquals(expected, supplied);
        }

        private static string Sign(string serverId, Guid remoteItemId, string tag)
        {
            using var hmac = new HMACSHA256(Key());
            var payload = $"art1\n{serverId}\n{remoteItemId:N}\nPrimary\n{tag}";
            return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
        }

        private static byte[] Key()
        {
            lock (KeyLock)
            {
                if (_key != null)
                {
                    return _key;
                }

                var dir = KeyDirectory ?? Plugin.Instance?.DataFolderPath ?? Path.GetTempPath();
                var path = Path.Combine(dir, "artwork-url.key");
                try
                {
                    if (File.Exists(path))
                    {
                        var stored = File.ReadAllBytes(path);
                        if (stored.Length == 32)
                        {
                            return _key = stored;
                        }
                    }

                    Directory.CreateDirectory(dir);
                    var created = RandomNumberGenerator.GetBytes(32);
                    File.WriteAllBytes(path, created);
                    return _key = created;
                }
                catch (IOException)
                {
                    // Without a stored key the addresses only last until restart;
                    // the next sync stamps fresh ones.
                    return _key = RandomNumberGenerator.GetBytes(32);
                }
            }
        }

        /// <summary>Test seam: forget the loaded key.</summary>
        internal static void ResetForTests() => _key = null;
    }
}
