"""Real Companion plus a Plex API proxy that deliberately refuses video APIs.

Used by federation_metadata_smoke.py --companion-files. Catalog/artwork come
from the real fixture PMS. All video bytes must come from approved local files.
This proves independence from Plex's video API, not account entitlement policy.
"""
import json
import os
from pathlib import Path
import secrets
import shutil
import subprocess
import uuid

PROXY = r'''
using System.Text.Json.Nodes;
var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
var app = builder.Build();
var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
int mediaRequests = 0;
app.Run(async ctx => {
    var path = ctx.Request.Path.Value ?? "/";
    if (path == "/qa-stats") { await ctx.Response.WriteAsJsonAsync(new { mediaRequests }); return; }
    if (path.StartsWith("/library/parts/") || path.StartsWith("/video/") || path.Contains("transcode")) {
        Interlocked.Increment(ref mediaRequests);
        ctx.Response.StatusCode = 402; return;
    }
    using var req = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), "http://127.0.0.1:32400" + path + ctx.Request.QueryString);
    if (ctx.Request.Headers.TryGetValue("X-Plex-Token", out var token)) req.Headers.TryAddWithoutValidation("X-Plex-Token", token.ToArray());
    req.Headers.TryAddWithoutValidation("Accept", "application/json");
    try {
        using var response = await http.SendAsync(req, ctx.RequestAborted);
        ctx.Response.StatusCode = (int)response.StatusCode;
        var bytes = await response.Content.ReadAsByteArrayAsync(ctx.RequestAborted);
        if (response.Content.Headers.ContentType?.MediaType == "application/json") {
            var root = JsonNode.Parse(bytes);
            void Rewrite(JsonNode? node) {
                if (node is JsonObject obj) foreach (var field in obj.ToArray()) {
                    if ((field.Key == "file" || field.Key == "path") && field.Value is JsonValue value && value.TryGetValue<string>(out var text)
                        && text.StartsWith("/media/")) obj[field.Key] = "/plex-files/" + text[7..];
                    else Rewrite(field.Value);
                }
                else if (node is JsonArray array) foreach (var child in array) Rewrite(child);
            }
            Rewrite(root);
            bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(root);
        }
        ctx.Response.ContentType = response.Content.Headers.ContentType?.ToString();
        if (!HttpMethods.IsHead(ctx.Request.Method)) await ctx.Response.Body.WriteAsync(bytes, ctx.RequestAborted);
    } catch (HttpRequestException) { ctx.Response.StatusCode = 502; }
});
app.Run();
'''


