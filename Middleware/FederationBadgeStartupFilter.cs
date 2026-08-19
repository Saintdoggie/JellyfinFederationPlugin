using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace Jellyfin.Plugin.Federation.Middleware
{
    /// <summary>
    /// Registers <see cref="BadgeScriptInjectionMiddleware"/> at the very front of
    /// the request pipeline. <see cref="IStartupFilter"/> is a standard ASP.NET
    /// Core extension point - any implementation registered in the DI container
    /// is picked up automatically by the host's own Startup.Configure, with no
    /// cooperation needed from Jellyfin itself.
    /// </summary>
    public class FederationBadgeStartupFilter : IStartupFilter
    {
        /// <inheritdoc />
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return app =>
            {
                app.UseMiddleware<BadgeScriptInjectionMiddleware>();
                next(app);
            };
        }
    }
}
