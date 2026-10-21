using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Jellyfin.Plugin.Federation.Api;
using Jellyfin.Plugin.Federation.Configuration;
using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

[Collection("PluginInstance")]
public class PlaybackTokenBatchTests
{
    [Fact]
    public void BatchRequest_WithOnlyItemIds_IsNotRejectedAsInvalidItemId()
    {
        using var plugin = new RealPluginInstance();
        plugin.Configuration.RemoteServers.Add(new RemoteServer { Enabled = true, IssuedApiKey = "abc123", FederationId = "fed-1" });

        var controller = (FederationController)RuntimeHelpers.GetUninitializedObject(typeof(FederationController));
        Set(controller, "_friends", RuntimeHelpers.GetUninitializedObject(typeof(FederationFriendService)));
        Set(controller, "_peerAccess", new FederationPeerAccessService(Mock.Of<ILibraryManager>()));

        var http = new DefaultHttpContext();
        http.Request.Headers[FederationTokenAuth.Header] = "abc123";
        controller.ControllerContext = new ControllerContext { HttpContext = http };

        var result = controller.IssuePlaybackToken(new IssuePlaybackTokenRequest
        {
            ItemIds = new List<string> { Guid.NewGuid().ToString("N") },
            Purpose = "Image"
        });

        // The unknown id yields a null token, but the batch itself succeeds (not a 400).
        Assert.IsType<OkObjectResult>(result);
    }

    private static void Set(object target, string field, object value)
        => target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
}
