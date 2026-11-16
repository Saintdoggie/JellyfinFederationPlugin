using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Updates;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Makes the plugin's own page under Dashboard → Plugins complete however the
    /// plugin was installed.
    /// <para>
    /// A copy put into the plugins folder by hand has no picture (Jellyfin only
    /// stores one when it installs from a repository), and on a server without the
    /// plugin's repository the page asks Jellyfin for details nobody has and shows
    /// "An error occurred while getting the plugin details from the repository".
    /// This answers those three requests for this plugin only, and only when
    /// Jellyfin itself has nothing: the list says the plugin has a picture, the
    /// picture request gets the logo built into the plugin, and the details
    /// request gets the installed version with its notes.
    /// </para>
    /// It offers no other version to install: installing is Jellyfin's job and
    /// needs the repository.
    /// </summary>
    public sealed class FederationPluginPageFilter : IAsyncActionFilter
    {
        /// <summary>Where releases are published; shown as the repository when none is configured.</summary>
        internal const string ReleasesUrl = "https://github.com/Saintdoggie/JellyfinFederationPlugin/releases";

        private static readonly Lazy<byte[]?> Logo = new(() => Resource("logo.png"));
        private static readonly Lazy<PackageInfo?> Details = new(BuildDetails);

        private readonly ILogger<FederationPluginPageFilter> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="FederationPluginPageFilter"/> class.
        /// </summary>
        public FederationPluginPageFilter(ILogger<FederationPluginPageFilter> logger)
        {
            _logger = logger;
        }

        /// <summary>Test seam: this plugin's id.</summary>
        internal Func<Guid> PluginId { get; set; } = () => Plugin.Instance?.Id ?? Guid.Empty;

        /// <inheritdoc />
        public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            var kind = Classify(context.ActionDescriptor.RouteValues);
            var executed = await next().ConfigureAwait(false);
            if (kind == PageRequest.None || executed.Exception != null)
            {
                return;
            }

            try
            {
                var id = PluginId();
                switch (kind)
                {
                    case PageRequest.List:
                        MarkPicture(executed.Result, id);
                        break;
                    case PageRequest.Picture when IsMissing(executed.Result) && Argument(context, "pluginId") == id && Logo.Value is { } logo:
                        executed.Result = new FileContentResult(logo, "image/png");
                        break;
                    case PageRequest.Details when IsMissing(executed.Result) && Argument(context, "assemblyGuid") == id && Details.Value is { } details:
                        executed.Result = new OkObjectResult(details);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[Federation] Could not complete the plugin's dashboard page");
            }
        }

        /// <summary>Which of the dashboard's plugin requests this is.</summary>
        internal static PageRequest Classify(IDictionary<string, string?> route)
        {
            route.TryGetValue("controller", out var controller);
            route.TryGetValue("action", out var action);
            if (string.Equals(controller, "Plugins", StringComparison.OrdinalIgnoreCase))
            {
                return action switch
                {
                    "GetPlugins" => PageRequest.List,
                    "GetPluginImage" => PageRequest.Picture,
                    _ => PageRequest.None
                };
            }

            return string.Equals(controller, "Package", StringComparison.OrdinalIgnoreCase) && action == "GetPackageInfo"
                ? PageRequest.Details
                : PageRequest.None;
        }

        /// <summary>Says "has a picture" for this plugin in Jellyfin's list of plugins.</summary>
        internal static void MarkPicture(IActionResult? result, Guid id)
        {
            if (id == Guid.Empty || Logo.Value == null || result is not ObjectResult { Value: IEnumerable<PluginInfo> plugins } answer)
            {
                return;
            }

            var list = plugins.ToList();
            foreach (var plugin in list.Where(p => p.Id == id))
            {
                plugin.HasImage = true;
            }

            answer.Value = list;
        }

        /// <summary>
        /// The installed version described for the plugin page, from the
        /// <c>meta.json</c> built into the plugin. The version is written the way
        /// Jellyfin reports an installed plugin (four parts), which is what the
        /// page compares to mark it "Installed".
        /// </summary>
        internal static PackageInfo? BuildDetails()
        {
            var meta = Resource("meta.json");
            if (meta == null)
            {
                return null;
            }

            using var json = JsonDocument.Parse(meta);
            var root = json.RootElement;
            string Text(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
            var version = typeof(FederationPluginPageFilter).Assembly.GetName().Version ?? new Version(0, 0, 0, 0);
            DateTime.TryParse(Text("timestamp"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var built);
            return new PackageInfo
            {
                Name = Text("name"),
                Description = Text("description"),
                Overview = Text("overview"),
                Owner = Text("owner"),
                Category = Text("category"),
                Id = Guid.TryParse(Text("guid"), out var guid) ? guid : Guid.Empty,
                Versions = new List<VersionInfo>
                {
                    new()
                    {
                        Version = version.ToString(4),
                        Changelog = Text("changelog"),
                        TargetAbi = Text("targetAbi"),
                        Timestamp = built == default ? null : built.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
                        RepositoryName = "GitHub releases (installed by hand)",
                        RepositoryUrl = ReleasesUrl
                    }
                }
            };
        }

        private static bool IsMissing(IActionResult? result)
            => result is NotFoundResult or NotFoundObjectResult or ObjectResult { StatusCode: 404 } or StatusCodeResult { StatusCode: 404 };

        private static Guid Argument(ActionExecutingContext context, string name)
            => context.ActionArguments.TryGetValue(name, out var value) && value is Guid guid ? guid : Guid.Empty;

        private static byte[]? Resource(string name)
        {
            var assembly = typeof(FederationPluginPageFilter).Assembly;
            using var stream = assembly.GetManifestResourceStream(typeof(Plugin).Namespace + ".PluginPage." + name);
            if (stream == null)
            {
                return null;
            }

            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return copy.ToArray();
        }

        /// <summary>The dashboard requests this filter looks at.</summary>
        internal enum PageRequest
        {
            /// <summary>Anything else.</summary>
            None,

            /// <summary>The list of installed plugins.</summary>
            List,

            /// <summary>A plugin's picture.</summary>
            Picture,

            /// <summary>A plugin's details from the repositories.</summary>
            Details
        }
    }
}
