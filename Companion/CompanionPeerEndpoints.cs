using System.Text.Json;

namespace FederationCompanion;

internal static class CompanionPeerEndpoints
{
    internal static void MapCompanionPeers(this WebApplication app)
    {
        app.MapPost("/api/companion/invite", (CompanionAddress body, CompanionConnections connections, CancellationToken ct)
            => OwnerAction(async () => await connections.InviteAsync(body.Url, ct), "Request sent. They can accept it in Companion → Friends & sharing."));
        app.MapPost("/api/companion/requests", (CompanionInvite body, CompanionConnections connections)
            => OwnerAction(async () => await connections.ReceiveAsync(body), "Request received."));
        app.MapGet("/api/companion/requests", (CompanionState state) =>
        {
            lock (state) return Results.Ok(state.CompanionRequests.Select(r => new
            { r.Id, r.Url, r.Outgoing, r.Status, r.Error, r.ExpiresUtc, name = r.Offer?.Name }).ToArray());
        });
        app.MapPost("/api/companion/check", (CompanionConnections connections, CancellationToken ct)
            => OwnerAction(async () => await connections.PollAsync(ct), "Friend requests refreshed."));
        app.MapPost("/api/companion/requests/{id}/accept", (string id, CompanionConnections connections)
            => OwnerAction(async () => await connections.AcceptAsync(id), "Connected. Choose incoming libraries, and check your Libraries to share switches."));
        app.MapPost("/api/companion/requests/{id}/decline", (string id, CompanionConnections connections)
            => OwnerAction(async () => await connections.RejectAsync(id), "Request declined."));
        app.MapPost("/api/companion/requests/{id}/cancel", (string id, CompanionConnections connections)
            => OwnerAction(async () => await connections.CancelAsync(id), "Request cancelled. Its sharing credential has been revoked."));
        app.MapGet("/api/companion/requests/{id}/result", (string id, HttpRequest request, CompanionConnections connections) =>
        {
            var result = connections.Result(id, request.Headers["X-Companion-Request"].ToString());
            return result == null ? Results.Unauthorized() : Results.Ok(result);
        });

        app.MapGet("/Plugins/Federation/Peer/Libraries", (HttpRequest request, PlexPeerProtocol protocol, CancellationToken ct)
            => PeerAction(request, protocol, async peer => Results.Json(new { Items = await protocol.LibrariesAsync(peer, ct) }, ProtocolJson)));
        app.MapGet("/Plugins/Federation/Peer/Items", (string parentId, string mediaType, int startIndex, int limit, HttpRequest request, PlexPeerProtocol protocol, CancellationToken ct)
            => PeerAction(request, protocol, async peer => Results.Json(new { Items = await protocol.ItemsAsync(peer, parentId, mediaType, startIndex, limit, ct) }, ProtocolJson)));
        app.MapPost("/Plugins/Federation/PlaybackToken", (PlexPlaybackRequest body, HttpRequest request, PlexPeerProtocol protocol, CancellationToken ct)
            => PeerAction(request, protocol, async peer =>
            {
                var grant = await protocol.MintAsync(peer, body.ItemId, ct);
                return Results.Ok(new { token = grant.Token, expiresUtc = grant.ExpiresUtc });
            }));
        app.MapMethods("/Plugins/Federation/DirectStream/{itemId}", new[] { "GET", "HEAD" }, async (string itemId, string? token, HttpContext context, PlexPeerProtocol protocol, CancellationToken ct) =>
        {
            try { await protocol.StreamAsync(itemId, token ?? "", context, ct); }
            catch (UnauthorizedAccessException) { if (!context.Response.HasStarted) context.Response.StatusCode = 403; }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or ArgumentException or InvalidOperationException)
            { if (!context.Response.HasStarted) context.Response.StatusCode = 502; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        });
    }

    private static readonly JsonSerializerOptions ProtocolJson = new();

    private static async Task<IResult> OwnerAction(Func<Task> action, string message)
    {
        try { await action(); return Results.Ok(new { message }); }
        catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        { return Results.BadRequest(new { error = "Could not reach their Companion. Check the HTTPS address and try again; both owners need the Plex-to-Plex preview." }); }
    }

    private static async Task<IResult> PeerAction(HttpRequest request, PlexPeerProtocol protocol, Func<CompanionPeer, Task<IResult>> action)
    {
        var peer = protocol.Authenticate(request.Headers["X-Federation-Token"].ToString());
        if (peer == null) return Results.Unauthorized();
        try { return await action(peer); }
        catch (UnauthorizedAccessException) { return Results.StatusCode(403); }
        catch (ArgumentException) { return Results.BadRequest(); }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
        { return Results.StatusCode(502); }
    }

    internal sealed record CompanionAddress(string Url);
    internal sealed record PlexPlaybackRequest(string ItemId);
}
