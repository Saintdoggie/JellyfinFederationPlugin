using System;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Federation.Services.DirectLink
{
    /// <summary>
    /// Runs the direct link's QUIC code (the embedded
    /// <c>Jellyfin.Plugin.Federation.QuicTransport</c> assembly) in a private
    /// load context with its own copy of System.Net.Quic.
    /// <para>
    /// Why: on Linux, System.Net.Quic needs <c>libmsquic.so.2</c>, which Jellyfin
    /// doesn't ship, and Jellyfin's web host asks whether QUIC works before any
    /// plugin loads. The runtime remembers that first "no" for the whole
    /// process. A private copy of System.Net.Quic starts fresh, and this context
    /// hands it the plugin's bundled library (<c>native/&lt;rid&gt;/libmsquic.so.2</c>).
    /// Only BCL types cross between the plugin and the transport: connections
    /// are opaque objects and streams are plain <see cref="Stream"/>s.
    /// </para>
    /// On Windows, .NET ships MsQuic itself; the same path just finds it.
    /// </summary>
    public static class QuicTransportHost
    {
        private const string TransportName = "Jellyfin.Plugin.Federation.QuicTransport";
        private static readonly object Gate = new();
        private static bool _done;
        private static bool _available;
        private static string _reason = "not checked yet";

        /// <summary>Gets a value indicating whether QUIC works for the direct link.</summary>
        public static bool Available
        {
            get
            {
                EnsureLoaded();
                return _available;
            }
        }

        /// <summary>Gets why QUIC isn't available, or the MsQuic version when it is.</summary>
        public static string Reason
        {
            get
            {
                EnsureLoaded();
                return _reason;
            }
        }

        /// <summary>Gets the initiator connect call (see QuicTransport.ConnectAsync).</summary>
        internal static Func<int, IPEndPoint, X509Certificate2, string, byte[], byte[], CancellationToken, Task<object>> Connect { get; private set; } = null!;

        /// <summary>Gets the responder accept call (see QuicTransport.AcceptAsync).</summary>
        internal static Func<int, IPEndPoint, X509Certificate2, string, byte[], byte[], int, TimeSpan, CancellationToken, Task<object>> Accept { get; private set; } = null!;

        /// <summary>Gets the call that opens a request stream.</summary>
        internal static Func<int, IPEndPoint, X509Certificate2, string, byte[], byte[], int, CancellationToken, Task<object>> ConnectPaired { get; private set; } = null!;

        internal static Func<int, IPEndPoint, X509Certificate2, string, byte[], byte[], int, TimeSpan, CancellationToken, Task<object>> AcceptPaired { get; private set; } = null!;

        internal static Action<long, long> ConfigureWindows { get; private set; } = null!;

        internal static Func<object, CancellationToken, Task<Stream>> OpenStream { get; private set; } = null!;

        /// <summary>Gets the call that waits for the other side's next stream.</summary>
        internal static Func<object, CancellationToken, Task<Stream>> AcceptStream { get; private set; } = null!;

        /// <summary>Gets the call that ends this side's writes on a stream.</summary>
        internal static Action<Stream> CompleteWrites { get; private set; } = null!;

        /// <summary>Gets the call that aborts a stream.</summary>
        internal static Action<Stream> Abort { get; private set; } = null!;

        /// <summary>Gets the call that reads a connection's remote endpoint.</summary>
        internal static Func<object, IPEndPoint> RemoteEndPoint { get; private set; } = null!;

        /// <summary>Gets the call that reads a connection's local endpoint.</summary>
        internal static Func<object, IPEndPoint> LocalEndPoint { get; private set; } = null!;

        /// <summary>Gets the call that closes a connection.</summary>
        internal static Func<object, Task> Close { get; private set; } = null!;

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

        /// <summary>Loads the transport (once) and records whether QUIC works.</summary>
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
                    Load();
                }
                catch (Exception ex)
                {
                    _available = false;
                    _reason = "the QUIC transport didn't load: " + ex.GetBaseException().Message;
                }
            }
        }

        private static void Load()
        {
            string? library = null;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                library = BundledPath(Path.GetDirectoryName(typeof(QuicTransportHost).Assembly.Location));
                if (library == null)
                {
                    _reason = "no bundled MsQuic for " + RuntimeInformation.ProcessArchitecture;
                    return;
                }

                if (!File.Exists(library))
                {
                    _reason = "the bundled MsQuic is missing (" + library + ")";
                    return;
                }
            }
            else if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                _reason = "direct links work on Linux and Windows servers only";
                return;
            }

            var quicPath = typeof(System.Net.Quic.QuicConnection).Assembly.Location;
            if (string.IsNullOrEmpty(quicPath) || !File.Exists(quicPath))
            {
                _reason = "this Jellyfin build has no System.Net.Quic file to load";
                return;
            }

            byte[] transportBytes;
            using (var resource = typeof(QuicTransportHost).Assembly.GetManifestResourceStream(TransportName + ".dll"))
            {
                if (resource == null)
                {
                    _reason = "the plugin build is missing its QUIC transport";
                    return;
                }

                using var copy = new MemoryStream();
                resource.CopyTo(copy);
                transportBytes = copy.ToArray();
            }

            var context = new TransportLoadContext(quicPath, transportBytes, library);
            var assembly = context.LoadFromAssemblyName(new AssemblyName(TransportName));
            var type = assembly.GetType(TransportName + ".QuicTransport", throwOnError: true)!;

            T Bind<T>(string name)
                where T : Delegate
                => type.GetMethod(name, BindingFlags.Public | BindingFlags.Static)!.CreateDelegate<T>();

            var supported = Bind<Func<bool>>("IsSupported")();
            var diagnostics = Bind<Func<string>>("Diagnostics")();
            if (!supported)
            {
                _reason = "QUIC isn't supported here: " + diagnostics + (library != null ? " (MsQuic needs libcrypto.so.3 and libnuma.so.1)" : string.Empty);
                return;
            }

            Connect = Bind<Func<int, IPEndPoint, X509Certificate2, string, byte[], byte[], CancellationToken, Task<object>>>("ConnectAsync");
            Accept = Bind<Func<int, IPEndPoint, X509Certificate2, string, byte[], byte[], int, TimeSpan, CancellationToken, Task<object>>>("AcceptAsync");
            ConnectPaired = Bind<Func<int, IPEndPoint, X509Certificate2, string, byte[], byte[], int, CancellationToken, Task<object>>>("ConnectPairedAsync");
            AcceptPaired = Bind<Func<int, IPEndPoint, X509Certificate2, string, byte[], byte[], int, TimeSpan, CancellationToken, Task<object>>>("AcceptPairedAsync");
            ConfigureWindows = Bind<Action<long, long>>("ConfigureWindows");
            OpenStream = Bind<Func<object, CancellationToken, Task<Stream>>>("OpenStreamAsync");
            AcceptStream = Bind<Func<object, CancellationToken, Task<Stream>>>("AcceptStreamAsync");
            CompleteWrites = Bind<Action<Stream>>("CompleteWrites");
            Abort = Bind<Action<Stream>>("Abort");
            RemoteEndPoint = Bind<Func<object, IPEndPoint>>("RemoteEndPoint");
            LocalEndPoint = Bind<Func<object, IPEndPoint>>("LocalEndPoint");
            Close = Bind<Func<object, Task>>("CloseAsync");
            _available = true;
            _reason = diagnostics;
        }

        /// <summary>
        /// Gives the transport its own System.Net.Quic and the bundled MsQuic;
        /// everything else resolves from the default context, so certificates,
        /// endpoints and streams are the same types on both sides.
        /// </summary>
        private sealed class TransportLoadContext : AssemblyLoadContext
        {
            private readonly string _quicPath;
            private readonly byte[] _transport;
            private readonly string? _library;

            public TransportLoadContext(string quicPath, byte[] transport, string? library)
                : base("federation-direct-link-quic", isCollectible: false)
            {
                _quicPath = quicPath;
                _transport = transport;
                _library = library;
            }

            protected override Assembly? Load(AssemblyName assemblyName)
            {
                if (string.Equals(assemblyName.Name, "System.Net.Quic", StringComparison.Ordinal))
                {
                    return LoadFromAssemblyPath(_quicPath);
                }

                if (string.Equals(assemblyName.Name, TransportName, StringComparison.Ordinal))
                {
                    using var stream = new MemoryStream(_transport, writable: false);
                    return LoadFromStream(stream);
                }

                return null;
            }

            protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
            {
                if (_library != null && unmanagedDllName.Contains("msquic", StringComparison.OrdinalIgnoreCase))
                {
                    return NativeLibrary.Load(_library);
                }

                return IntPtr.Zero;
            }
        }
    }
}
