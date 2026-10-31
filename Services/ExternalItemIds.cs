using System;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>Stable identifiers shared by cache compatibility and external adapters.</summary>
    public static class ExternalItemIds
    {
        public static Guid PlexRatingKeyToGuid(string ratingKey)
        {
            var bytes = MD5.HashData(Encoding.UTF8.GetBytes("plex-item:" + ratingKey));
            return new Guid(bytes);
        }
    }
}