class CompanionFileFixture:
    def __init__(self, repo, work, pod, created, image, media, request, wait_for, run):
        self.request = request
        self.run = run
        self.media = media
        self.peer = 'file-qa-peer'
        self.token = secrets.token_hex(32)
        self.other_token = secrets.token_hex(32)
        self.source = 'http://127.0.0.1:5000/plex/' + self.peer
        dotnet = os.environ.get('DOTNET') or shutil.which('dotnet')
        if not dotnet:
            raise RuntimeError('Set DOTNET to .NET SDK 10 with the .NET 9 ASP.NET runtime installed.')
        sdk_root = Path(dotnet).resolve().parent
        app = work / 'companion'
        shutil.copytree(repo / 'artifacts/qa/companion', app)
        for private in ('companion-state.json', 'media-mount.conf'):
            (app / private).unlink(missing_ok=True)
        self.admin_key = secrets.token_hex(32)
        (app / 'companion-state.json').write_text(json.dumps({
            'ClientIdentifier': str(uuid.uuid4()), 'AdminAccessKey': self.admin_key,
            'MediaAccessKey': secrets.token_hex(32),
            'Peers': [{'Id': self.peer, 'Name': 'QA receiver', 'AccessToken': self.token, 'AllowDownloads': True},
                      {'Id': 'file-qa-peer-other', 'Name': 'Other QA receiver', 'AccessToken': self.other_token}],
        }))
        (app / 'companion-state.json').chmod(0o600)
        proxy = work / 'blocked-plex-api'
        proxy.mkdir()
        (proxy / 'Proxy.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk.Web"><PropertyGroup><TargetFramework>net9.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><InvariantGlobalization>true</InvariantGlobalization></PropertyGroup></Project>')
        (proxy / 'Program.cs').write_text(PROXY)
        published = proxy / 'published'
        subprocess.run([dotnet, 'publish', str(proxy), '-c', 'Release', '-o', str(published), '--nologo', '-v', 'quiet'],
                       check=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        self.baseline = None
        for suffix, directory, dll, port in [('proxy', published, 'Proxy.dll', 5001), ('app', app, 'FederationCompanion.dll', 5000)]:
            name = pod + '-files-' + suffix
            created.append(name)
            run(['podman', 'run', '-d', '--name', name, '--pod', pod, '--security-opt', 'label=disable',
                 '-v', f'{sdk_root}:/qa-dotnet:ro', '-v', f'{directory}:/qa-app', '-v', f'{media}:/owned-media:ro',
                 '-w', '/qa-app', '--entrypoint', '/qa-dotnet/dotnet', image, f'/qa-app/{dll}', '--urls', f'http://0.0.0.0:{port}'])
        infra = json.loads(run(['podman', 'pod', 'inspect', pod]))[0]['InfraContainerID']
        self.base = 'http://' + run(['podman', 'port', infra, '5000/tcp'])
        self.proxy_base = 'http://' + run(['podman', 'port', infra, '5001/tcp'])
        self.owner = {'X-Companion-Admin': self.admin_key}
        wait_for(lambda: request(self.base, '/api/app/info', self.owner), 'File-relay Companion startup failed')
        wait_for(lambda: request(self.proxy_base, '/identity', raw=True)[1], 'Blocked Plex API proxy startup failed')

    def enable(self, plex_token, plex_movies, sections):
        req = self.request
        for method in ('GET', 'POST'):
            req(self.base, '/api/local-file-relay', method=method,
                data={'enabled': True} if method == 'POST' else None, expected=401, raw=True)
        req(self.base, '/api/plex/connect-local', self.owner, {'url': 'http://127.0.0.1:5001', 'token': plex_token})
        for section, _ in sections:
            req(self.base, '/api/libraries/toggle', self.owner, {'sectionKey': section, 'shared': True})
        movie = plex_movies[0]
        path = movie['Media'][0]['Part'][0]['key']
        friend = {'X-Plex-Token': self.token}
        req(self.base, f"/plex/{self.peer}/library/metadata/{movie['ratingKey']}", friend)
        req(self.base, '/plex/' + self.peer + path, friend, expected=402, raw=True)
        self.baseline = req(self.proxy_base, '/qa-stats')['mediaRequests']
        if self.baseline != 1:
            raise RuntimeError('Blocked-Plex baseline was not exercised exactly once')
        req(self.base, '/api/local-file-relay', self.owner, {'enabled': True}, expected=400, raw=True)
        settings = req(self.base, '/api/local-file-relay', self.owner)
        mappings = [{'plexRoot': r['plexRoot'], 'localRoot': r['plexRoot'].replace('/plex-files/', '/owned-media/', 1)} for r in settings['roots']]
        req(self.base, '/api/local-file-relay', self.owner, {'enabled': True, 'mappings': [{'plexRoot': '/unapproved', 'localRoot': '/owned-media'}]}, expected=400, raw=True)
        req(self.base, '/api/local-file-relay', self.owner, {'enabled': True, 'mappings': mappings})
        req(self.base, '/plex/' + self.peer + path, friend, raw=True)
        for bad in ('library/parts/999/missing.mp4', 'library/parts/77/private.mp4'):
            req(self.base, '/plex/' + self.peer + '/' + bad, friend, expected=403, raw=True)

    def verify(self, item):
        req = self.request
        if req(self.proxy_base, '/qa-stats')['mediaRequests'] != self.baseline:
            raise RuntimeError('Original-file playback called a Plex video API')
        path = item['Media'][0]['Part'][0]['key']
        rating = item['ratingKey']
        for peer_id, token in [(self.peer, self.token), ('file-qa-peer-other', self.other_token)]:
            friend = {'X-Plex-Token': token}
            req(self.base, f'/plex/{peer_id}/library/metadata/{rating}', friend)
            req(self.base, '/plex/' + peer_id + path, friend, method='HEAD', raw=True)
        req(self.base, '/api/peers/' + self.peer, self.owner, method='DELETE')
        req(self.base, '/plex/' + self.peer + path, {'X-Plex-Token': self.token}, expected=401, raw=True)
        req(self.base, '/plex/file-qa-peer-other' + path, {'X-Plex-Token': self.other_token}, method='HEAD', raw=True)
        original = self.media / Path(item['Media'][0]['Part'][0]['file']).relative_to('/media')
        hidden = original.with_name(original.name + '.qa-hidden-' + uuid.uuid4().hex)
        original.rename(hidden)
        try:
            req(self.base, '/plex/file-qa-peer-other' + path, {'X-Plex-Token': self.other_token}, method='HEAD', expected=404, raw=True)
        finally:
            hidden.rename(original)
        if req(self.proxy_base, '/qa-stats')['mediaRequests'] != self.baseline:
            raise RuntimeError('A missing original file fell back to Plex streaming')
        # Even stale/invalid mappings must not prevent the owner from disabling the mode.
        req(self.base, '/api/local-file-relay', self.owner, {'enabled': False, 'mappings': [{'plexRoot': '/removed', 'localRoot': '/missing'}]})
        if req(self.base, '/api/local-file-relay', self.owner)['enabled']:
            raise RuntimeError('Owner could not disable original-file mode')
        print('LIVE PASS: Companion original files served despite HTTP 402 at Plex media APIs; no fallback requests; revoked peer denied and other peer retained access.', flush=True)
