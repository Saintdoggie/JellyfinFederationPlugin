using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace Jellyfin.Plugin.Federation.Services.DirectLink
{
    /// <summary>
    /// Makes .NET's QUIC support work inside Jellyfin. On Linux, System.Net.Quic
    /// needs <c>libmsquic.so.2</c>, which Jellyfin's images don't ship, so the
    /// plugin carries its own copy (<c>native/&lt;rid&gt;/libmsquic.so.2</c>) and
    /// loads it here. Once a library with that name is loaded, the runtime's own
    /// lookup finds it. This must run before anything reads
    /// <c>QuicConnection.IsSupported</c>: the runtime checks once per process and
    /// remembers a "no". Windows ships MsQuic with .NET, so nothing is loaded there.
    /// </summary>
    public static class MsQuicLoader
    {
        private static readonly object Gate = new();
        private static bool _done;
        private static bool _available;
        private static string _reason = "not checked yet";

        /// <summary>Gets a value indicating whether QUIC works in this process.</summary>
        public static bool Available
        {
            get
            {
                EnsureLoaded();
                return _available;
            }
        }

        /// <summary>Gets why QUIC isn't available (or "ok").</summary>
        public static string Reason
        {
            get
            {
                EnsureLoaded();
                return _reason;
            }
        }

        /// <summary>The bundled library's path for this process, or null when none fits.</summary>
        internal static string? BundledPath(string? pluginDirectory)
        {
            if (string.IsNullOrEmpty(pluginDirectory) || !RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                return null;
            }

            var rid = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "linux-x64",
                Architecture.Arm64 => "linux-arm64",
                _ => null
            };
            return rid == null ? null : Path.Combine(pluginDirectory, "native", rid, "libmsquic.so.2");
        }

        /// <summary>Loads the bundled library (once) and records whether QUIC works.</summary>
        public static void EnsureLoaded()
        {
            lock (Gate)
            {
                if (_done)
                {
                    return;
                }

                _done = true;
                try
                {
                    string? loadNote = null;
                    var quicAlreadyLoaded = AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "System.Net.Quic");
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                    {
                        var path = BundledPath(Path.GetDirectoryName(typeof(MsQuicLoader).Assembly.Location));
                        if (path == null)
                        {
                            loadNote = "no bundled MsQuic for " + RuntimeInformation.ProcessArchitecture;
                        }
                        else if (!File.Exists(path))
                        {
                            loadNote = "the bundled MsQuic is missing (" + path + ")";
                        }
                        else if (!NativeLibrary.TryLoad(path, out _))
                        {
                            loadNote = "MsQuic could not load (it needs libcrypto.so.3 and libnuma.so.1)";
                        }
                    }
                    else if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    {
                        _reason = "direct links work on Linux and Windows servers only";
                        return;
                    }

                    _available = IsQuicSupported();
                    _reason = _available ? "ok" : (loadNote ?? "this system has no QUIC support") + " [" + RuntimeReason() + (quicAlreadyLoaded ? "; QUIC was checked before the plugin loaded" : string.Empty) + "]";
                }
                catch (Exception ex)
                {
                    _available = false;
                    _reason = "QUIC check failed: " + ex.Message;
                }
            }
        }

        /// <summary>The runtime's own reason QUIC is unsupported (diagnostics only).</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static string RuntimeReason()
        {
            try
            {
                var api = typeof(System.Net.Quic.QuicConnection).Assembly.GetType("System.Net.Quic.MsQuicApi");
                return api?.GetProperty("NotSupportedReason", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)?.GetValue(null) as string ?? "no reason given";
            }
            catch (Exception ex)
            {
                return ex.GetType().Name;
            }
        }

        // Kept out of EnsureLoaded's body so the System.Net.Quic types are only
        // touched after the native library is in place.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static bool IsQuicSupported()
        {
#pragma warning disable CA2252, CA1416
            return System.Net.Quic.QuicConnection.IsSupported && System.Net.Quic.QuicListener.IsSupported;
#pragma warning restore CA2252, CA1416
        }
    }
}
