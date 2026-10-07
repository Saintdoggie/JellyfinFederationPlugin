using System;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Federation.Configuration;

[ApiController]
[Authorize]
[Route("Plugins/Federation/Adaptive")]
public sealed class AdaptivePlaybackController : ControllerBase
{
    private readonly IAuthorizationContext _authorization;
    private readonly IUserManager _users;
    private readonly ILibraryManager _library;
    private readonly FederationLibraryManager _manager;
    private readonly RemoteAccessControlService _access;
    private readonly AdaptiveSourceRanking _ranking;
    private readonly AdaptivePreparationGate _preparations;

    public AdaptivePlaybackController(IAuthorizationContext authorization, IUserManager users, ILibraryManager library,
        FederationLibraryManager manager, RemoteAccessControlService access, AdaptiveSourceRanking ranking, AdaptivePreparationGate preparations)
    {
        _authorization = authorization; _users = users; _library = library; _manager = manager;
        _access = access; _ranking = ranking; _preparations = preparations;
    }

    private async Task<Jellyfin.Database.Implementations.Entities.User?> UserAsync()
    {
        var info = await _authorization.GetAuthorizationInfo(HttpContext).ConfigureAwait(false);
        return info?.User ?? (info?.UserId is { } id && id != Guid.Empty ? _users.GetUserById(id) : null);
    }

    [HttpGet("Hls")]
    [AllowAnonymous]
    public IActionResult Hls()
    {
        var stream = typeof(AdaptivePlaybackController).Assembly.GetManifestResourceStream("Jellyfin.Plugin.Federation.Web.vendor.hls.light.min.js");
        return stream == null ? NotFound() : File(stream, "application/javascript");
    }

    /// <summary>Cache-only ranking, no polling of upstream servers and no credential-bearing paths.</summary>
    [HttpGet("{itemId:guid}/Sources")]
    public async Task<IActionResult> Sources(Guid itemId)
    {
        Response.Headers.CacheControl = "private, no-store";
        var user = await UserAsync().ConfigureAwait(false);
        if (user == null) return Unauthorized();
        if (Plugin.Instance?.Configuration.EnableAdaptivePlayback != true) return NotFound();
        var item = _library.GetItemById(itemId);
        if (item == null || !item.IsVisible(user, false)
            || !_library.GetItemList(new InternalItemsQuery(user) { ItemIds = new[] { itemId }, Recursive = true, Limit = 1 }).Any()) return NotFound();
        var key = FederationLibraryManager.GetFederationKey(item);
        var entry = key == null ? null : _manager.Cache.GetEntryByKey(key);
        if (entry == null || entry.ItemType != "Movie") return NotFound();
        var sources = entry.GetSourcesSnapshot()
            .Select(source => (Source: source, Server: _manager.GetServer(source.ServerId)))
            .Where(p => p.Server is { Enabled: true }
                && FederationItemPersistenceService.AvailabilityOverride?.IsOffline(p.Source.ServerId) != true
                && _access.IsAllowed(p.Server, user.Id, entry.MappingName, p.Source.RemoteItemId))
            .Select(p => new
            {
                Id = FederationMediaSourceProvider.BuildSourceId(p.Source),
                StaticIdentity = p.Source.ServerId + ":" + p.Source.RemoteItemId.ToString("N"),
                Name = p.Server!.Name,
                TimelineName = p.Source.TimelineName,
                RunTimeTicks = p.Source.RunTimeTicks,
                Score = _ranking.Score(p.Server, p.Source.Bitrate.GetValueOrDefault(),
                    p.Source.MediaStreams?.Where(s => s.Type == MediaStreamType.Video).Select(s => s.Height.GetValueOrDefault()).DefaultIfEmpty(0).Max() ?? 0,
                    _manager.GetEffectiveWanCapMbps(p.Server)),
                Streams = p.Source.MediaStreams?.Where(s => s.Type is MediaStreamType.Audio or MediaStreamType.Subtitle)
                    .Select(s => new { s.Index, Type = s.Type.ToString(), s.Language, s.Title, s.IsDefault, s.IsForced, s.Channels })
            }).OrderByDescending(p => p.Score).ToArray();
        return Ok(new { Sources = sources });
    }

    [HttpPost("Preparation")]
    public async Task<IActionResult> Prepare()
    {
        var user = await UserAsync().ConfigureAwait(false);
        if (user == null) return Unauthorized();
        if (Plugin.Instance?.Configuration.EnableAdaptivePlayback != true) return NotFound();
        var lease = _preparations.Acquire(user.Id);
        return lease.HasValue ? Ok(new { Lease = lease.Value }) : StatusCode(429);
    }

    [HttpDelete("Preparation/{lease:guid}")]
    public async Task<IActionResult> Release(Guid lease)
    {
        var user = await UserAsync().ConfigureAwait(false);
        if (user == null) return Unauthorized();
        _preparations.Release(user.Id, lease);
        return NoContent();
    }
}
