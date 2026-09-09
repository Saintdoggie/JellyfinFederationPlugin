using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using FederationCompanion;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

var launch = CompanionLaunchOptions.Parse(args);

// One owner process per machine. A second launch (double-clicking the
// shortcut while the tray app is running) asks the first process to open its
// dashboard and exits instead of starting a second listener and mount.
using var singleInstance = new SingleInstance();
if (!singleInstance.TryAcquire())
{
    if (launch.OpenBrowser)
    {
        SingleInstance.TrySignalExistingInstance(TimeSpan.FromSeconds(3), singleInstance.InstancePipeName);
    }

    return;
}

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = CompanionLaunchOptions.KestrelArgs(args),
    ContentRootPath = Directory.Exists(Path.Combine(AppContext.BaseDirectory, "wwwroot")) ? AppContext.BaseDirectory : null
});

builder.WebHost.ConfigureKestrel(options => options.AddServerHeader = false);

// A single shared HttpClient for both Plex.tv (account/sign-in) and the
// user's own Plex Media Server - this app makes a handful of requests per
// user action, never a sustained media stream, so one client is plenty.
builder.Services.AddSingleton(new HttpClient());
builder.Services.AddSingleton<PlexClient>();
builder.Services.AddSingleton(sp => new JellyfinImportService(
    sp.GetRequiredService<HttpClient>(),
    new HttpClient { Timeout = Timeout.InfiniteTimeSpan }));

// Loaded once at startup rather than per-request: every request in this app
// either reads or mutates the same single-user state, and concurrent writes
// already serialize through CompanionState's own save lock.
var state = await CompanionState.LoadAsync();
await state.SaveAsync();
builder.Services.AddSingleton(state);
var runtime = new CompanionRuntime { BackgroundLaunch = launch.Background };
builder.Services.AddSingleton(runtime);
builder.Services.AddSingleton<IAutostartRegistration>(_ =>
#if WINDOWS
    new WindowsAutostartRegistration()
#else
    OperatingSystem.IsLinux() ? new LinuxAutostartRegistration() : new UnsupportedAutostartRegistration()
#endif
);
builder.Services.AddSingleton(sp => new PlexFederationRelay(
    state,
    new HttpClient { Timeout = Timeout.InfiniteTimeSpan }));
builder.Services.AddSingleton(sp => new PlexAuth(sp.GetRequiredService<HttpClient>(), state.ClientIdentifier));
builder.Services.AddSingleton(sp => new CompanionUpdater(sp.GetRequiredService<HttpClient>()));
builder.Services.AddSingleton(sp => new RcloneBootstrapper(new HttpClient { Timeout = TimeSpan.FromMinutes(5) }));
builder.Services.AddSingleton(sp => new WinFspInstaller(new HttpClient { Timeout = TimeSpan.FromMinutes(2) }));

builder.Services.AddHostedService<ImportSyncBackgroundService>();
builder.Services.AddSingleton<LocalMediaMountService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<LocalMediaMountService>());

var app = builder.Build();

// Funnel exposes this listener publicly so peers can claim one-time links and
// Plex can fetch item-bound streams. All owner/admin APIs use a separate local
// access key; otherwise anyone who discovered the Funnel hostname could change
// library consent or mint a friend-scoped Plex relay connection.
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api")
        && !IsPublicCompanionApi(context.Request.Path))
    {
        var supplied = context.Request.Headers["X-Companion-Admin"].ToString();
        if (!FixedTimeEquals(supplied, state.AdminAccessKey))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Companion owner access is required." }).ConfigureAwait(false);
            return;
        }
    }

    await next().ConfigureAwait(false);
});

app.UseDefaultFiles();
app.UseStaticFiles();

app.Lifetime.ApplicationStarted.Register(() =>
{
    try
    {
        var addresses = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()
            ?.Addresses;
        foreach (var raw in addresses ?? Array.Empty<string>())
        {
            var candidate = raw.Replace("*", "127.0.0.1", StringComparison.Ordinal)
                .Replace("+", "127.0.0.1", StringComparison.Ordinal);
            if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && uri.Port > 0)
            {
                CompanionListen.Port = uri.Port;
                break;
            }
        }
    }
    catch (InvalidOperationException)
    {
    }

    // Console output is for the Linux/macOS/source builds. The Windows desktop
    // build has no console; it opens the dashboard itself and the tray offers
    // "Copy owner key" for the manual case. Never write the key to the log.
    Console.WriteLine();
    Console.WriteLine("Federation Companion owner access:");
    Console.WriteLine($"  Add this to the end of the Companion URL: #access={state.AdminAccessKey}");
    Console.WriteLine("  Keep this owner key private. It is not your Plex token.");
    Console.WriteLine();
});

// Tracks the in-flight sign-in attempt's PIN id between StartSignIn and
// PollSignIn - state a single-user local app can safely keep in memory
// rather than persisting, since it is meaningless after this process exits.
var pendingPinId = new PendingPin();

// One-time connect-code tokens, keyed by token, expiring after 15 minutes
// unclaimed. In-memory only and deliberately so: a code is meant to be
// generated and consumed within the same short window an admin is actively
// pasting it in, and losing an unclaimed one across a restart costs nothing
// - the user just generates a fresh one.
var pendingLinks = new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);

app.MapGet("/api/status", (CompanionState s) => Results.Ok(new
{
    signedIn = !string.IsNullOrEmpty(s.PlexAccountToken) || !string.IsNullOrEmpty(s.ServerAccessToken),
    serverConnected = !string.IsNullOrEmpty(s.ServerBaseUrl),
    serverName = s.ServerName,
    serverMachineIdentifier = s.ServerMachineIdentifier,
    libraries = s.Libraries.Select(l => new { l.SectionKey, l.Title, l.Type, shared = CompanionLibraryPolicy.IsShared(s, l), imported = CompanionLibraryPolicy.IsImported(s, l) }),
    peerCount = s.Peers.Count,
    importPeerCount = s.ImportPeers.Count,
    plexVisibleImportRoot = s.PlexVisibleImportRoot,
    playbackBaseUrl = s.PlaybackBaseUrl,
    federationPluginVersion = CompanionVersion.FederationPluginVersion(),
    poolInviteCount = s.IncomingPoolInvites.Count
}));

// Owner-facing app facts: which build/port is running, how long and how much
// memory it uses, where the log is, and whether the Windows sign-in entry is
// set. Never returns credentials.
app.MapGet("/api/app/info", (CompanionRuntime runtime, IAutostartRegistration autostart) => Results.Ok(new
{
    version = CompanionVersion.FederationPluginVersion(),
    revision = CompanionVersion.LocalRevision(),
    pid = Environment.ProcessId,
    port = runtime.Port,
    uptimeSeconds = (long)runtime.Uptime.TotalSeconds,
    workingSetMb = runtime.WorkingSetBytes / (1024 * 1024),
    installDirectory = runtime.InstallDirectory,
    logPath = runtime.LogPath,
    backgroundLaunch = runtime.BackgroundLaunch,
    windows = OperatingSystem.IsWindows(),
    autostartSupported = autostart.IsSupported,
    autostartEnabled = autostart.IsEnabled()
}));

