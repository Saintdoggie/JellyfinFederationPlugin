using System.Diagnostics;
using System.IO.Pipes;
using System.Text;

namespace FederationCompanion;

/// <summary>
/// One owner process per user. A second launch (double-clicking the shortcut
/// while the tray app is already running) hands an "open dashboard" request to
/// the first process over a named pipe and exits, instead of starting a second
/// Kestrel listener and a second rclone mount.
///
/// Ownership is a file lock in the per-user temp directory rather than a named
/// mutex: .NET scopes named mutexes per login session on Unix, so a launch from
/// a different session (an SSH shell beside the desktop, a scheduled task)
/// could start a duplicate. The lock is released by the OS when the process
/// exits, so a crash never leaves a stale lock behind.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    internal const string MutexName = "FederationCompanion.SingleInstance";
    internal const string PipeName = "FederationCompanion.OpenDashboard";
    private const string OpenMessage = "open";

    private readonly string _lockPath;
    private readonly string _pipeName;
    private FileStream? _lock;
    private CancellationTokenSource? _cts;
    private Task? _listener;

    public SingleInstance(string? testSuffix = null)
    {
        _lockPath = Path.Combine(Path.GetTempPath(), "federation-companion" + (testSuffix ?? string.Empty) + ".lock");
        _pipeName = PipeName + "." + Sanitize(Environment.UserName) + (testSuffix ?? string.Empty);
    }

    public bool IsFirstInstance { get; private set; }

    internal string InstancePipeName => _pipeName;

    /// <summary>True when this process owns the lock file and may run the app.</summary>
    public bool TryAcquire()
    {
        try
        {
            _lock = new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            _lock.SetLength(0);
            var pid = Encoding.UTF8.GetBytes(Environment.ProcessId.ToString());
            _lock.Write(pid);
            _lock.Flush();
            IsFirstInstance = true;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            IsFirstInstance = false;
            return false;
        }
    }

    /// <summary>Starts the background pipe that opens the dashboard on request.</summary>
    public void StartListener(Action onOpenRequested)
    {
        if (!IsFirstInstance)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _listener = Task.Run(() => ListenAsync(onOpenRequested, _cts.Token));
    }

    /// <summary>
    /// Asks an already-running Companion to open its dashboard. Returns false
    /// when no instance answers within the timeout.
    /// </summary>
    public static bool TrySignalExistingInstance(TimeSpan timeout, string? pipeName = null)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName ?? PipeName, PipeDirection.Out, PipeOptions.None);
            client.Connect((int)timeout.TotalMilliseconds);
            var bytes = Encoding.UTF8.GetBytes(OpenMessage);
            client.Write(bytes, 0, bytes.Length);
            client.Flush();
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }

    private async Task ListenAsync(Action onOpenRequested, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                var buffer = new byte[OpenMessage.Length];
                var read = await server.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                if (read == buffer.Length && Encoding.UTF8.GetString(buffer) == OpenMessage)
                {
                    onOpenRequested();
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A stale client or another listener; retry shortly.
                try
                {
                    await Task.Delay(500, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private static string Sanitize(string name)
        => new(name.Where(char.IsLetterOrDigit).ToArray());

    public void Dispose()
    {
        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _cts?.Dispose();
        _cts = null;
        _lock?.Dispose();
        _lock = null;
        try
        {
            File.Delete(_lockPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
