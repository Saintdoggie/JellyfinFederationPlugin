using System;
using System.IO;
using Jellyfin.Plugin.Federation.Services;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

/// <summary>
/// Viewer session tokens survive a restart like playback tokens: a friend
/// caches them for hours and a direct play sends the viewer's player straight
/// here, so a restart that forgot them refused that friend's direct plays.
/// </summary>
public class FederationUserSessionTokenPersistenceTests
{
    [Fact]
    public void SessionTokens_SurviveARestart_AndTheFileHoldsNoUsableToken()
    {
        var path = Path.Combine(Path.GetTempPath(), "fed-session-tokens-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var before = new FederationUserSessionTokenService(path);
            var token = before.Issue("friend-a", "viewer-1");
            before.Save();

            Assert.DoesNotContain(token, File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);

            var after = new FederationUserSessionTokenService(path);
            Assert.True(after.TryValidate(token, out var federationId, out var viewer));
            Assert.Equal("friend-a", federationId);
            Assert.Equal("viewer-1", viewer);
            Assert.False(after.TryValidate("not-a-token", out _, out _));

            File.WriteAllText(path, "not json");
            Assert.False(new FederationUserSessionTokenService(path).TryValidate(token, out _, out _));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void WithoutAStore_TokensStillWork()
    {
        var service = new FederationUserSessionTokenService(null);
        var token = service.Issue("friend-a", "viewer-1");

        Assert.True(service.TryValidate(token, out var federationId, out _));
        Assert.Equal("friend-a", federationId);
    }
}
