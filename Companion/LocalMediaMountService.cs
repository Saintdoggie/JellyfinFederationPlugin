using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Hosting;

namespace FederationCompanion;

/// <summary>Owns only the rclone process started by Companion, never an existing user mount.</summary>
public sealed class LocalMediaMountService(CompanionState state, IHostApplicationLifetime lifetime) : BackgroundService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    public string Message { get; private set; } = "Install rclone and the filesystem driver, then start the local media mount.";

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
            var executable = OperatingSystem.IsWindows() ? "rclone.exe" : "rclone";
            var bundled = Path.Combine(AppContext.BaseDirectory, executable);
            var start = CreateStartInfo(File.Exists(bundled) ? bundled : executable, config, root);
            _process = Process.Start(start) ?? throw new InvalidOperationException("Mount process did not start.");
            // Drain without logging: rclone errors can contain item URLs. The
            // owner receives a stable repair message instead of raw process text.
            _process.OutputDataReceived += (_, _) => { };
            _process.ErrorDataReceived += (_, _) => { };
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
            StopOwnedProcess();
            Message = OperatingSystem.IsWindows()
                ? "Mount did not start. Install WinFsp, restart Companion, and retry. Run Companion under the same Windows account as Plex."
                : "Mount did not start. Check FUSE is installed and available to this user, then retry.";
            return false;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            StopOwnedProcess();
            Message = "Could not start rclone. Install it on PATH or place its executable beside Companion, then retry.";
            return false;
        }
        catch (OperationCanceledException) { StopOwnedProcess(); throw; }
        finally { _gate.Release(); }
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
            if (state.AutoStartMediaMount && !MediaMount.IsMounted(state.MediaMountRoot, state.ClientIdentifier))
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
        if (_process == null) return;
        try
        {
            if (!_process.HasExited && !OperatingSystem.IsWindows())
            {
                var signal = new ProcessStartInfo("/bin/kill") { UseShellExecute = false, CreateNoWindow = true };
                signal.ArgumentList.Add("-TERM"); signal.ArgumentList.Add(_process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
                using var sender = Process.Start(signal);
                _process.WaitForExit(3000);
            }
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
        _process.Dispose();
        _process = null;
    }
}
