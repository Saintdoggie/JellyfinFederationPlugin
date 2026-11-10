using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Jellyfin.Plugin.FederationPlex
{
    /// <summary>Why the Plex add-in is, or is not, running.</summary>
    public enum PlexHostState
    {
        /// <summary>Jellyfin has not asked this plugin to register services yet.</summary>
        NotStarted = 0,

        /// <summary>The add-in is loaded and registered.</summary>
        Loaded = 1,

        /// <summary>The Jellyfin Federation plugin is not installed or not enabled.</summary>
        CoreMissing = 2,

        /// <summary>The Jellyfin Federation plugin is a different version.</summary>
        VersionMismatch = 3,

        /// <summary>An add-in DLL copied by hand into the Federation folder is already running.</summary>
        ManualCopyActive = 4,

        /// <summary>Loading failed; the message says why.</summary>
        Failed = 5
    }

    /// <summary>The outcome of trying to start the add-in, in words an admin can act on.</summary>
    public sealed record PlexHostStatus(PlexHostState State, string Message, string? CoreVersion, string OwnVersion);

    /// <summary>
    /// Loads the Plex add-in that ships inside this plugin into the Jellyfin
    /// Federation plugin's own load context.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The add-in is compiled against the core plugin's types. Jellyfin gives every
    /// plugin folder its own isolated <see cref="AssemblyLoadContext"/> and checks
    /// all of a plugin's types before running any of its code, so an assembly in
    /// this plugin's folder that referenced the core could never be loaded, and a
    /// second copy of the core beside it would be a different set of types (its
    /// <c>Plugin.Instance</c> would be empty). Loading the add-in into the core's
    /// context makes its references resolve to the one running core, exactly as if
    /// the DLL had been copied beside it by hand - without touching that folder.
    /// </para>
    /// <para>
    /// Nothing here references a core or add-in type: only names, versions and
    /// interfaces that Jellyfin itself defines. A missing or mismatched core leaves
    /// this plugin idle with a clear status instead of failing to load.
    /// </para>
    /// </remarks>
    public static class PlexAddInHost
    {
        /// <summary>Assembly name of the Jellyfin Federation plugin.</summary>
        public const string CoreAssemblyName = "Jellyfin.Plugin.Federation";

        /// <summary>Assembly name of the Plex add-in.</summary>
        public const string AddInAssemblyName = "Jellyfin.Plugin.Federation.Plex";

        /// <summary>Name of the embedded add-in DLL.</summary>
        internal const string AddInResource = "Jellyfin.Plugin.Federation.Plex.dll";

        private static readonly object Gate = new();

        /// <summary>Gets the current status.</summary>
        public static PlexHostStatus Status { get; private set; } = new(PlexHostState.NotStarted, "Jellyfin has not started this plugin yet.", null, OwnVersion());

        /// <summary>Gets the loaded add-in assembly, or null when it is not running from this plugin.</summary>
        public static Assembly? AddIn { get; private set; }

        /// <summary>
        /// Loads and registers the add-in once. Never throws: a failure becomes the status.
        /// </summary>
        /// <param name="services">Jellyfin's service collection, before the container is built.</param>
        /// <param name="applicationHost">Jellyfin's application host.</param>
        /// <returns>The resulting status.</returns>
        public static PlexHostStatus Start(IServiceCollection services, IServerApplicationHost applicationHost)
        {
            lock (Gate)
            {
                if (Status.State != PlexHostState.NotStarted)
                {
                    return Status;
                }

                try
                {
                    var loaded = AssemblyLoadContext.All.SelectMany(context => context.Assemblies).Where(a => !a.IsDynamic).ToList();
                    var core = loaded
                        .Where(a => string.Equals(a.GetName().Name, CoreAssemblyName, StringComparison.Ordinal))
                        .OrderByDescending(a => a.GetName().Version)
                        .FirstOrDefault();
                    var manualCopy = loaded.Any(a => string.Equals(a.GetName().Name, AddInAssemblyName, StringComparison.Ordinal));
                    var decision = Decide(core?.GetName().Version, manualCopy, typeof(PlexAddInHost).Assembly.GetName().Version);
                    if (decision.State != PlexHostState.Loaded)
                    {
                        return Status = decision;
                    }

                    var context = AssemblyLoadContext.GetLoadContext(core!)
                        ?? throw new InvalidOperationException("The Federation plugin has no load context.");
                    Assembly addIn;
                    using (var stream = typeof(PlexAddInHost).Assembly.GetManifestResourceStream(AddInResource)
                        ?? throw new InvalidOperationException("This plugin package does not contain the Plex add-in."))
                    using (var copy = new MemoryStream())
                    {
                        stream.CopyTo(copy);
                        copy.Position = 0;
                        addIn = context.LoadFromStream(copy);
                    }

                    var addInVersion = addIn.GetName().Version;
                    if (!SameVersion(addInVersion, core!.GetName().Version))
                    {
                        return Status = new PlexHostStatus(
                            PlexHostState.VersionMismatch,
                            $"This package carries Plex add-in {Text(addInVersion)}, but Jellyfin Federation is {Text(core.GetName().Version)}. Install matching versions of both plugins.",
                            Text(core.GetName().Version),
                            OwnVersion());
                    }

                    // The add-in's own registrations, exactly as Jellyfin would have
                    // called them had the DLL sat beside the core plugin.
                    var registrators = addIn.GetTypes()
                        .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IPluginServiceRegistrator).IsAssignableFrom(t))
                        .ToList();
                    foreach (var type in registrators)
                    {
                        ((IPluginServiceRegistrator)Activator.CreateInstance(type)!).RegisterServices(services, applicationHost);
                    }

                    // Jellyfin creates controllers from the container, so each one
                    // has to be registered as a service as well as discovered.
                    foreach (var controller in addIn.GetTypes().Where(t => t is { IsClass: true, IsAbstract: false, IsPublic: true } && typeof(ControllerBase).IsAssignableFrom(t)))
                    {
                        services.AddTransient(controller);
                    }

                    // Jellyfin only adds controllers from assemblies it loaded itself.
                    services.AddTransient<IPostConfigureOptions<MvcOptions>>(provider =>
                        new AddInControllers(provider.GetRequiredService<ApplicationPartManager>(), addIn));

                    AddIn = addIn;
                    return Status = decision;
                }
                catch (Exception ex)
                {
                    var detail = ex is ReflectionTypeLoadException load && load.LoaderExceptions.FirstOrDefault(e => e != null) is { } first
                        ? first.Message
                        : ex.Message;
                    return Status = new PlexHostStatus(
                        PlexHostState.Failed,
                        $"The Plex add-in could not be started ({ex.GetType().Name}: {detail}). Plex sources stay as they are; Jellyfin friends are not affected.",
                        null,
                        OwnVersion());
                }
            }
        }

        /// <summary>
        /// Decides whether the add-in may start. Pure, so every case is unit tested.
        /// </summary>
        /// <param name="coreVersion">Version of the loaded Federation plugin, or null when it is not loaded.</param>
        /// <param name="manualCopyLoaded">Whether an add-in assembly is already loaded (copied by hand beside the core).</param>
        /// <param name="ownVersion">This plugin's version.</param>
        /// <returns>The status; <see cref="PlexHostState.Loaded"/> means "go ahead".</returns>
        public static PlexHostStatus Decide(Version? coreVersion, bool manualCopyLoaded, Version? ownVersion)
        {
            var own = Text(ownVersion);
            if (coreVersion == null)
            {
                return new PlexHostStatus(
                    PlexHostState.CoreMissing,
                    "Jellyfin Federation is not installed or not enabled. Install it (same version as this plugin) and restart Jellyfin.",
                    null,
                    own);
            }

            var core = Text(coreVersion);
            if (manualCopyLoaded)
            {
                return new PlexHostStatus(
                    PlexHostState.ManualCopyActive,
                    "A Plex add-in DLL copied by hand into the Jellyfin Federation plugin folder is already running, so this plugin stays idle. To switch to this plugin, stop Jellyfin, remove Jellyfin.Plugin.Federation.Plex.dll from that folder, and start Jellyfin again. Settings and cached titles are kept.",
                    core,
                    own);
            }

            if (!SameVersion(coreVersion, ownVersion))
            {
                return new PlexHostStatus(
                    PlexHostState.VersionMismatch,
                    $"Jellyfin Federation is {core} but this plugin is {own}. Both must be the same version: update the older one and restart Jellyfin. Plex sources are paused until then; settings and cached titles are kept.",
                    core,
                    own);
            }

            return new PlexHostStatus(PlexHostState.Loaded, $"Plex support {own} is running with Jellyfin Federation {core}.", core, own);
        }

        /// <summary>Creates an add-in type by its full name with constructor injection, or null when it is not running.</summary>
        internal static T? Create<T>(IServiceProvider services, string typeName)
            where T : class
        {
            var type = AddIn?.GetType(typeName, throwOnError: false);
            return type == null ? null : ActivatorUtilities.CreateInstance(services, type) as T;
        }

        /// <summary>Test hook.</summary>
        internal static void ResetForTests()
        {
            lock (Gate)
            {
                AddIn = null;
                Status = new PlexHostStatus(PlexHostState.NotStarted, "Jellyfin has not started this plugin yet.", null, OwnVersion());
            }
        }

        private static bool SameVersion(Version? a, Version? b)
            => a != null && b != null && a.Major == b.Major && a.Minor == b.Minor && a.Build == b.Build;

        private static string Text(Version? version)
            => version == null ? "unknown" : $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";

        private static string OwnVersion() => Text(typeof(PlexAddInHost).Assembly.GetName().Version);

        private sealed class AddInControllers : IPostConfigureOptions<MvcOptions>
        {
            private readonly ApplicationPartManager _parts;
            private readonly Assembly _addIn;

            public AddInControllers(ApplicationPartManager parts, Assembly addIn)
            {
                _parts = parts;
                _addIn = addIn;
            }

            public void PostConfigure(string? name, MvcOptions options)
            {
                lock (_parts)
                {
                    if (!_parts.ApplicationParts.OfType<AssemblyPart>().Any(part => part.Assembly == _addIn))
                    {
                        _parts.ApplicationParts.Add(new AssemblyPart(_addIn));
                    }
                }
            }
        }
    }
}
