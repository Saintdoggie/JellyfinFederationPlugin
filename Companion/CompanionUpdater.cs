using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;

namespace FederationCompanion;

/// <summary>
/// Downloads the rolling <c>companion-latest</c> GitHub build and replaces
/// this process's install directory. Apply is owner-triggered from the UI;
/// status is a cheap GitHub lookup. Running from <c>dotnet run</c> is
/// detected and refused so a source tree is never overwritten.
/// </summary>
public sealed class CompanionUpdater
{
    private readonly HttpClient _http;

    public CompanionUpdater(HttpClient http)
    {
        _http = http;
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
        {
            _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("FederationCompanion", "1.0"));
        }
    }

    public async Task<UpdateStatus> CheckAsync(CancellationToken cancellationToken)
    {
        var local = CompanionVersion.LocalRevision();
        var installed = CompanionVersion.LooksLikeInstalledBuild(Environment.ProcessPath);
        try
        {
            using var response = await _http.GetAsync(CompanionVersion.ReleaseApiUrl(), cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new UpdateStatus(installed, false, local, null, "Could not check GitHub for a newer Companion build.");
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            var notes = doc.RootElement.TryGetProperty("body", out var body) ? body.GetString() : null;
            var remote = CompanionVersion.ParseRevisionFromReleaseNotes(notes);
            var published = doc.RootElement.TryGetProperty("published_at", out var publishedAt)
                ? publishedAt.GetString()
                : null;
            var available = remote != null && !CompanionVersion.SameRevision(local, remote);
            return new UpdateStatus(installed, available, local, remote ?? published, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new UpdateStatus(installed, false, local, null, "Could not reach GitHub to check for updates.");
        }
    }

    public async Task<(bool Success, string Message)> ApplyAsync(CancellationToken cancellationToken)
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath) || !CompanionVersion.LooksLikeInstalledBuild(processPath))
        {
            return (false, "This copy was started from source (`dotnet run`), not an installed build. Re-run the install script, or use Update in a Companion installed to ~/FederationCompanion.");
        }

        var installDir = Path.GetDirectoryName(processPath);
        if (string.IsNullOrWhiteSpace(installDir) || !Directory.Exists(installDir))
        {
            return (false, "Could not find the Companion install folder.");
        }

        var staging = Path.Combine(Path.GetTempPath(), "federation-companion-update-" + Guid.NewGuid().ToString("N"));
        var zipPath = staging + ".zip";
        try
        {
            await using (var remote = await _http.GetStreamAsync(CompanionVersion.AssetDownloadUrl(), cancellationToken).ConfigureAwait(false))
            await using (var file = File.Create(zipPath))
            {
                await remote.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
            }

            Directory.CreateDirectory(staging);
            ZipFile.ExtractToDirectory(zipPath, staging, overwriteFiles: true);

            var script = WriteRestartScript(installDir, staging, processPath, Environment.ProcessId);
            LaunchRestarter(script);
            return (true, "Update downloaded. Companion will restart in a moment — refresh this page after it comes back.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidDataException)
        {
            TryDelete(staging);
            TryDelete(zipPath);
            return (false, "Could not download or unpack the new Companion build. Check your internet connection and try again.");
        }
    }

    internal static string WriteRestartScript(string installDir, string stagingDir, string processPath, int pid)
    {
        if (OperatingSystem.IsWindows())
        {
            var cmd = Path.Combine(installDir, "companion-restart-" + Guid.NewGuid().ToString("N") + ".cmd");
            File.WriteAllText(cmd, WindowsRestartCommands(installDir, stagingDir, Path.GetFileName(processPath), pid));
            return cmd;
        }

        var sh = Path.Combine(installDir, "companion-restart-" + Guid.NewGuid().ToString("N") + ".sh");
        var unixExe = Path.GetFileName(processPath);
        File.WriteAllText(sh, $"""
            #!/bin/sh
            while kill -0 {pid} 2>/dev/null; do sleep 0.2; done
            set -e
            cp -R {ShellQuote(stagingDir + "/.")} {ShellQuote(installDir + "/")}
            rm -rf -- {ShellQuote(stagingDir)}
            chmod +x {ShellQuote(installDir + "/" + unixExe)}
            cd {ShellQuote(installDir)}
            rm -- "$0"
            exec {ShellQuote("./" + unixExe)} --background
            """);
        try
        {
            File.SetUnixFileMode(sh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch (PlatformNotSupportedException)
        {
        }

        return sh;
    }

    internal static string ShellQuote(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    internal static string WindowsRestartCommands(string installDir, string stagingDir, string exe, int pid)
    {
        // Percent expansion occurs inside batch quotes; reject ambiguous paths
        // instead of constructing commands from them.
        if (new[] { installDir, stagingDir, exe }.Any(p => p.Any(c => c is '%' or '"' or '\r' or '\n')))
            throw new InvalidDataException("Move Companion to a folder without percent signs, quotes or line breaks before updating.");
        return $"""
        @echo off
        setlocal DisableDelayedExpansion
        :wait
        timeout /t 1 /nobreak >nul
        tasklist /FI "PID eq {pid}" | find "{pid}" >nul && goto wait
        {WindowsStopOwnedRcloneCommands(installDir)}
        timeout /t 2 /nobreak >nul
        xcopy /E /Y /Q "{stagingDir}\*" "{installDir}\"
        if errorlevel 1 exit /b 1
        rmdir /S /Q "{stagingDir}"
        cd /d "{installDir}"
        start "" "{exe}" --background
        del "%~f0"
        """;
    }

    internal static string WindowsStopOwnedRcloneCommands(string installDir)
    {
        return "rem The host stops its verified media helper before exiting. Never kill a process from a bare PID marker.";
    }

    private static void LaunchRestarter(string script)
    {
        var start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", $"/c \"{script}\"")
            : new ProcessStartInfo("/bin/sh", $"\"{script}\"");
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.WorkingDirectory = Path.GetTempPath();
        Process.Start(start);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            else if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}

public sealed record UpdateStatus(
    bool InstalledBuild,
    bool UpdateAvailable,
    string? LocalRevision,
    string? RemoteRevision,
    string? Error);