app.MapPost("/api/app/autostart", (AutostartRequest body, IAutostartRegistration autostart) =>
{
    if (!autostart.IsSupported)
    {
        return Results.BadRequest(new { error = "Starting with the system is only available in the Windows desktop build." });
    }

    if (!autostart.Set(body.Enabled))
    {
        return Results.BadRequest(new { error = "Windows did not accept the sign-in setting." });
    }

    return Results.Ok(new { enabled = autostart.IsEnabled() });
});

app.MapPost("/api/app/open-dashboard", (CompanionState s, CompanionRuntime runtime) =>
    CompanionShell.OpenDashboard(runtime, s.AdminAccessKey)
        ? Results.Ok(new { opened = true })
        : Results.BadRequest(new { error = "Could not open a browser on the Companion machine." }));

app.MapPost("/api/app/exit", (IHostApplicationLifetime lifetime) =>
{
    // Reply before shutting down so the browser gets a clean response.
    _ = Task.Run(async () =>
    {
        await Task.Delay(300).ConfigureAwait(false);
        AppLog.Info("Exit requested from the dashboard.");
        lifetime.StopApplication();
    });
    return Results.Ok(new { stopping = true });
});

app.MapGet("/api/federation/info", () => Results.Ok(new
{
    kind = "PlexCompanion",
    federationPluginVersion = CompanionVersion.FederationPluginVersion(),
    companionRevision = CompanionVersion.LocalRevision()
}));

app.MapPost("/api/pools/invite", async (HttpRequest request, CompanionPoolInviteRequest body, CompanionState s) =>
{
    var peer = FindPeerByFederationToken(request, s);
    if (peer == null)
    {
        return Results.Unauthorized();
    }

    if (string.IsNullOrWhiteSpace(body.InviteId) || string.IsNullOrWhiteSpace(body.PoolId))
    {
        return Results.BadRequest(new { error = "Pool invite is missing an id." });
    }

    s.IncomingPoolInvites.RemoveAll(i => i.CreatedUtc < DateTime.UtcNow.AddHours(-24));
    if (s.IncomingPoolInvites.Any(i => i.InviteId == body.InviteId))
    {
        return Results.Ok();
    }

    while (s.IncomingPoolInvites.Count >= 25)
    {
        s.IncomingPoolInvites.RemoveAt(0);
    }

    s.IncomingPoolInvites.Add(new CompanionPoolInvite
    {
        InviteId = body.InviteId.Trim(),
        PoolId = body.PoolId.Trim(),
        PoolName = string.IsNullOrWhiteSpace(body.PoolName) ? "a pool" : body.PoolName.Trim(),
        OwnerName = body.OwnerName,
        FromFederationId = body.FromFederationId ?? string.Empty,
        CallbackUrl = body.CallbackUrl,
        CallbackToken = body.CallbackToken,
        FederationPluginVersion = body.FederationPluginVersion,
        Roster = (body.Roster ?? new List<CompanionPoolRosterWire>())
            .Select(m => new CompanionPoolRosterMember { Name = m.Name ?? string.Empty, Url = m.Url ?? string.Empty })
            .ToList(),
        CreatedUtc = DateTime.UtcNow
    });
    await s.SaveAsync().ConfigureAwait(false);
    return Results.Ok();
});

app.MapGet("/api/pools/invites", (CompanionState s) => Results.Ok(s.IncomingPoolInvites.Select(i => new
{
    i.InviteId,
    i.PoolName,
    i.OwnerName,
    i.FederationPluginVersion,
    memberCount = i.Roster.Count,
    i.CreatedUtc
})));

app.MapPost("/api/pools/invites/{id}/accept", async (string id, CompanionState s, HttpClient http, CancellationToken ct) =>
{
    var invite = s.IncomingPoolInvites.FirstOrDefault(i => i.InviteId == id);
    if (invite == null)
    {
        return Results.NotFound(new { error = "Pool invite not found." });
    }

    if (!string.IsNullOrWhiteSpace(invite.CallbackUrl) && !string.IsNullOrWhiteSpace(invite.CallbackToken))
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, invite.CallbackUrl.TrimEnd('/') + "/Plugins/Federation/Pools/AcceptNotice")
            {
                Content = JsonContent.Create(new { InviteId = invite.InviteId, FromFederationId = invite.FromFederationId })
            };
            req.Headers.TryAddWithoutValidation("X-Federation-Token", invite.CallbackToken);
            using var response = await http.SendAsync(req, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return Results.BadRequest(new { error = $"Could not confirm with the Jellyfin server (HTTP {(int)response.StatusCode}). Try again shortly." });
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return Results.BadRequest(new { error = "Could not reach the Jellyfin server to confirm. Check Funnel/public URL on their side." });
        }
    }

    s.IncomingPoolInvites.RemoveAll(i => i.InviteId == id);
    await s.SaveAsync().ConfigureAwait(false);

    return Results.Ok(new { message = $"You joined {invite.PoolName}." });
});

app.MapPost("/api/pools/invites/{id}/reject", async (string id, CompanionState s, HttpClient http, CancellationToken ct) =>
{
    var invite = s.IncomingPoolInvites.FirstOrDefault(i => i.InviteId == id);
    if (invite == null)
    {
        return Results.NotFound(new { error = "Pool invite not found." });
    }

    if (!string.IsNullOrWhiteSpace(invite.CallbackUrl) && !string.IsNullOrWhiteSpace(invite.CallbackToken))
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, invite.CallbackUrl.TrimEnd('/') + "/Plugins/Federation/Pools/RejectNotice")
            {
                Content = JsonContent.Create(new { InviteId = invite.InviteId, FromFederationId = invite.FromFederationId })
            };
            req.Headers.TryAddWithoutValidation("X-Federation-Token", invite.CallbackToken);
            await http.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch
        {
            // Clearing locally still stands if we cannot reach them.
        }
    }

    s.IncomingPoolInvites.RemoveAll(i => i.InviteId == id);
    await s.SaveAsync().ConfigureAwait(false);
    return Results.Ok();
});

