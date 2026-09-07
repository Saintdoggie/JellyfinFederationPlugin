using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace FederationCompanion;

/// <summary>
/// Downloads a pinned WinFsp MSI and asks Windows to install it. A kernel
/// driver cannot be silent without elevation; the owner sees one UAC prompt.
/// </summary>
public sealed class WinFspInstaller
{
    // 2.2B4 is used instead of 2.1 because 2.1.25156 and older have local
    // privilege-escalation CVEs (CVE-2026-3006 / CVE-2026-7162).
    public const string FileName = "winfsp-2.2.26215.msi";
    public const string DownloadUrl = "https://github.com/winfsp/winfsp/releases/download/v2.2B4/winfsp-2.2.26215.msi";
    public const string Sha256 = "2ecb5c89405488a95bbd8a01875e02c48534fd37bbdfd84488f7590464d65944";
    internal const int MaxMsiBytes = 20 * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly string _directory;
    private readonly bool _windows;
    private int _attempted;

    public WinFspInstaller(HttpClient http)
        : this(http, Path.GetTempPath())
    {
    }

    internal WinFspInstaller(HttpClient http, string directory, Func<string, bool>? runInstaller = null, Func<bool>? driverInstalled = null, bool? windows = null)
    {
        _http = http;
        _directory = directory;
        _windows = windows ?? OperatingSystem.IsWindows();
        RunInstaller = runInstaller ?? LaunchMsiexec;
        DriverInstalled = driverInstalled ?? FilesystemDriver.IsAvailable;
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
        {
            _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("FederationCompanion", "1.0"));
        }
    }

    internal Func<string, bool> RunInstaller { get; }
    internal Func<bool> DriverInstalled { get; }

    public async Task<bool> EnsureAsync(CancellationToken cancellationToken)
    {
        if (DriverInstalled())
        {
            return true;
        }

        if (!_windows)
        {
            return false;
        }

        if (Interlocked.Exchange(ref _attempted, 1) == 1)
        {
            return DriverInstalled();
        }

        Directory.CreateDirectory(_directory);
        var msi = Path.Combine(_directory, FileName);
        try
        {
            using var response = await _http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            if (response.Content.Headers.ContentLength is > MaxMsiBytes)
            {
                return false;
            }

            await using (var file = new FileStream(msi, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await response.Content.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
            }

            var info = new FileInfo(msi);
            if (info.Length is <= 0 or > MaxMsiBytes)
            {
                return false;
            }

            var actual = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(msi, cancellationToken).ConfigureAwait(false)));
            if (!string.Equals(actual, Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return RunInstaller(msi) && DriverInstalled();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }

    internal static ProcessStartInfo CreateInstallStartInfo(string msi)
        => new()
        {
            FileName = "msiexec.exe",
            UseShellExecute = true,
            Verb = "runas",
            Arguments = "/i \"" + msi + "\" /qn /norestart"
        };

    private static bool LaunchMsiexec(string msi)
    {
        try
        {
            using var process = Process.Start(CreateInstallStartInfo(msi));
            if (process == null)
            {
                return false;
            }

            process.WaitForExit(120_000);
            return process.HasExited && process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
