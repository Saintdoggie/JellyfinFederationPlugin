using FederationCompanion;

namespace FederationCompanion.Tests;

public class CompanionSelfCheckTests
{
    [Fact]
    public void LooksLikeUs_RequiresCompanionInfoPayload()
    {
        Assert.True(CompanionSelfCheck.LooksLikeUs("""{"kind":"PlexCompanion","federationPluginVersion":"0.0.138"}""", 200));
        Assert.False(CompanionSelfCheck.LooksLikeUs("<html>Plex</html>", 200));
        Assert.False(CompanionSelfCheck.LooksLikeUs("""{"kind":"PlexCompanion"}""", 404));
    }

    [Fact]
    public void LooksLikePlex_DetectsMediaServerPages()
    {
        Assert.True(CompanionSelfCheck.LooksLikePlex("""<MediaContainer size="0"></MediaContainer>"""));
        Assert.True(CompanionSelfCheck.LooksLikePlex("Welcome to Plex Media Server"));
        Assert.False(CompanionSelfCheck.LooksLikePlex("""{"kind":"PlexCompanion"}"""));
    }
}