app.MapPost("/api/diagnostics", async (CompanionState s, HttpClient http, CancellationToken ct) =>
{
    var library = s.Libraries.FirstOrDefault(l => CompanionLibraryPolicy.IsShared(s, l));
    if (library == null || s.ServerBaseUrl == null || s.ServerAccessToken == null)
        return Results.Ok(new[] { new ConnectionCheck("Plex", false, "Connect Plex and select a local library to share first.") });
    var checks = new List<ConnectionCheck>
    {
        await ConnectionDiagnostics.ProbeAsync(http, s.ServerBaseUrl, s.ServerAccessToken, library.SectionKey, library.Type, "Local Plex media", ct)
    };
    var peer = s.Peers.FirstOrDefault();
    if (peer != null && PlexRemoteEndpoint.IsPublicHttpsUrl(s.PublicUrl))
        checks.Add(await ConnectionDiagnostics.ProbeAsync(http, s.PublicUrl + "/plex/" + peer.Id, peer.AccessToken, library.SectionKey, library.Type, "Public media path", ct));
    else
        checks.Add(new("Public media path", false, "Connect a Jellyfin friend through Funnel to test the complete public media path."));
    return Results.Ok(checks);
});

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
    var server = servers.OrderByDescending(candidate => candidate.Owned).FirstOrDefault();
    if (server != null)
    {
        // This address is handed to a different machine (the Jellyfin friend),
        // so prefer Plex's public direct endpoint. A LAN-first choice can pass
        // every local catalog check yet make all remote playback fail.
        var (connection, libraries) = await ResolvePlexConnectionAsync(server, plex, ct).ConfigureAwait(false);
        if (connection != null)
        {
            CompanionLibraryPolicy.PrepareServerChange(s, server.MachineIdentifier);
            s.ServerBaseUrl = connection.Uri;
            s.ServerAccessToken = server.AccessToken;
            s.ServerName = server.Name;
            s.ServerMachineIdentifier = server.MachineIdentifier;

            MergeLibraries(s, libraries);
        }
    }

    await s.SaveAsync().ConfigureAwait(false);
    pendingPinId.Id = null;
    return Results.Ok(new { complete = true, serverName = s.ServerName });
});

app.MapGet("/api/plex/servers", async (PlexAuth auth, CompanionState s, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(s.PlexAccountToken))
    {
        return Results.BadRequest(new { error = "Sign in with Plex first." });
    }

    var servers = await auth.GetOwnedServersAsync(s.PlexAccountToken, ct).ConfigureAwait(false);
    return Results.Ok(servers.Select(server => new
    {
        id = server.MachineIdentifier,
        server.Name,
        server.Owned,
        selected = string.Equals(server.MachineIdentifier, s.ServerMachineIdentifier, StringComparison.Ordinal)
    }));
});

app.MapPost("/api/plex/servers/select", async (SelectPlexServerRequest body, PlexAuth auth, PlexClient plex, CompanionState s, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(s.PlexAccountToken))
    {
        return Results.BadRequest(new { error = "Sign in with Plex first." });
    }

    var servers = await auth.GetOwnedServersAsync(s.PlexAccountToken, ct).ConfigureAwait(false);
    var server = servers.FirstOrDefault(candidate => string.Equals(candidate.MachineIdentifier, body.Id, StringComparison.Ordinal));
    if (server == null)
    {
        return Results.NotFound(new { error = "That Plex server no longer has a usable connection." });
    }

    var (connection, libraries) = await ResolvePlexConnectionAsync(server, plex, ct).ConfigureAwait(false);
    if (connection == null)
    {
        return Results.BadRequest(new { error = "Plex reported this server, but none of its connection addresses answered." });
    }

    CompanionLibraryPolicy.PrepareServerChange(s, server.MachineIdentifier);
    s.ServerBaseUrl = connection.Uri;
    s.ServerAccessToken = server.AccessToken;
    s.ServerName = server.Name;
    s.ServerMachineIdentifier = server.MachineIdentifier;
    MergeLibraries(s, libraries);
    await s.SaveAsync().ConfigureAwait(false);

    return Results.Ok(new { server.Name });
});

