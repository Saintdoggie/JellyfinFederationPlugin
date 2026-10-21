using Jellyfin.Plugin.Federation.Services;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

public class IncomingContentFilterTests
{
    [Theory]
    [InlineData("TV-14", "NC-17", true)]
    [InlineData("NC-17", "TV-14", false)]
    [InlineData("R", "TV-PG", false)]
    [InlineData("TV-PG", "R", true)]
    [InlineData("TV-MA", "PG-13", false)]
    [InlineData("TV-G", "G", true)]
    [InlineData("TV-14", "PG-13", true)]
    [InlineData("NR", "G", true)]
    public void Ceiling_ranks_across_scales(string item, string ceiling, bool allowed)
        => Assert.Equal(allowed, IncomingContentFilterService.IsWithinRatingCeiling(item, ceiling));

    [Fact]
    public void Unknown_ceiling_blocks_rated_items()
        => Assert.False(IncomingContentFilterService.IsWithinRatingCeiling("R", "PG13"));

    [Fact]
    public void Global_ceiling_survives_unknown_user_ceiling()
    {
        Assert.False(IncomingContentFilterService.IsAllowedByRatingCeilings("R", "PG", "bogus"));
        Assert.False(IncomingContentFilterService.IsAllowedByRatingCeilings("R", "bogus", "PG"));
        Assert.True(IncomingContentFilterService.IsAllowedByRatingCeilings("G", "PG", "bogus"));
    }

    [Fact]
    public void Stricter_known_ceiling_wins()
    {
        Assert.False(IncomingContentFilterService.IsAllowedByRatingCeilings("PG-13", "PG", "R"));
        Assert.False(IncomingContentFilterService.IsAllowedByRatingCeilings("PG-13", "R", "PG"));
        Assert.True(IncomingContentFilterService.IsAllowedByRatingCeilings("PG", "PG", "R"));
    }
}
