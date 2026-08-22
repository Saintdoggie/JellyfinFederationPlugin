using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace Jellyfin.Plugin.Federation.Middleware
{
    /// <summary>
    /// Registers <see cref="ConfigurationPageCompressionFixMiddleware"/> at the front
    /// of the pipeline, before <see cref="BadgeScriptInjectionMiddleware"/> and before
    /// Jellyfin's own <c>ResponseCompression</c> middleware, so it can strip
    /// <c>Accept-Encoding</c> for the single broken endpoint before compression runs.
    /// </summary>
    public class ConfigurationPageCompressionFixStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return app =>
            {
                app.UseMiddleware<ConfigurationPageCompressionFixMiddleware>();
                next(app);
            };
        }
    }
}