app.MapPost("/api/plex/connect-local", async (ConnectLocalPlexRequest body, PlexClient plex, CompanionState s, CancellationToken ct) =>
{
    if (body.Url == null || !Uri.TryCreate(body.Url, UriKind.Absolute, out var parsed)
        || (parsed.Scheme != "http" && parsed.Scheme != "https")
        || string.IsNullOrWhiteSpace(body.Token))
    {
        return Results.BadRequest(new { error = "Plex server address and token are both required." });
    }

    var url = body.Url.TrimEnd('/');
    try
    {
        var libraries = await plex.GetSectionsAsync(url, body.Token.Trim(), ct).ConfigureAwait(false);
        var machineIdentifier = await plex.GetMachineIdentifierAsync(url, body.Token.Trim(), ct).ConfigureAwait(false);
        CompanionLibraryPolicy.PrepareServerChange(s, machineIdentifier);
        s.ServerMachineIdentifier = machineIdentifier;
        s.PlexAccountToken = null;
        s.ServerBaseUrl = url;
        s.ServerAccessToken = body.Token.Trim();
        s.ServerName = await plex.GetServerNameAsync(url, body.Token.Trim(), ct).ConfigureAwait(false) ?? "Plex Media Server";
        MergeLibraries(s, libraries);
        await s.SaveAsync().ConfigureAwait(false);
        return Results.Ok(new { serverName = s.ServerName, libraries = s.Libraries });
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
    {
        return Results.BadRequest(new { error = "Could not reach that Plex server with the token provided." });
    }
});

app.MapPost("/api/playback-url", async (SetPublicUrlRequest body, CompanionState s) =>
{
    if (string.IsNullOrWhiteSpace(body.Url))
    {
        s.PlaybackBaseUrl = null;
        await s.SaveAsync().ConfigureAwait(false);
        return Results.Ok(new { playbackBaseUrl = s.PlaybackBaseUrl });
    }

    if (!Uri.TryCreate(body.Url, UriKind.Absolute, out var parsed)
        || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
        || !string.IsNullOrEmpty(parsed.UserInfo) || !string.IsNullOrEmpty(parsed.Query) || !string.IsNullOrEmpty(parsed.Fragment) || body.Url.Any(char.IsControl))
    {
        return Results.BadRequest(new { error = "Enter the http(s) address Plex should call back to this app on Play." });
    }

    s.PlaybackBaseUrl = parsed.AbsoluteUri.TrimEnd('/');
    await s.SaveAsync().ConfigureAwait(false);
    return Results.Ok(new { playbackBaseUrl = s.PlaybackBaseUrl });
});

app.MapPost("/api/plex-visible-root", async (SetPublicUrlRequest body, CompanionState s) =>
{
    s.PlexVisibleImportRoot = string.IsNullOrWhiteSpace(body.Url) ? null : body.Url.Trim().TrimEnd('/', '\\').Replace('\\', '/');
    await s.SaveAsync().ConfigureAwait(false);
    return Results.Ok(new { plexVisibleImportRoot = s.PlexVisibleImportRoot });
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

    if (body.Shared && CompanionLibraryPolicy.IsImported(s, library))
        return Results.BadRequest(new { error = "Imported libraries belong to a friend and cannot be shared onward." });
    library.Shared = body.Shared;
    await s.SaveAsync().ConfigureAwait(false);
    return Results.Ok(library);
});

app.MapGet("/api/tailscale/status", async (CancellationToken ct) =>
{
    var status = await TailscaleHelper.CheckAsync(ct).ConfigureAwait(false);
    return Results.Ok(status);
});

app.MapGet("/api/public-url", (CompanionState s) => Results.Ok(new { publicUrl = s.PublicUrl }));

app.MapPost("/api/public-url", async (SetPublicUrlRequest body, CompanionState s) =>
{
    // Empty is allowed: Funnel is optional when Plex Remote Access / Relay
    // already gives friends a public path. Once a Funnel URL is saved, generate
    // used to prefer claim codes forever because this endpoint rejected blank.
    if (string.IsNullOrWhiteSpace(body.Url))
    {
        s.PublicUrl = null;
        await s.SaveAsync().ConfigureAwait(false);
        return Results.Ok(new { publicUrl = (string?)null });
    }

    if (!PlexRemoteEndpoint.IsPublicHttpsUrl(body.Url))
    {
        return Results.BadRequest(new { error = "Enter a public https:// address your Jellyfin friend can reach without joining your Tailscale, or leave this blank to use Plex Remote Access/Relay only. A Tailscale Funnel URL (https://name.ts.net) works; a 100.x or LAN address does not." });
    }

    var previousUrl = s.PublicUrl;
    s.PublicUrl = body.Url.Trim().TrimEnd('/');
    foreach (var peer in s.ImportPeers.Where(p => string.IsNullOrWhiteSpace(p.PlaybackBaseUrl)
        || string.Equals(p.PlaybackBaseUrl, previousUrl, StringComparison.OrdinalIgnoreCase)))
    {
        peer.PlaybackBaseUrl = s.PublicUrl;
    }
    await s.SaveAsync().ConfigureAwait(false);
    return Results.Ok(new { publicUrl = s.PublicUrl });
});

app.MapPost("/api/connect/generate", async (CompanionState s, PlexAuth auth, HttpClient http, CancellationToken ct, bool usePlexRemoteAccess = false) =>
{
    if (s.ServerBaseUrl == null || s.ServerAccessToken == null)
    {
        return Results.BadRequest(new { error = "Connect to Plex first." });
    }

    var remotePlexUrl = await ResolveFriendFacingPlexUrlAsync(s, auth, ct).ConfigureAwait(false);
    var companionUrl = await CompanionFunnelIfReachableAsync(http, s, ct).ConfigureAwait(false);
    if (!ConnectCodeFactory.TryGenerate(
            companionUrl,
            remotePlexUrl,
            s.ServerAccessToken,
            s.ServerName,
            s.Libraries.Where(l => CompanionLibraryPolicy.IsShared(s, l)),
            () => Convert.ToHexString(RandomNumberGenerator.GetBytes(24)),
            funnelExpected: PlexRemoteEndpoint.IsPublicHttpsUrl(s.PublicUrl),
            usePlexRemoteAccess,
            out var generated,
            out var generateError)
        || generated == null)
    {
        return Results.BadRequest(new { error = generateError });
    }

    // Funnel must be this Companion. A miss used to fall through to a direct
    // code that embeds the standing PMS token; revoking the Companion peer
    // does not revoke that token. Direct Plex is only when Funnel is unset or
    // the owner explicitly chooses Plex Remote Access.
    object payload;
    if (generated.Claim)
    {
        pendingLinks[generated.Token] = DateTime.UtcNow.AddMinutes(15);
        payload = new
        {
            url = generated.Url,
            token = generated.Token,
            name = generated.Name,
            claim = true,
            fallbackUrl = generated.FallbackUrl,
            fallbackToken = generated.FallbackToken,
            libraries = generated.Libraries
        };
    }
    else
    {
        payload = new
        {
            url = generated.Url,
            token = generated.Token,
            name = generated.Name,
            claim = false,
            libraries = generated.Libraries
        };
    }

    var code = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)));
    return Results.Ok(new { code, expiresInMinutes = 15, mode = generated.Mode });
});

app.MapPost("/api/connect/invite", async (InviteFriendRequest body, CompanionState s, PlexAuth auth, HttpClient http, CancellationToken ct) =>
{
    if (s.ServerBaseUrl == null || s.ServerAccessToken == null)
    {
        return Results.BadRequest(new { error = "Connect to Plex first." });
    }

    if (!TryNormalizeJellyfinInviteUrl(body.Url, out var jellyfinUrl))
    {
        return Results.BadRequest(new { error = "Enter your friend's Jellyfin address, like https://their-server.ts.net or https://jellyfin.example.com:8096." });
    }

    var remotePlexUrl = await ResolveFriendFacingPlexUrlAsync(s, auth, ct).ConfigureAwait(false);
    var companionUrl = await CompanionFunnelIfReachableAsync(http, s, ct).ConfigureAwait(false);
    if (!ConnectCodeFactory.TryGenerate(
            companionUrl,
            remotePlexUrl,
            s.ServerAccessToken,
            s.ServerName,
            s.Libraries.Where(l => CompanionLibraryPolicy.IsShared(s, l)),
            () => Convert.ToHexString(RandomNumberGenerator.GetBytes(24)),
            funnelExpected: PlexRemoteEndpoint.IsPublicHttpsUrl(s.PublicUrl),
            body.UsePlexRemoteAccess,
            out var generated,
            out var generateError)
        || generated == null)
    {
        return Results.BadRequest(new { error = generateError });
    }

    object payload;
    if (generated.Claim)
    {
        pendingLinks[generated.Token] = DateTime.UtcNow.AddMinutes(15);
        payload = new
        {
            url = generated.Url,
            token = generated.Token,
            name = generated.Name,
            claim = true,
            fallbackUrl = generated.FallbackUrl,
            fallbackToken = generated.FallbackToken,
            libraries = generated.Libraries
        };
    }
    else
    {
        payload = new
        {
            url = generated.Url,
            token = generated.Token,
            name = generated.Name,
            claim = false,
            libraries = generated.Libraries
        };
    }

    var code = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)));
    try
    {
        using var response = await http.PostAsJsonAsync(
            jellyfinUrl + "/Plugins/Federation/PlexOffers",
            new { code },
            ct).ConfigureAwait(false);
        var bodyText = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var remoteError = TryReadJsonError(bodyText);
            return Results.BadRequest(new { error = remoteError ?? "That Jellyfin server did not accept the request. Check the address, that Federation is installed, and that it is reachable from here." });
        }

        return Results.Ok(new
        {
            message = $"Share request sent to {jellyfinUrl}. They will see it under Federation → Companion and can Accept. You do not need to send them a code.",
            mode = generated.Mode
        });
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
    {
        return Results.BadRequest(new { error = $"Could not reach {jellyfinUrl}. Use a public https:// address (Funnel or port-forward), not a 100.x Tailscale address unless you share a tailnet." });
    }
});

app.MapGet("/api/update/status", async (CompanionUpdater updater, CancellationToken ct) =>
{
    var status = await updater.CheckAsync(ct).ConfigureAwait(false);
    return Results.Ok(new
    {
        installedBuild = status.InstalledBuild,
        updateAvailable = status.UpdateAvailable,
        localRevision = status.LocalRevision,
        remoteRevision = status.RemoteRevision,
        error = status.Error
    });
});

