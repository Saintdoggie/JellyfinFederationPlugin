using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Hosting;

namespace FederationCompanion;

/// <summary>Owns only the rclone process started by Companion, never an existing user mount.</summary>
public sealed class LocalMediaMountService(CompanionState state, IHostApplicationLifetime lifetime, RcloneBootstrapper rclone, WinFspInstaller winfsp) : BackgroundService
{
    internal const string OwnedPidFileName = "media-mount.pid";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    public string Message { get; private set; } = "Companion starts the media folder by itself after install.";
    public bool HelperReady => rclone.FindExisting() != null;

    /// <summary>True while this process owns the mount or a previous run left its rclone behind.</summary>
    public bool HasOwnedProcess => _process is { HasExited: false } || TryReadOwnedPid(AppContext.BaseDirectory, out _);

    internal static bool ShouldManageMount(CompanionState current)
        => current.AutoStartMediaMount || string.IsNullOrEmpty(current.MediaMountRoot);

    internal static string OwnedPidPath(string directory) => Path.Combine(directory, OwnedPidFileName);

    internal static void WriteOwnedPid(string directory, int pid)
    {
        try { File.WriteAllText(OwnedPidPath(directory), pid.ToString(CultureInfo.InvariantCulture)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    internal static void ClearOwnedPid(string directory)
    {
        try { File.Delete(OwnedPidPath(directory)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    internal static bool TryReadOwnedPid(string directory, out int pid)
    {
        pid = 0;
        try
        {
            var text = File.ReadAllText(OwnedPidPath(directory)).Trim();
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out pid) && pid > 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public async Task<bool> StartMountAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (MediaMount.IsMounted(state.MediaMountRoot, state.ClientIdentifier))
            {
                Message = "Media mount is readable. You can add your friend to Plex.";
                return true;
            }
            if (CompanionListen.Port is not int port)
            {
                Message = "Companion is still starting. Retry in a moment.";
                return false;
            }
            StopOwnedProcess();
            if (OperatingSystem.IsWindows() && !FilesystemDriver.IsAvailable())
            {
                Message = "Installing the Windows media driver (one-time; Windows may ask for permission)…";
                await winfsp.EnsureAsync(ct).ConfigureAwait(false);
                if (!FilesystemDriver.IsAvailable())
                {
                    Message = FilesystemDriver.MissingMessage();
                    return false;
                }
            }
            if (rclone.FindExisting() == null)
            {
                Message = "Downloading the media helper (one-time)…";
            }
            var (ready, executable, helperMessage) = await rclone.EnsureAsync(ct).ConfigureAwait(false);
            if (!ready || string.IsNullOrWhiteSpace(executable))
            {
                Message = helperMessage;
                return false;
            }
            var root = Path.Combine(AppContext.BaseDirectory, "plex-media");
            if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            {
                Message = "The plex-media folder is not empty. Choose a different manual mount folder in Advanced setup.";
                return false;
            }
            if (OperatingSystem.IsWindows())
            {
                // WinFsp directory mounts require a nonexistent child folder.
                if (Directory.Exists(root)) Directory.Delete(root);
            }
            else Directory.CreateDirectory(root);

            var config = Path.Combine(AppContext.BaseDirectory, "media-mount.conf");
            await File.WriteAllTextAsync(config,
                $"[companion]\ntype = webdav\nurl = http://127.0.0.1:{port}/media/\nvendor = other\nbearer_token = {state.MediaAccessKey}\n", ct);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(config, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var start = CreateStartInfo(executable, config, root);
            _process = Process.Start(start) ?? throw new InvalidOperationException("Mount process did not start.");
            WriteOwnedPid(AppContext.BaseDirectory, _process.Id);
            // Drain without logging: rclone errors can contain item URLs. The
            // owner receives a stable repair message instead of raw process text.
            var errors = new System.Text.StringBuilder();
            _process.OutputDataReceived += (_, _) => { };
            _process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data) && errors.Length < 4000)
                {
                    errors.AppendLine(e.Data);
                }
            };
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            for (var attempt = 0; attempt < 40; attempt++)
            {
                if (_process.HasExited) break;
                if (MediaMount.IsMounted(root, state.ClientIdentifier))
                {
                    state.MediaMountRoot = root;
                    state.PlexMountRoot = root;
                    state.AutoStartMediaMount = true;
                    await state.SaveAsync();
                    Message = "Media mount started. Keep Companion running; it will restore the mount after a restart.";
                    return true;
                }
                await Task.Delay(250, ct);
            }
            var output = errors.ToString();
            StopOwnedProcess();
            Message = FilesystemDriver.ClassifyFailure(output);
            return false;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            StopOwnedProcess();
            Message = FilesystemDriver.ClassifyFailure(ex.Message);
            return false;
        }
        catch (OperationCanceledException) { StopOwnedProcess(); throw; }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Stops only the rclone Companion started, including one left behind by a
    /// previous run. The PID marker is the ownership proof; an unrelated
    /// rclone process is never touched.
    /// </summary>
    public async Task<bool> StopMountAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!HasOwnedProcess)
            {
                Message = "Companion is not running the media folder.";
                return false;
            }

            StopOwnedProcess();
            Message = "Media folder stopped. Start it again before Plex plays imported titles.";
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static ProcessStartInfo CreateStartInfo(string executable, string config, string root)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var arg in new[] { "mount", "companion:", root, "--config", config, "--read-only", "--cache-dir", Path.Combine(Path.GetDirectoryName(config)!, "media-cache"), "--vfs-cache-mode", "full", "--vfs-cache-max-size", "2G", "--dir-cache-time", "30s", "--file-perms", "0444", "--dir-perms", "0555" })
            start.ArgumentList.Add(arg);
        return start;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The listener and its port must exist before rclone probes WebDAV.
        while (!lifetime.ApplicationStarted.IsCancellationRequested) await Task.Delay(250, stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do
        {
            if (ShouldManageMount(state) && !MediaMount.IsMounted(state.MediaMountRoot, state.ClientIdentifier))
                await StartMountAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try { StopOwnedProcess(); }
        finally { _gate.Release(); }
    }

    private void StopOwnedProcess()
    {
        if (_process == null)
        {
            // The mount outlived a previous Companion process: the PID marker
            // still names the rclone it started. Stop exactly that process.
            if (TryReadOwnedPid(AppContext.BaseDirectory, out var orphanPid) && TryKillOwnedRclone(orphanPid))
            {
                ClearOwnedPid(AppContext.BaseDirectory);
            }

            return;
        }

        var exited = false;
        try
        {
            exited = TerminateProcess(_process);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
        }

        _process.Dispose();
        _process = null;
        if (exited) ClearOwnedPid(AppContext.BaseDirectory);
    }

    private static bool TryKillOwnedRclone(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (!string.Equals(process.ProcessName, "rclone", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return TerminateProcess(process);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    private static bool TerminateProcess(Process process)
    {
        if (!process.HasExited && !OperatingSystem.IsWindows())
        {
            var signal = new ProcessStartInfo("/bin/kill") { UseShellExecute = false, CreateNoWindow = true };
            signal.ArgumentList.Add("-TERM");
            signal.ArgumentList.Add(process.Id.ToString(CultureInfo.InvariantCulture));
            using var sender = Process.Start(signal);
            process.WaitForExit(3000);
        }

        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }

        process.WaitForExit(3000);
        return process.HasExited;
    }
}
