using FederationCompanion;

var builder = WebApplication.CreateBuilder(args);

// A single shared HttpClient for both Plex.tv (account/sign-in) and the
// user's own Plex Media Server - this app makes a handful of requests per
// user action, never a sustained media stream, so one client is plenty.
builder.Services.AddSingleton(new HttpClient());
builder.Services.AddSingleton<PlexClient>();

// Loaded once at startup rather than per-request: every request in this app
// either reads or mutates the same single-user state, and concurrent writes
// already serialize through CompanionState's own save lock.
var state = await CompanionState.LoadAsync();
builder.Services.AddSingleton(state);
builder.Services.AddSingleton(sp => new PlexAuth(sp.GetRequiredService<HttpClient>(), state.ClientIdentifier));

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

// Tracks the in-flight sign-in attempt's PIN id between StartSignIn and
// PollSignIn - state a single-user local app can safely keep in memory
// rather than persisting, since it is meaningless after this process exits.
var pendingPinId = new PendingPin();

app.MapGet("/api/status", (CompanionState s) => Results.Ok(new
{
    signedIn = !string.IsNullOrEmpty(s.PlexAccountToken),
    serverConnected = !string.IsNullOrEmpty(s.ServerBaseUrl),
    serverName = s.ServerName,
    libraries = s.Libraries,
    peerCount = s.Peers.Count
}));

app.MapPost("/api/plex/start-signin", async (HttpRequest req, PlexAuth auth, CancellationToken ct) =>
{
    var appBaseUrl = $"{req.Scheme}://{req.Host}";
    var (pinId, signInUrl) = await auth.StartSignInAsync(appBaseUrl, ct).ConfigureAwait(false);
    pendingPinId.Id = pinId;
    return Results.Ok(new { signInUrl });
});

app.MapGet("/api/plex/poll-signin", async (PlexAuth auth, PlexClient plex, CompanionState s, CancellationToken ct) =>
{
    if (pendingPinId.Id is not { } pinId)
    {
        return Results.BadRequest(new { error = "No sign-in in progress. Start one first." });
    }

    var accountToken = await auth.TryCompleteSignInAsync(pinId, ct).ConfigureAwait(false);
    if (accountToken == null)
    {
        return Results.Ok(new { complete = false });
    }

    s.PlexAccountToken = accountToken;

    // Auto-picks the first server the account can reach. An account with
    // more than one server is an edge case not worth a picker UI for yet -
    // revisit if it turns out to matter in practice.
    var servers = await auth.GetOwnedServersAsync(accountToken, ct).ConfigureAwait(false);
    var server = servers.FirstOrDefault();
    if (server != null)
    {
        // Prefers a local connection over a relay one - a relay connection
        // routes through Plex's own servers, which works but adds latency
        // and depends on Plex's relay staying up; a direct connection (even
        // over Tailscale, which shows up here like any other reachable
        // address) does not.
        var connection = server.Connections.FirstOrDefault(c => c.Local) ?? server.Connections.FirstOrDefault();
        if (connection != null)
        {
            s.ServerBaseUrl = connection.Uri;
            s.ServerAccessToken = server.AccessToken;
            s.ServerName = server.Name;

            var libraries = await plex.GetSectionsAsync(connection.Uri, server.AccessToken, ct).ConfigureAwait(false);
            MergeLibraries(s, libraries);
        }
    }

    await s.SaveAsync().ConfigureAwait(false);
    pendingPinId.Id = null;
    return Results.Ok(new { complete = true, serverName = s.ServerName });
});

app.MapPost("/api/libraries/refresh", async (PlexClient plex, CompanionState s, CancellationToken ct) =>
{
    if (s.ServerBaseUrl == null || s.ServerAccessToken == null)
    {
        return Results.BadRequest(new { error = "Not connected to a Plex server yet." });
    }

    var libraries = await plex.GetSectionsAsync(s.ServerBaseUrl, s.ServerAccessToken, ct).ConfigureAwait(false);
    MergeLibraries(s, libraries);
    await s.SaveAsync().ConfigureAwait(false);
    return Results.Ok(s.Libraries);
});

app.MapPost("/api/libraries/toggle", async (ToggleLibraryRequest body, CompanionState s) =>
{
    var library = s.Libraries.FirstOrDefault(l => l.SectionKey == body.SectionKey);
    if (library == null)
    {
        return Results.NotFound(new { error = "Unknown library." });
    }

    library.Shared = body.Shared;
    await s.SaveAsync().ConfigureAwait(false);
    return Results.Ok(library);
});

app.Run();

static void MergeLibraries(CompanionState s, List<CompanionLibrary> fresh)
{
    // Preserves each existing library's Shared choice by key - re-scanning
    // must never silently reset what the user already decided to share.
    var existingByKey = s.Libraries.ToDictionary(l => l.SectionKey);
    foreach (var lib in fresh)
    {
        if (existingByKey.TryGetValue(lib.SectionKey, out var existing))
        {
            lib.Shared = existing.Shared;
        }
    }

    s.Libraries = fresh;
}

internal sealed class PendingPin
{
    public int? Id { get; set; }
}

internal sealed record ToggleLibraryRequest(string SectionKey, bool Shared);