app.MapPost("/api/update/apply", async (CompanionUpdater updater, IHostApplicationLifetime lifetime, CancellationToken ct) =>
{
    var (success, message) = await updater.ApplyAsync(ct).ConfigureAwait(false);
    if (!success)
    {
        return Results.BadRequest(new { error = message });
    }

    // Graceful stop lets the mount service close rclone.exe so the restarter
    // can replace it. Environment.Exit skipped that and left the file locked.
    _ = Task.Run(async () =>
    {
        await Task.Delay(800).ConfigureAwait(false);
        lifetime.StopApplication();
    });
    return Results.Ok(new { message });
});

app.MapPost("/api/tailscale/funnel", async (CompanionState s, CancellationToken ct) =>
{
    var port = CompanionListen.Port.GetValueOrDefault();
    if (port <= 0)
    {
        return Results.BadRequest(new { error = "Companion has not bound a local port yet. Wait a second and try again." });
    }

    var result = await TailscaleHelper.SetUpFunnelAsync(port, ct).ConfigureAwait(false);
    if (!result.Success || string.IsNullOrWhiteSpace(result.FunnelUrl))
    {
        return Results.BadRequest(new { error = result.Message });
    }

    s.PublicUrl = result.FunnelUrl.TrimEnd('/');
    await s.SaveAsync().ConfigureAwait(false);
    return Results.Ok(new { funnelUrl = s.PublicUrl, message = result.Message });
});

app.MapPost("/api/link/complete", async (LinkCompleteRequest body, CompanionState s, PlexAuth auth, CancellationToken ct) =>
{
    if (!pendingLinks.TryRemove(body.Token, out var expiry) || expiry < DateTime.UtcNow)
    {
        return Results.BadRequest(new { error = "This connect code is invalid or has expired. Generate a new one." });
    }

    if (!PlexRemoteEndpoint.IsPublicHttpsUrl(s.PublicUrl)
        || string.IsNullOrWhiteSpace(s.ServerBaseUrl)
        || string.IsNullOrWhiteSpace(s.ServerAccessToken))
    {
        return Results.Conflict(new { error = "Companion is not ready to share a Plex server yet. Set a public Funnel URL, or generate a direct Plex Remote Access/Relay code instead." });
    }

    var requesterName = body.RequesterName?.Trim();
    var peer = new CompanionPeer
    {
        Name = string.IsNullOrWhiteSpace(requesterName)
            ? "Unnamed Jellyfin server"
            : requesterName[..Math.Min(requesterName.Length, 160)]
    };
    s.Peers.Add(peer);
    await s.SaveAsync().ConfigureAwait(false);

    var remotePlexUrl = await ResolveFriendFacingPlexUrlAsync(s, auth, ct).ConfigureAwait(false);
    var share = ConnectCodeFactory.FriendFacingShare(
        remotePlexUrl,
        s.ServerAccessToken,
        s.PublicUrl,
        peer.Id,
        peer.AccessToken);

    return Results.Ok(new
    {
        // Funnel carries the scoped relay credential; the Plex token stays local.
        plexUrl = share.PlexUrl,
        plexToken = share.PlexToken,
        serverName = s.ServerName,
        libraries = s.Libraries.Where(l => CompanionLibraryPolicy.IsShared(s, l)).Select(l => new { l.SectionKey, l.Title, l.Type })
    });
});

app.MapGet("/api/peers", (CompanionState s) => Results.Ok(s.Peers.Select(PeerView)));

app.MapDelete("/api/peers/{id}", async (string id, CompanionState s, PlexFederationRelay relay) =>
{
    var removed = s.Peers.RemoveAll(p => p.Id == id) > 0;
    if (!removed)
    {
        return Results.NotFound();
    }

    relay.ForgetPeer(id);
    await s.SaveAsync().ConfigureAwait(false);
    return Results.Ok();
});

app.MapPost("/api/peers/{id}/download-access", async (string id, SetPeerDownloadAccessRequest body, CompanionState s) =>
{
    var peer = s.Peers.FirstOrDefault(p => p.Id == id);
    if (peer == null)
    {
        return Results.NotFound(new { error = "Friend not found." });
    }

    peer.AllowDownloads = body.AllowDownloads;
    peer.AllowBulkDownloads = body.AllowDownloads && body.AllowBulkDownloads;
    await s.SaveAsync().ConfigureAwait(false);
    return Results.Ok(PeerView(peer));
});

app.MapGet("/api/import/peers", (CompanionState s) => Results.Ok(s.ImportPeers.Select(ImportPeerView)));

