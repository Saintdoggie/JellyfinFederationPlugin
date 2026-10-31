using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Federation.Configuration;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>Builds the admin-safe server representation shared by core and optional add-ins.</summary>
    public static class FederationServerPresentation
    {
        public static void PreserveConnectionMetadata(RemoteServer existing, RemoteServer posted)
        {
            posted.CompanionUrl = existing.CompanionUrl;
            posted.FederationPluginVersion = existing.FederationPluginVersion;
        }

        public static object Sanitize(RemoteServer server, FriendDataLimits? limits)
        {
            return new
            {
                server.Id,
                server.Name,
                server.Url,
                server.Enabled,
                server.UserId,
                Kind = (int)server.Kind,
                server.AllowedExternalLibraryIds,
                StreamingMode = (int)server.StreamingMode,
                server.Priority,
                server.RequireApiKeyForImages,
                WanCapMode = (int)server.WanCapMode,
                server.WanMaxBitrateMbps,
                server.WanMaxHeight,
                HasApiKey = !string.IsNullOrEmpty(server.ApiKey),
                server.Paired,
                server.ShareAllFriends,
                server.SharedFriendIds,
                HasIssuedApiKey = !string.IsNullOrEmpty(server.IssuedApiKey),
                server.ShareAllLibraries,
                server.SharedLibraryFolderIds,
                server.ShareAllUsers,
                server.SharedUserIds,
                server.ExcludedItemIds,
                server.SharingExpiresUtc,
                server.LibraryShareExpiries,
                server.AllowDownloads,
                server.AllowBulkDownloads,
                server.AllowRemoteTranscode,
                server.MonthlyDataCapGb,
                server.MaxConcurrentStreams,
                server.DirectLinkMode,
                server.SharePopular,
                server.ShareViewerNames,
                DataUsedThisMonthBytes = limits?.UsedThisMonth(server.FederationId ?? string.Empty) ?? 0,
                ActiveStreams = limits?.ActiveStreams(server.FederationId ?? string.Empty) ?? 0,
                RemoteUserAccessRules = (server.RemoteUserAccessRules ?? new List<RemoteUserAccessRule>()).Select(rule => new
                {
                    rule.RemoteUserId,
                    rule.RemoteUserName,
                    Mode = (int)rule.Mode,
                    rule.LibraryFolderIds,
                    rule.ItemIds,
                    rule.MaxAllowedRating,
                    rule.AllowDownload,
                    rule.BlockedItemIds
                }).ToList(),
                FriendUserAccessRules = (server.FriendUserAccessRules ?? new List<RemoteUserAccessRule>()).Select(rule => new
                {
                    rule.RemoteUserId,
                    rule.RemoteUserName,
                    Mode = (int)rule.Mode,
                    rule.LibraryFolderIds,
                    rule.ItemIds,
                    rule.MaxAllowedRating,
                    rule.AllowDownload,
                    rule.BlockedItemIds
                }).ToList()
            };
        }
    }
}
