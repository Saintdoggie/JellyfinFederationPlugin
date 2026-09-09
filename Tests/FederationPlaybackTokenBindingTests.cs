using System;
using Jellyfin.Plugin.Federation.Services;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

/// <summary>
/// Pins the friendship binding on item-scoped playback tokens (0.0.78): a token
/// minted through a friend's <c>PlaybackToken</c> request carries that friend's
/// federation id, so stream-time validation can reject tokens whose minting
/// friendship no longer exists. Before this, an item token minted just before an
/// unfriend kept authorizing streams for up to 24 hours after the relationship
/// was removed.
/// </summary>
public class FederationPlaybackTokenBindingTests
{
    [Fact]
    public void Issue_BindsTokenToMintingFriend_TryValidateReturnsIt()
    {
        var service = new FederationPlaybackTokenService();
        var token = service.Issue("item-1", "friend-a");

        Assert.True(service.TryValidate(token, "ITEM-1", out var federationId));
        Assert.Equal("friend-a", federationId);
    }

    [Fact]
    public void TryValidate_WrongItem_Fails_EvenForMintingFriend()
    {
        var service = new FederationPlaybackTokenService();
        var token = service.Issue("item-1", "friend-a");

        Assert.False(service.TryValidate(token, "item-2", out _));
    }

    [Fact]
    public void TryValidate_UnknownToken_Fails()
    {
        var service = new FederationPlaybackTokenService();

        Assert.False(service.TryValidate("no-such-token", "item-1", out var federationId));
        Assert.Null(federationId);
    }

    [Fact]
    public void Issue_WithBulkDownloadPurpose_PreservesPurpose()
    {
        var service = new FederationPlaybackTokenService();
        var token = service.Issue("item-1", "friend-a", FederationTokenPurpose.BulkDownload);

        Assert.True(service.TryValidate(token, "item-1", out var federationId, out var purpose));
        Assert.Equal("friend-a", federationId);
        Assert.Equal(FederationTokenPurpose.BulkDownload, purpose);
    }

    [Fact]
    public void Issue_DefaultPurpose_RemainsPlaybackForOlderCallers()
    {
        var service = new FederationPlaybackTokenService();
        var token = service.Issue("item-1", "friend-a");

        Assert.True(service.TryValidate(token, "item-1", out _, out var purpose));
        Assert.Equal(FederationTokenPurpose.Playback, purpose);
    }

    [Fact]
    public void Issue_UserScopedPlayback_PreservesUserForStreamTimeRecheck()
    {
        var service = new FederationPlaybackTokenService();
        var token = service.Issue("item-1", "friend-a", FederationTokenPurpose.Playback, "viewer-7");

        Assert.True(service.TryValidate(token, "item-1", out _, out _, out var remoteUserId));
        Assert.Equal("viewer-7", remoteUserId);
    }

    [Fact]
    public void OrdinaryDownloads_AreLimitedByDistinctItemEvenWhenCallerSplitsRequests()
    {
        var service = new FederationPlaybackTokenService();

        Assert.True(service.TryReserveOrdinaryDownload("friend-a", "item-1", out _));
        Assert.True(service.TryReserveOrdinaryDownload("friend-a", "item-2", out _));
        Assert.True(service.TryReserveOrdinaryDownload("friend-a", "item-3", out _));
        Assert.True(service.TryReserveOrdinaryDownload("friend-a", "item-1", out _));
        Assert.False(service.TryReserveOrdinaryDownload("friend-a", "item-4", out var retryAfter));
        Assert.True(retryAfter > TimeSpan.Zero);

        // Limits belong to the source-side friendship, not the entire server.
        Assert.True(service.TryReserveOrdinaryDownload("friend-b", "item-4", out _));
    }

    [Fact]
    public void DownloadTokens_AreShorterLivedThanPlaybackTokens()
    {
        Assert.True(
            FederationPlaybackTokenService.GetLifetime(FederationTokenPurpose.Download)
            < FederationPlaybackTokenService.GetLifetime(FederationTokenPurpose.Playback));
        Assert.Equal(
            FederationPlaybackTokenService.GetLifetime(FederationTokenPurpose.Download),
            FederationPlaybackTokenService.GetLifetime(FederationTokenPurpose.BulkDownload));
        Assert.Equal(
            FederationPlaybackTokenService.GetLifetime(FederationTokenPurpose.Playback),
            FederationPlaybackTokenService.GetLifetime(FederationTokenPurpose.Image));
    }

    [Fact]
    public void ImageToken_DoesNotAuthorizeDirectStream()
    {
        var service = new FederationPlaybackTokenService();
        var token = service.Issue("item-1", "friend-a", FederationTokenPurpose.Image);

        Assert.True(service.TryValidate(token, "item-1", out _, out var purpose));
        Assert.Equal(FederationTokenPurpose.Image, purpose);
        Assert.False(FederationPlaybackTokenService.AllowsDirectStream(purpose, download: false));
        Assert.False(FederationPlaybackTokenService.AllowsDirectStream(purpose, download: true));
        Assert.True(FederationPlaybackTokenService.AllowsDirectImage(purpose));
    }

    [Fact]
    public void PlaybackToken_DoesNotAuthorizeImages()
    {
        var service = new FederationPlaybackTokenService();
        var token = service.Issue("item-1", "friend-a");

        Assert.True(service.TryValidate(token, "item-1", out _, out var purpose));
        Assert.True(FederationPlaybackTokenService.AllowsDirectStream(purpose, download: false));
        Assert.False(FederationPlaybackTokenService.AllowsDirectImage(purpose));
    }

    [Fact]
    public void SessionToken_IsNotItemScoped_SoClientDirectPathsMustNotUseIt()
    {
        // A session token validates for any item the user can currently see.
        // Direct-mode client Path therefore must mint an item-scoped playback
        // token instead; swapping itemId on that Path is rejected below.
        var sessions = new FederationUserSessionTokenService();
        var playback = new FederationPlaybackTokenService();
        var sessionToken = sessions.Issue("friend-a", "viewer-7");
        var playbackToken = playback.Issue("item-1", "friend-a");

        Assert.True(sessions.TryValidate(sessionToken, out _, out _));
        Assert.False(playback.TryValidate(sessionToken, "item-1", out _));
        Assert.False(playback.TryValidate(sessionToken, "item-2", out _));
        Assert.False(playback.TryValidate(playbackToken, "item-2", out _));
        Assert.True(playback.TryValidate(playbackToken, "item-1", out _, out var purpose));
        Assert.True(FederationPlaybackTokenService.AllowsDirectStream(purpose, download: false));
    }
}