app.MapPost("/api/import/connect", async (ImportConnectRequest body, HttpRequest request, CompanionState s, JellyfinImportService jellyfin, PlexClient plex, ILogger<Program> logger, CancellationToken ct) =>
{
    ConnectCodePayload? decoded;
    try
    {
        decoded = JsonSerializer.Deserialize<ConnectCodePayload>(
            Convert.FromBase64String(body.Code ?? string.Empty),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }
    catch (Exception ex) when (ex is FormatException or JsonException)
    {
        return Results.BadRequest(new { error = "That code could not be read - check it was copied in full." });
    }

    if (decoded == null || string.IsNullOrWhiteSpace(decoded.Url) || string.IsNullOrWhiteSpace(decoded.Token))
    {
        return Results.BadRequest(new { error = "That code is missing an address or token." });
    }

    if (!IsSafePeerUrl(decoded.Url, out var peerUrl))
    {
        return Results.BadRequest(new { error = "The Jellyfin address must use https:// (http:// is allowed only for a loopback development server)." });
    }

    if (s.ImportPeers.Any(p => string.Equals(p.Url, peerUrl, StringComparison.OrdinalIgnoreCase)
        && string.Equals(p.Token, decoded.Token, StringComparison.Ordinal)))
    {
        return Results.Conflict(new { error = "That Jellyfin friend is already connected." });
    }

    List<PeerLibrary> availableLibraries;
    // Validate the code before persisting it. A typo or expired/revoked token
    // should not leave a permanently broken peer row behind.
    try
    {
        availableLibraries = await jellyfin.GetLibrariesAsync(peerUrl, decoded.Token, ct).ConfigureAwait(false);
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
    {
        logger.LogWarning(ex, "[Companion] Rejected a Jellyfin connect code because its catalog could not be reached");
        return Results.BadRequest(new { error = "The Jellyfin server could not verify this code. Check its public address and generate a fresh code." });
    }

    if (body.LibraryIds == null || body.LibraryIds.Count == 0
        || body.LibraryIds.Any(id => !availableLibraries.Any(l => l.Id == id)))
        return Results.BadRequest(new { error = "Choose the libraries to import before connecting." });

    var importName = decoded.Name?.Trim();
    var peer = new JellyfinImportPeer
    {
        Name = string.IsNullOrWhiteSpace(importName)
            ? peerUrl
            : importName[..Math.Min(importName.Length, 160)],
        Url = peerUrl,
        Token = decoded.Token,
        SelectedLibraryIds = body.LibraryIds.Distinct().ToList(),
        AvailableLibraries = availableLibraries,
        PlaybackBaseUrl = !string.IsNullOrWhiteSpace(s.PublicUrl)
            ? s.PublicUrl
            : $"{request.Scheme}://{request.Host}"
    };
    peer.ExportPath = Path.Combine(AppContext.BaseDirectory, "imported", peer.Id);

    s.ImportPeers.Add(peer);
    await s.SaveAsync().ConfigureAwait(false);

    // Immediate first sync so the admin sees results right away rather than
    // waiting up to 30 minutes for the background timer's first tick.
    await ImportSyncCoordinator.SyncOneAsync(s, peer, jellyfin, plex, logger, ct).ConfigureAwait(false);

    if (peer.LastError == null && !string.IsNullOrWhiteSpace(s.PlexMountRoot) && !string.IsNullOrEmpty(s.ServerBaseUrl) && !string.IsNullOrEmpty(s.ServerAccessToken))
    {
        try
        {
            await PlexImportManager.AttachAsync(s, plex, peer, ct).ConfigureAwait(false);
            await s.SaveAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Companion] Imported {Peer} but could not add Plex libraries automatically", peer.Name);
            peer.LastError = "Catalog saved. Check the media mount, then choose Add to Plex.";
            await s.SaveAsync().ConfigureAwait(false);
        }
    }

    return Results.Ok(ImportPeerView(peer));
});

app.MapPost("/api/import/peers/{id}/add-to-plex", async (string id, CompanionState s, PlexClient plex, CancellationToken ct) =>
{
    var peer = s.ImportPeers.FirstOrDefault(p => p.Id == id);
    if (peer == null)
    {
        return Results.NotFound();
    }

    try
    {
        await PlexImportManager.AttachAsync(s, plex, peer, ct).ConfigureAwait(false);
        await s.SaveAsync().ConfigureAwait(false);
        return Results.Ok(ImportPeerView(peer));
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

// Retire the old unsigned redirect: it allowed arbitrary peer/item reads.
app.MapMethods("/import-stream/{peerId}/{itemId}", new[] { "GET", "HEAD" },
    () => Results.StatusCode(StatusCodes.Status410Gone));

app.MapPost("/api/import/peers/{id}/plex-section", async (string id, SetPlexSectionRequest body, CompanionState s) =>
{
    var peer = s.ImportPeers.FirstOrDefault(p => p.Id == id);
    if (peer == null)
    {
        return Results.NotFound();
    }

    peer.PlexSectionKey = string.IsNullOrWhiteSpace(body.SectionKey) ? null : body.SectionKey;
    await s.SaveAsync().ConfigureAwait(false);
    return Results.Ok(ImportPeerView(peer));
});

app.MapPost("/api/import/peers/{id}/sync", async (string id, CompanionState s, JellyfinImportService jellyfin, PlexClient plex, ILogger<Program> logger, CancellationToken ct) =>
{
    var peer = s.ImportPeers.FirstOrDefault(p => p.Id == id);
    if (peer == null)
    {
        return Results.NotFound();
    }

    await ImportSyncCoordinator.SyncOneAsync(s, peer, jellyfin, plex, logger, ct).ConfigureAwait(false);
    return Results.Ok(ImportPeerView(peer));
});

app.MapDelete("/api/import/peers/{id}", async (string id, bool? removeFiles, CompanionState s, CancellationToken ct) =>
{
    var peer = s.ImportPeers.FirstOrDefault(p => p.Id == id);
    if (peer == null) return Results.NotFound();
    await ImportSyncCoordinator.RemoveAsync(s, peer, removeFiles == true, ct).ConfigureAwait(false);
    return Results.Ok(new { removed = true });
});

app.MapPost("/api/import/preview", async (ImportConnectRequest body, JellyfinImportService jellyfin, CancellationToken ct) =>
{
    try
    {
        var decoded = JsonSerializer.Deserialize<ConnectCodePayload>(Convert.FromBase64String(body.Code ?? ""),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (decoded == null || string.IsNullOrWhiteSpace(decoded.Token) || !IsSafePeerUrl(decoded.Url ?? "", out var url))
            return Results.BadRequest(new { error = "The code needs a valid Jellyfin address and token." });
        var libraries = await jellyfin.GetLibrariesAsync(url, decoded.Token, ct).ConfigureAwait(false);
        return Results.Ok(new { name = decoded.Name, libraries = libraries.Select(l => new { l.Id, l.Name, l.CollectionType }) });
    }
    catch (Exception ex) when (ex is FormatException or JsonException or HttpRequestException or TaskCanceledException)
    {
        return Results.BadRequest(new { error = "Could not read this friend's libraries. Check the address and ask for a fresh code." });
    }
});

app.MapGet("/api/import/peers/{id}/catalog", (string id, string? search, CompanionState s) =>
{
    var peer = s.ImportPeers.FirstOrDefault(p => p.Id == id);
    if (peer == null) return Results.NotFound();
    var items = peer.ImportCatalog.Where(i => string.IsNullOrWhiteSpace(search)
        || i.Title.Contains(search, StringComparison.OrdinalIgnoreCase)
        || (i.Series?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false))
        .OrderBy(i => i.Series).ThenBy(i => i.Season).ThenBy(i => i.Episode).ThenBy(i => i.Title).ToList();
    return Results.Ok(new { total = items.Count, items = items.Take(200), issueCount = peer.ImportCatalog.Count(i => i.Issue != null) });
});

app.MapPost("/api/import/peers/{id}/libraries", async (string id, SelectImportLibrariesRequest body, CompanionState s, JellyfinImportService jellyfin, PlexClient plex, ILogger<Program> logger, CancellationToken ct) =>
{
    var peer = s.ImportPeers.FirstOrDefault(p => p.Id == id);
    if (peer == null) return Results.NotFound();
    var libraries = await jellyfin.GetLibrariesAsync(peer.Url, peer.Token, ct).ConfigureAwait(false);
    if (body.LibraryIds == null || body.LibraryIds.Any(id => !libraries.Any(l => l.Id == id)))
        return Results.BadRequest(new { error = "Refresh the library list and choose currently shared libraries." });
    peer.SelectedLibraryIds = body.LibraryIds.Distinct().ToList();
    await ImportSyncCoordinator.SyncOneAsync(s, peer, jellyfin, plex, logger, ct).ConfigureAwait(false);
    return Results.Ok(ImportPeerView(peer));
});

app.MapMethods("/media/{**path}", new[] { "GET", "HEAD", "OPTIONS", "PROPFIND" },
    (string? path, HttpContext ctx, CompanionState s, JellyfinImportService jellyfin, CancellationToken ct)
        => MediaMount.HandleAsync(s, jellyfin, ctx, path, ct));

app.MapGet("/api/media-mount/status", (CompanionState s, LocalMediaMountService mount) => Results.Ok(new
{
    ready = MediaMount.IsMounted(s.MediaMountRoot, s.ClientIdentifier), s.MediaMountRoot, s.PlexMountRoot,
    s.AutoStartMediaMount, mount.Message, windows = OperatingSystem.IsWindows(),
    driverReady = FilesystemDriver.IsAvailable(),
    driverName = FilesystemDriver.Name,
    driverHelpUrl = FilesystemDriver.HelpUrl,
    helperReady = mount.HelperReady
}));

app.MapPost("/api/media-mount/start", async (LocalMediaMountService mount, CancellationToken ct) =>
    Results.Ok(new { ready = await mount.StartMountAsync(ct), message = mount.Message }));

app.MapPost("/api/media-mount/stop", async (LocalMediaMountService mount, CancellationToken ct) =>
{
    var stopped = await mount.StopMountAsync(ct).ConfigureAwait(false);
    return Results.Ok(new { running = mount.HasOwnedProcess, stopped, message = mount.Message });
});

app.MapGet("/api/media-mount/config", (HttpRequest request, HttpResponse response, CompanionState s) =>
{
    response.Headers.CacheControl = "no-store";
    var baseUrl = s.PlaybackBaseUrl ?? $"{request.Scheme}://{request.Host}{request.PathBase}";
    var config = $"[companion]\ntype = webdav\nurl = {baseUrl.TrimEnd('/')}/media/\nvendor = other\nbearer_token = {s.MediaAccessKey}\n";
    return Results.File(Encoding.UTF8.GetBytes(config), "text/plain", "companion-rclone.conf");
});

app.MapPost("/api/media-mount", async (SetMediaMountRequest body, CompanionState s) =>
{
    if (string.IsNullOrWhiteSpace(body.LocalPath) || !Path.IsPathFullyQualified(body.LocalPath)
        || string.IsNullOrWhiteSpace(body.PlexPath) || body.PlexPath.Any(char.IsControl))
        return Results.BadRequest(new { error = "Enter the mounted folder path as Companion and Plex see it." });
    if (!MediaMount.IsMounted(body.LocalPath, s.ClientIdentifier))
        return Results.BadRequest(new { error = "The media mount is not readable at that folder. Start the mount, then retry." });
    s.AutoStartMediaMount = false;
    s.MediaMountRoot = body.LocalPath;
    s.PlexMountRoot = body.PlexPath;
    await s.SaveAsync().ConfigureAwait(false);
    return Results.Ok(new { ready = true });
});

// Plex-compatible, revocable facade consumed by the Jellyfin plugin. It is
// deliberately outside /api because the caller is another server, not the
// owner browser; authentication uses this friend's distinct relay token.
app.MapMethods("/plex/{peerId}/{**path}", new[] { "GET", "HEAD" }, async (
    string peerId,
    string? path,
    HttpRequest request,
    HttpResponse response,
    CompanionState s,
    PlexFederationRelay relay,
    CancellationToken ct) =>
{
    var peer = s.Peers.FirstOrDefault(p => string.Equals(p.Id, peerId, StringComparison.Ordinal));
    var supplied = request.Headers["X-Plex-Token"].ToString();
    if (string.IsNullOrEmpty(supplied))
    {
        supplied = request.Query["X-Plex-Token"].ToString();
    }

    if (peer == null || !FixedTimeEquals(supplied, peer.AccessToken))
    {
        response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

    try
    {
        await relay.RelayAsync(peer, path, request, response, ct).ConfigureAwait(false);
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    {
        // The remote player sought elsewhere or disconnected.
    }
    catch (HttpRequestException)
    {
        if (!response.HasStarted)
        {
            response.StatusCode = StatusCodes.Status502BadGateway;
        }
    }
});

// Stable, item-bound relay consumed by Plex .strm files. The capability is an
// HMAC over this exact peer+item pair; neither the standing federation token
// nor the freshly minted upstream playback token is exposed to Plex/browser.
app.MapMethods("/stream/{peerId}/{itemId}", new[] { "GET", "HEAD" }, async (
    string peerId,
    string itemId,
    string? cap,
    HttpRequest request,
    HttpResponse response,
    CompanionState s,
    JellyfinImportService jellyfin,
    CancellationToken ct) =>
{
    var peer = s.ImportPeers.FirstOrDefault(p => string.Equals(p.Id, peerId, StringComparison.Ordinal));
    if (peer == null || !Guid.TryParse(itemId, out _) || !JellyfinImportService.IsValidStreamCapability(peer, itemId, cap))
    {
        response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }

    try
    {
        await jellyfin.RelayStreamAsync(peer, itemId, request, response, ct).ConfigureAwait(false);
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    {
        // The player sought elsewhere or disconnected; no error body is useful.
    }
    catch (HttpRequestException)
    {
        if (!response.HasStarted)
        {
            response.StatusCode = StatusCodes.Status502BadGateway;
        }
    }
});

await app.StartAsync().ConfigureAwait(false);

runtime.Port = CompanionListen.Port ?? runtime.Port;
AppLog.Info($"Companion {CompanionVersion.FederationPluginVersion()} started on port {runtime.Port} (pid {Environment.ProcessId}, background: {runtime.BackgroundLaunch}).");

// A second launch asks this process to open the dashboard through the pipe.
singleInstance.StartListener(() =>
{
    AppLog.Info("Opening the dashboard for a second launch.");
    CompanionShell.OpenDashboard(runtime, state.AdminAccessKey);
});

if (launch.OpenBrowser)
{
    CompanionShell.OpenDashboard(runtime, state.AdminAccessKey);
}

#if WINDOWS
var mountService = app.Services.GetRequiredService<LocalMediaMountService>();
var autostartRegistration = app.Services.GetRequiredService<IAutostartRegistration>();
WindowsCompanionTray.Start(state, mountService, autostartRegistration, runtime, app.Lifetime);
#endif

await app.WaitForShutdownAsync().ConfigureAwait(false);
AppLog.Info("Companion stopped.");

static async Task<string?> CompanionFunnelIfReachableAsync(HttpClient http, CompanionState s, CancellationToken cancellationToken)
    => await CompanionSelfCheck.PublicUrlIsThisCompanionAsync(http, s.PublicUrl, cancellationToken).ConfigureAwait(false)
        ? s.PublicUrl
        : null;

static async Task<string?> ResolveFriendFacingPlexUrlAsync(CompanionState s, PlexAuth auth, CancellationToken cancellationToken)
{
    IEnumerable<PlexConnection> connections = Enumerable.Empty<PlexConnection>();
    if (!string.IsNullOrWhiteSpace(s.PlexAccountToken))
    {
        try
        {
            var servers = await auth.GetOwnedServersAsync(s.PlexAccountToken, cancellationToken).ConfigureAwait(false);
            var server = servers.FirstOrDefault(candidate =>
                    !string.IsNullOrWhiteSpace(s.ServerName)
                    && string.Equals(candidate.Name, s.ServerName, StringComparison.OrdinalIgnoreCase))
                ?? servers.FirstOrDefault();
            if (server != null)
            {
                connections = server.Connections;
            }
        }
        catch (Exception)
        {
            // plex.tv being briefly unreachable must not block a claim if we
            // already know a public connection from sign-in.
        }
    }

    var selected = PlexRemoteEndpoint.SelectFriendFacing(connections);
    if (selected != null)
    {
        return selected.Uri.TrimEnd('/');
    }

    if (!string.IsNullOrWhiteSpace(s.ServerBaseUrl) && !PlexRemoteEndpoint.IsLanOrPrivateHost(s.ServerBaseUrl))
    {
        return s.ServerBaseUrl.TrimEnd('/');
    }

    return null;
}

static void MergeLibraries(CompanionState s, List<CompanionLibrary> fresh)
{
    // Preserves each existing library's Shared choice by key - re-scanning
    // must never silently reset what the user already decided to share.
    var existingByKey = s.Libraries.ToDictionary(l => l.SectionKey);
    foreach (var lib in fresh)
    {
        if (existingByKey.TryGetValue(lib.SectionKey, out var existing))
        {
            lib.Shared = existing.Shared && !CompanionLibraryPolicy.IsImported(s, lib);
        }
    }

    s.Libraries = fresh;
}

static async Task<(PlexConnection? Connection, List<CompanionLibrary> Libraries)> ResolvePlexConnectionAsync(
    PlexResource server,
    PlexClient plex,
    CancellationToken cancellationToken)
{
    foreach (var candidate in PlexAuth.OrderLocalConnections(server))
    {
        try
        {
            using var probe = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            probe.CancelAfter(TimeSpan.FromSeconds(8));
            var libraries = await plex.GetSectionsAsync(candidate.Uri, server.AccessToken, probe.Token).ConfigureAwait(false);
            return (candidate, libraries);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // Plex can advertise stale LAN/public/relay addresses together.
            // Continue in reliability order and use the first one that really
            // answers rather than persisting a URL that only looked suitable.
        }
    }

    return (null, new List<CompanionLibrary>());
}

static object ImportPeerView(JellyfinImportPeer peer) => new
{
    peer.Id,
    peer.Name,
    peer.ExportPath,
    peer.PlaybackBaseUrl,
    peer.PlexSectionKey,
    peer.PlexMovieSectionKey,
    peer.PlexShowSectionKey,
    peer.LastSyncUtc,
    peer.LastItemCount,
    peer.LastError,
    availableLibraries = peer.AvailableLibraries.Select(l => new { l.Id, l.Name, l.CollectionType }),
    peer.SelectedLibraryIds,
    mountedItemCount = peer.MountedFiles.Count,
    importIssueCount = peer.ImportCatalog.Count(i => i.Issue != null)
};

static object PeerView(CompanionPeer peer) => new
{
    peer.Id,
    peer.Name,
    peer.AddedUtc,
    peer.AllowDownloads,
    peer.AllowBulkDownloads
};

static string? TryReadJsonError(string body)
{
    try
    {
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
        {
            return error.GetString();
        }

        if (doc.RootElement.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
        {
            return message.GetString();
        }
    }
    catch (JsonException)
    {
    }

    return null;
}

static bool TryNormalizeJellyfinInviteUrl(string? raw, out string normalized)
{
    normalized = string.Empty;
    if (string.IsNullOrWhiteSpace(raw))
    {
        return false;
    }

    var trimmed = raw.Trim().TrimEnd('/');
    if (!trimmed.Contains("://", StringComparison.Ordinal))
    {
        trimmed = "https://" + trimmed;
    }

    return IsSafePeerUrl(trimmed, out normalized);
}

static bool IsSafePeerUrl(string candidate, out string normalized)
{
    normalized = string.Empty;
    if (!Uri.TryCreate(candidate.Trim(), UriKind.Absolute, out var uri)
        || !string.IsNullOrEmpty(uri.UserInfo)
        || (uri.Scheme != Uri.UriSchemeHttps
            && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
    {
        return false;
    }

    if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
    {
        return false;
    }

    normalized = uri.AbsoluteUri.TrimEnd('/');
    return true;
}

static bool IsPublicCompanionApi(PathString path)
    => path.Equals("/api/link/complete", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/api/federation/info", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/api/pools/invite", StringComparison.OrdinalIgnoreCase);

static CompanionPeer? FindPeerByFederationToken(HttpRequest request, CompanionState s)
{
    var token = request.Headers["X-Federation-Token"].ToString();
    if (string.IsNullOrEmpty(token))
    {
        return null;
    }

    return s.Peers.FirstOrDefault(p => FixedTimeEquals(token, p.AccessToken));
}

static bool FixedTimeEquals(string supplied, string expected)
{
    if (string.IsNullOrEmpty(supplied) || string.IsNullOrEmpty(expected))
    {
        return false;
    }

    var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
    var expectedBytes = Encoding.UTF8.GetBytes(expected);
    return suppliedBytes.Length == expectedBytes.Length
        && CryptographicOperations.FixedTimeEquals(suppliedBytes, expectedBytes);
}

internal static class CompanionListen
{
    public static int? Port { get; set; }
}

internal sealed class PendingPin
{
    public int? Id { get; set; }
}

internal sealed record ToggleLibraryRequest(string SectionKey, bool Shared);

internal sealed record SetPublicUrlRequest(string Url);

internal sealed record ConnectLocalPlexRequest(string? Url, string? Token);

internal sealed record LinkCompleteRequest(string Token, string? RequesterName);

internal sealed record InviteFriendRequest(string? Url, bool UsePlexRemoteAccess = false);

internal sealed record ImportConnectRequest(string? Code, List<string>? LibraryIds = null);
internal sealed record SelectImportLibrariesRequest(List<string>? LibraryIds);
internal sealed record SetMediaMountRequest(string LocalPath, string PlexPath);

internal sealed record SetPlexSectionRequest(string? SectionKey);

internal sealed record SelectPlexServerRequest(string Id);

internal sealed record SetPeerDownloadAccessRequest(bool AllowDownloads, bool AllowBulkDownloads);

internal sealed record AutostartRequest(bool Enabled);

internal sealed class CompanionPoolInviteRequest
{
    public string? InviteId { get; set; }
    public string? FromFederationId { get; set; }
    public string? PoolId { get; set; }
    public string? PoolName { get; set; }
    public string? OwnerName { get; set; }
    public string? CallbackUrl { get; set; }
    public string? CallbackToken { get; set; }
    public string? FederationPluginVersion { get; set; }
    public List<CompanionPoolRosterWire>? Roster { get; set; }
}

internal sealed class CompanionPoolRosterWire
{
    public string? Name { get; set; }
    public string? Url { get; set; }
}

internal sealed class ConnectCodePayload
{
    public string? Url { get; set; }

    public string? Token { get; set; }

    public string? Name { get; set; }
}
