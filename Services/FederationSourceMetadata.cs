using System;
using System.Linq;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.Federation.Services;

/// <summary>Apply source corrections to already persisted federated items.
/// A missing remote field preserves the last known value during incomplete syncs.</summary>
internal static class FederationSourceMetadata
{
    internal static bool Apply(BaseItem item, FederatedItemMetadata metadata)
    {
        var before = Snapshot(item);
        if (!string.IsNullOrEmpty(metadata.Name)) item.Name = metadata.Name;
        item.Overview = metadata.Overview ?? item.Overview;
        item.OriginalTitle = metadata.OriginalTitle ?? item.OriginalTitle;
        item.ProductionYear = metadata.ProductionYear ?? item.ProductionYear;
        item.PremiereDate = metadata.PremiereDate ?? item.PremiereDate;
        item.CommunityRating = metadata.CommunityRating ?? item.CommunityRating;
        item.OfficialRating = metadata.OfficialRating ?? item.OfficialRating;
        item.RunTimeTicks = metadata.RunTimeTicks ?? item.RunTimeTicks;
        item.Genres = metadata.Genres ?? item.Genres;
        item.Studios = metadata.Studios ?? item.Studios;
        return before != Snapshot(item);
    }

    private static string Snapshot(BaseItem item) => System.Text.Json.JsonSerializer.Serialize(new
    {
        item.Name, item.Overview, item.OriginalTitle, item.ProductionYear, item.PremiereDate,
        item.CommunityRating, item.OfficialRating, item.RunTimeTicks, item.Genres, item.Studios
    });
}
