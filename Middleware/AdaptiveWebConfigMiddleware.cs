using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.Federation.Middleware;

/// <summary>Register the window plugin through jellyfin-web's supported config.json plugin loader.</summary>
public sealed class AdaptiveWebConfigMiddleware
{
    private readonly RequestDelegate _next;
    public AdaptiveWebConfigMiddleware(RequestDelegate next) => _next = next;
    public async Task InvokeAsync(HttpContext context)
    {
        if (Plugin.Instance?.Configuration.EnableAdaptivePlayback != true
            || !context.Request.Path.Value!.EndsWith("/web/config.json", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context).ConfigureAwait(false); return;
        }
        var original = context.Response.Body;
        var encoding = context.Request.Headers.AcceptEncoding;
        context.Request.Headers.AcceptEncoding = string.Empty;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try { await _next(context).ConfigureAwait(false); }
        finally { context.Response.Body = original; context.Request.Headers.AcceptEncoding = encoding; }
        buffer.Position = 0;
        JsonObject? config = null;
        if (context.Response.StatusCode == 200)
        {
            try { config = await JsonNode.ParseAsync(buffer) as JsonObject; }
            catch (System.Text.Json.JsonException) { }
        }
        if (config?["plugins"] is JsonArray plugins)
        {
            if (!System.Linq.Enumerable.Any(plugins, p => p?.ToString() == "FederationAdaptivePlayer")) plugins.Add("FederationAdaptivePlayer");
            var bytes = System.Text.Encoding.UTF8.GetBytes(config.ToJsonString());
            context.Response.Headers.Remove("ETag");
            context.Response.Headers.Remove("Last-Modified");
            context.Response.Headers.CacheControl = "no-store";
            context.Response.ContentLength = bytes.Length;
            await original.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
        }
        else { buffer.Position = 0; await buffer.CopyToAsync(original, context.RequestAborted).ConfigureAwait(false); }
    }
}
