using System.IO.Compression;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace FederationCompanion;

/// <summary>
/// Finds or downloads a pinned rclone binary next to Companion. Plex owners
/// should not have to know what rclone is; the local mount button uses this.
/// </summary>
public sealed class RcloneBootstrapper
{
    public const string Version = "1.75.1";
    internal const int MaxArchiveBytes = 80 * 1024 * 1024;

    internal static readonly IReadOnlyDictionary<string, string> ArchiveSha256 = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["linux-amd64"] = "982b5aa772841168f8e380f139e9e787b2a105403e32b94da8676a0e1c0a13ab",
        ["linux-arm64"] = "03f2504174034b6d004152ed7369251c9a9ec1f7e0836eda420f5c7a5ec0dff9",
        ["osx-amd64"] = "29253d0288b8fbbac46baad6e5f6add6cb01d462c79f10805bbd4631c4cdf82c",
        ["osx-arm64"] = "c61d7a371c62bcbbe882c3423aa4b8bf63485c248dd0f692997b8f0c3f6d0c6f",
        ["windows-amd64"] = "200eb602c126d82aa38b51e0f6b9ae837473ff99b51278d3f6f837574c494d6e",
        ["windows-arm64"] = "c3c6cd0424dd49076ad179c30c3f9e5cde2c004ec07ea9fe6911f23e32eafe0f",
    };

    private readonly HttpClient _http;
    private readonly string _installDirectory;
    private readonly IReadOnlyDictionary<string, string> _sha256;

    public RcloneBootstrapper(HttpClient http)
        : this(http, AppContext.BaseDirectory, ArchiveSha256)
    {
    }

    internal RcloneBootstrapper(HttpClient http, string installDirectory, IReadOnlyDictionary<string, string>? sha256 = null, long minimumBinaryBytes = 1_000_000)
    {
        _http = http;
        _installDirectory = installDirectory;
        _sha256 = sha256 ?? ArchiveSha256;
        MinimumBinaryBytes = minimumBinaryBytes;
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
        {
            _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("FederationCompanion", Version));
        }
    }

    internal long MinimumBinaryBytes { get; }

    public static string BinaryName() => OperatingSystem.IsWindows() ? "rclone.exe" : "rclone";

    public static string ArchiveOsArch()
    {
        var arm = RuntimeInformation.ProcessArchitecture is Architecture.Arm64 or Architecture.Arm;
        if (OperatingSystem.IsWindows())
        {
            return arm ? "windows-arm64" : "windows-amd64";
        }

        if (OperatingSystem.IsMacOS())
        {
            return arm ? "osx-arm64" : "osx-amd64";
        }

        return arm ? "linux-arm64" : "linux-amd64";
    }

    public static string ArchiveFileName(string? osArch = null)
        => $"rclone-v{Version}-{osArch ?? ArchiveOsArch()}.zip";

    public static string DownloadUrl(string? osArch = null)
        => $"https://downloads.rclone.org/v{Version}/{ArchiveFileName(osArch)}";

    public string? FindExisting()
    {
        var bundled = Path.Combine(_installDirectory, BinaryName());
        return IsUsable(bundled) ? bundled : FindOnPath();
    }

    public async Task<(bool Success, string? Executable, string Message)> EnsureAsync(CancellationToken cancellationToken)
    {
        var existing = FindExisting();
        if (existing != null)
        {
            return (true, existing, "Media helper is ready.");
        }

        var osArch = ArchiveOsArch();
        if (!_sha256.TryGetValue(osArch, out var expected) || expected.Length != 64)
        {
            return (false, null, "This computer's architecture is not supported for the automatic media helper download.");
        }

        Directory.CreateDirectory(_installDirectory);
        var zipPath = Path.Combine(_installDirectory, ArchiveFileName(osArch) + ".part");
        try
        {
            using (var remote = await _http.GetAsync(DownloadUrl(osArch), HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                if (!remote.IsSuccessStatusCode)
                {
                    return (false, null, "Could not download the media helper. Check this computer is online, then retry.");
                }

                if (remote.Content.Headers.ContentLength is > MaxArchiveBytes)
                {
                    return (false, null, "The media helper download was larger than expected. Retry in a moment.");
                }

                await using var file = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None);
                await CopyLimitedAsync(await remote.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), file, MaxArchiveBytes, cancellationToken).ConfigureAwait(false);
            }

            var actual = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(zipPath, cancellationToken).ConfigureAwait(false)));
            var expectedBytes = ParseHex(expected);
            var actualBytes = ParseHex(actual);
            if (expectedBytes.Length == 0 || expectedBytes.Length != actualBytes.Length
                || !CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes))
            {
                return (false, null, "The media helper download was corrupted. Retry in a moment.");
            }

            var extracted = await ExtractBinaryAsync(zipPath, cancellationToken).ConfigureAwait(false);
            if (extracted == null)
            {
                return (false, null, "The media helper download was incomplete. Retry in a moment.");
            }

            return (true, extracted, "Media helper downloaded.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return (false, null, "Could not download the media helper. Check this computer is online, then retry.");
        }
        finally
        {
            TryDelete(zipPath);
        }
    }

    internal static string? FindBinaryEntryName(IEnumerable<string> names)
    {
        foreach (var name in names)
        {
            var normalized = name.Replace('\\', '/');
            if (normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(part => part == ".."))
            {
                continue;
            }

            var file = Path.GetFileName(normalized);
            if (file.Equals("rclone", StringComparison.OrdinalIgnoreCase)
                || file.Equals("rclone.exe", StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }
        }

        return null;
    }

    internal string? FindOnPath()
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var exe = BinaryName();
        foreach (var dir in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir))
            {
                continue;
            }

            try
            {
                var candidate = Path.Combine(dir.Trim().Trim('"'), exe);
                if (IsUsable(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
            }
        }

        return null;
    }

    internal bool IsUsable(string path)
    {
        try
        {
            return File.Exists(path) && new FileInfo(path).Length >= MinimumBinaryBytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task<string?> ExtractBinaryAsync(string zipPath, CancellationToken cancellationToken)
    {
        await using var input = File.OpenRead(zipPath);
        using var zip = new ZipArchive(input, ZipArchiveMode.Read);
        var entryName = FindBinaryEntryName(zip.Entries.Select(e => e.FullName));
        if (entryName == null)
        {
            return null;
        }

        var entry = zip.GetEntry(entryName);
        if (entry == null || entry.Length <= 0 || entry.Length > MaxArchiveBytes)
        {
            return null;
        }

        var destination = Path.Combine(_installDirectory, BinaryName());
        var temp = destination + ".part";
        await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        await using (var source = entry.Open())
        {
            await CopyLimitedAsync(source, output, MaxArchiveBytes, cancellationToken).ConfigureAwait(false);
        }

        if (File.Exists(destination))
        {
            File.Delete(destination);
        }

        File.Move(temp, destination);
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(destination, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            catch (PlatformNotSupportedException)
            {
            }
        }

        return IsUsable(destination) ? destination : null;
    }

    private static async Task CopyLimitedAsync(Stream source, Stream destination, int maxBytes, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        var total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return;
            }

            total += read;
            if (total > maxBytes)
            {
                throw new InvalidDataException("Archive exceeded the size limit.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    private static byte[] ParseHex(string hex)
    {
        var normalized = hex.Trim().Replace(" ", "", StringComparison.Ordinal);
        if (normalized.Length % 2 != 0)
        {
            return Array.Empty<byte>();
        }

        try
        {
            return Convert.FromHexString(normalized);
        }
        catch (FormatException)
        {
            return Array.Empty<byte>();
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>Detects the OS filesystem driver rclone mount needs. WinFsp cannot be silently installed.</summary>
public static class FilesystemDriver
{
    public static string Name => OperatingSystem.IsWindows() ? "WinFsp" : OperatingSystem.IsMacOS() ? "macFUSE" : "FUSE";

    public static string HelpUrl => OperatingSystem.IsWindows()
        ? "https://winfsp.dev/rel/"
        : OperatingSystem.IsMacOS()
            ? "https://osxfuse.github.io/"
            : "https://rclone.org/commands/rclone_mount/";

    public static bool IsAvailable()
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var root in WindowsProgramFiles())
            {
                if (File.Exists(Path.Combine(root, "WinFsp", "bin", "winfsp-x64.dll"))
                    || File.Exists(Path.Combine(root, "WinFsp", "bin", "winfsp-a64.dll"))
                    || File.Exists(Path.Combine(root, "WinFsp", "bin", "winfsp.dll")))
                {
                    return true;
                }
            }

            return false;
        }

        if (OperatingSystem.IsMacOS())
        {
            return Directory.Exists("/Library/Filesystems/macfuse.fs")
                || File.Exists("/usr/local/lib/libfuse.dylib")
                || File.Exists("/opt/homebrew/lib/libfuse.dylib");
        }

        return File.Exists("/dev/fuse");
    }

    internal const string WindowsMissing =
        "Install WinFsp once (a small Windows driver so Plex can see the media folder), then retry. Download it from https://winfsp.dev/rel/ — run Companion with the same Windows account as Plex.";

    public static string MissingMessage()
    {
        if (OperatingSystem.IsWindows())
        {
            return WindowsMissing;
        }

        if (OperatingSystem.IsMacOS())
        {
            return "Install macFUSE once so Plex can see the media folder, then retry. https://osxfuse.github.io/";
        }

        return "This computer needs FUSE so Plex can read the media folder. Install fuse3, then retry.";
    }

    internal static string ClassifyFailure(string? processOutput)
    {
        var text = processOutput ?? "";
        if (text.Contains("winfsp", StringComparison.OrdinalIgnoreCase))
        {
            return WindowsMissing;
        }

        if (text.Contains("macfuse", StringComparison.OrdinalIgnoreCase)
            || text.Contains("osxfuse", StringComparison.OrdinalIgnoreCase))
        {
            return "Install macFUSE once so Plex can see the media folder, then retry. https://osxfuse.github.io/";
        }

        if (text.Contains("fusermount", StringComparison.OrdinalIgnoreCase)
            || text.Contains("/dev/fuse", StringComparison.OrdinalIgnoreCase))
        {
            return "This computer needs FUSE so Plex can read the media folder. Install fuse3, then retry.";
        }

        return OperatingSystem.IsWindows()
            ? "The media mount did not start. Install WinFsp, restart Companion, and retry. Run Companion under the same Windows account as Plex."
            : "The media mount did not start. Check FUSE is installed and available to this user, then retry.";
    }

    private static IEnumerable<string> WindowsProgramFiles()
    {
        foreach (var path in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     @"C:\Program Files",
                     @"C:\Program Files (x86)"
                 })
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                yield return path;
            }
        }
    }
}
