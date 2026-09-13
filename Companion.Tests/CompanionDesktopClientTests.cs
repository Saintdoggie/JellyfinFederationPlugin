using System.Net;
using System.Text;
using FederationCompanion;

namespace FederationCompanion.Tests;

public sealed class CompanionDesktopClientTests
{
    [Fact]
    public async Task NativeCommandsStayOnLoopback_AuthenticateAndPreserveSelection()
    {
        using var handler = new Handler(async request =>
        {
            Assert.Equal("127.0.0.1",request.RequestUri!.Host);
            Assert.Equal(7890,request.RequestUri.Port);
            Assert.Equal("test-owner",request.Headers.GetValues("X-Companion-Admin").Single());
            Assert.Equal(HttpMethod.Post,request.Method);
            Assert.Contains("\"libraryIds\":[\"one\"]",await request.Content!.ReadAsStringAsync());
            return Json("{\"message\":\"Saved\"}");
        });
        using var client = new CompanionDesktopClient(7890,"test-owner",handler);
        var result = await client.SendAsync("/api/import/peers/test/libraries",new { libraryIds=new[]{"one"} });
        Assert.Equal("Saved",result["message"]!.ToString());
    }
    [Fact]
    public async Task EmptySuccessfulResponseIsAccepted_ForDisconnectAndDecline()
    {
        using var client = new CompanionDesktopClient(7890,"test-owner",new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))));
        Assert.NotNull(await client.SendAsync("/api/pools/invites/test/reject",new {}));
    }
    [Theory]
    [InlineData("https://elsewhere.example/api/status")]
    [InlineData("//elsewhere.example/api/status")]
    [InlineData("/api/../outside")]
    [InlineData("/api/\\elsewhere")]
    public async Task RejectsNonlocalCommandPaths(string path)
    {
        using var handler = new Handler(_ => throw new Exception("Must not send any request."));
        using var client = new CompanionDesktopClient(7890,"test-owner",handler);
        await Assert.ThrowsAsync<ArgumentException>(()=>client.SendAsync(path));
    }
    [Fact]
    public async Task FailedCommandsSurfaceAnActionableMessage()
    {
        using var client = new CompanionDesktopClient(7890,"test-owner",new Handler(_ => Task.FromResult(Json("{\"error\":\"Refresh available libraries.\"}",HttpStatusCode.BadRequest))));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(()=>client.SendAsync("/api/status"));
        Assert.Equal("Refresh available libraries.",error.Message);
    }
    private static HttpResponseMessage Json(string json,HttpStatusCode status=HttpStatusCode.OK)=>new(status){Content=new StringContent(json,Encoding.UTF8,"application/json")};
    private sealed class Handler(Func<HttpRequestMessage,Task<HttpResponseMessage>> action):HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>action(request); }
}
