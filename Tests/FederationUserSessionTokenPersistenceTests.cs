using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Services;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

/// <summary>Viewer and playback tokens survive restarts without writing usable tokens.</summary>
public class FederationUserSessionTokenPersistenceTests
{
    [Fact]
    public void SessionTokens_SurviveARestart_AndTheFileHoldsNoUsableToken()
    {
        var path = TempPath();
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
    public void TokensMintedJustBeforeShutdown_AreSavedWhenServicesAreDisposed()
    {
        var path = TempPath();
        var playbackPath = path + ".playback";
        try
        {
            var sessions = new FederationUserSessionTokenService(path);
            var session = sessions.Issue("friend-a", "viewer-1");
            var playback = new FederationPlaybackTokenService(playbackPath);
            var item = Guid.NewGuid().ToString("N");
            var token = playback.Issue(item, "friend-a", FederationTokenPurpose.Playback, "viewer-1");

            sessions.Dispose();
            playback.Dispose();

            Assert.True(new FederationUserSessionTokenService(path).TryValidate(session, out _, out _));
            Assert.True(new FederationPlaybackTokenService(playbackPath).TryValidate(token, item, out var federationId));
            Assert.Equal("friend-a", federationId);
        }
        finally
        {
            File.Delete(path);
            File.Delete(playbackPath);
        }
    }

    [Fact]
    public async Task ConcurrentSaves_DoNotCorruptTheTokenFile()
    {
        var path = TempPath();
        try
        {
            var service = new FederationUserSessionTokenService(path);
            var token = service.Issue("friend-a", "viewer-1");
            await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(service.Save)));
            service.Dispose();

            Assert.True(new FederationUserSessionTokenService(path).TryValidate(token, out _, out _));
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".tmp");
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

    private static string TempPath() => Path.Combine(Path.GetTempPath(), "fed-session-tokens-" + Guid.NewGuid().ToString("N") + ".json");
}
