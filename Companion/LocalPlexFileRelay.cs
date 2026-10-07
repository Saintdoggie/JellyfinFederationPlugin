using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Win32.SafeHandles;

namespace FederationCompanion;

/// <summary>
/// Opt-in serving of the owner's original files. Plex supplies catalog metadata,
/// never the video bytes. No Plex stream/transcode entitlement is used here.
/// </summary>
public static class LocalPlexFileRelay
{
    public static bool Supported => OperatingSystem.IsLinux() || OperatingSystem.IsWindows();

    public static IEnumerable<string> CandidateRoots(CompanionState state)
        => state.Libraries.Where(l => !CompanionLibraryPolicy.IsImported(state, l))
            .SelectMany(l => l.Locations).Where(IsSafeSourcePath).Distinct(StringComparer.Ordinal);

    public static string LocalRoot(CompanionState state, string sourceRoot)
        => state.LocalFileRootMappings.FirstOrDefault(m => SourceEquals(m.PlexRoot, sourceRoot))?.LocalRoot ?? sourceRoot;

    public static bool RootAvailable(string localRoot) => IsSafeLocalRoot(localRoot) && Directory.Exists(localRoot);

    public static IEnumerable<string> SharedRoots(CompanionState state)
        => state.Libraries.Where(l => CompanionLibraryPolicy.IsShared(state, l))
            .SelectMany(l => l.Locations).Where(IsSafeSourcePath).Distinct(StringComparer.Ordinal);

    public static bool IsSafeSourcePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0')) return false;
        var normalized = path.Replace('\\', '/');
        if (!(normalized.StartsWith('/') || normalized.Length > 3 && char.IsAsciiLetter(normalized[0]) && normalized[1..3] == ":/")) return false;
        if (normalized.StartsWith("//?/", StringComparison.Ordinal) || normalized.StartsWith("//./", StringComparison.Ordinal)) return false;
        return !normalized.Split('/').Any(p => p is "." or "..");
    }

    public static bool IsSafeLocalRoot(string? root)
    {
        if (!IsSafeSourcePath(root) || !Path.IsPathFullyQualified(root!)) return false;
        var full = Path.GetFullPath(root!);
        return !string.Equals(full.TrimEnd(Path.DirectorySeparatorChar), Path.GetPathRoot(full)!.TrimEnd(Path.DirectorySeparatorChar), PathComparison);
    }

    public static async Task RelayAsync(CompanionState state, byte[] metadata, string partKey, HttpContext context, CancellationToken cancellationToken)
    {
        if (!Supported) { context.Response.StatusCode = 501; return; }
        if (!TryFindPart(metadata, partKey, out var section, out var sourcePath, out var size)
            || !TryResolvePath(state, section, sourcePath, out var root, out var file))
        { context.Response.StatusCode = 403; return; }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var stream = OpenOwnedFile(root, file);
            if (IsImportedPath(state, OpenedPath(stream.SafeFileHandle)))
            { context.Response.StatusCode = 403; return; }
            if (!state.LocalFileRelayEnabled || !state.Libraries.Any(l => l.SectionKey == section && CompanionLibraryPolicy.IsShared(state, l)))
            { context.Response.StatusCode = 403; return; }
            if (size <= 0 || stream.Length != size)
            { context.Response.StatusCode = 409; return; } // Do not serve bytes from a stale/mismatched Plex part.
            context.Response.Headers.CacheControl = "private, no-store";
            await Results.Stream(stream, ContentType(file), lastModified: File.GetLastWriteTimeUtc(stream.SafeFileHandle),
                enableRangeProcessing: true).ExecuteAsync(context).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException) when (!context.Response.HasStarted) { context.Response.StatusCode = 403; }
        catch (IOException) when (!context.Response.HasStarted) { context.Response.StatusCode = 404; }
        catch (NotSupportedException) when (!context.Response.HasStarted) { context.Response.StatusCode = 404; }
    }

    internal static bool TryFindPart(byte[] body, string key, out string section, out string file, out long size)
    {
        section = file = string.Empty; size = 0;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var container = doc.RootElement.GetProperty("MediaContainer");
            foreach (var item in container.GetProperty("Metadata").EnumerateArray())
            {
                var sectionId = item.TryGetProperty("librarySectionID", out var id) ? Text(id)
                    : container.TryGetProperty("librarySectionID", out id) ? Text(id) : string.Empty;
                if (!item.TryGetProperty("Media", out var media) || media.ValueKind != JsonValueKind.Array) continue;
                foreach (var source in media.EnumerateArray())
                {
                    if (!source.TryGetProperty("Part", out var parts) || parts.ValueKind != JsonValueKind.Array) continue;
                    foreach (var part in parts.EnumerateArray())
                    {
                        if (!part.TryGetProperty("key", out var partKey) || Text(partKey).Split('?', 2)[0] != key
                            || !part.TryGetProperty("file", out var path) || !IsSafeSourcePath(Text(path))
                            || !part.TryGetProperty("size", out var length) || !long.TryParse(Text(length), out size) || size <= 0) continue;
                        section = sectionId; file = Text(path); return !string.IsNullOrEmpty(section);
                    }
                }
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException) { }
        return false;
    }

    private static string Text(JsonElement value) => value.ValueKind switch {
        JsonValueKind.String => value.GetString() ?? string.Empty, JsonValueKind.Number => value.GetRawText(), _ => string.Empty };

    internal static bool TryResolvePath(CompanionState state, string section, string sourceFile, out string root, out string file)
    {
        root = file = string.Empty;
        var library = state.Libraries.FirstOrDefault(l => l.SectionKey == section && CompanionLibraryPolicy.IsShared(state, l));
        if (!state.LocalFileRelayEnabled || library == null || !IsSafeSourcePath(sourceFile) || IsImportedPath(state, sourceFile)) return false;
        foreach (var sourceRoot in library.Locations.Where(IsSafeSourcePath).OrderByDescending(p => p.Length))
        {
            if (!IsWithinSource(sourceFile, sourceRoot)) continue;
            var mapping = state.LocalFileRootMappings.FirstOrDefault(m => SourceEquals(m.PlexRoot, sourceRoot));
            var localRoot = mapping?.LocalRoot ?? sourceRoot;
            if (!IsSafeLocalRoot(localRoot) || !Directory.Exists(localRoot)) continue;
            var relative = sourceFile.Replace('\\', '/')[sourceRoot.Replace('\\', '/').TrimEnd('/').Length..].TrimStart('/');
            var candidate = Path.GetFullPath(Path.Combine(localRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
            var normalizedRoot = Path.GetFullPath(localRoot).TrimEnd(Path.DirectorySeparatorChar);
            if (!candidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, PathComparison)
                || ContentType(candidate) == null || IsImportedPath(state, candidate)) return false;
            root = normalizedRoot; file = candidate; return true;
        }
        return false;
    }

    private static bool IsImportedPath(CompanionState state, string file)
    {
        var roots = state.ImportPeers.Select(p => p.ExportPath)
            .Concat(new[] { state.PlexVisibleImportRoot, state.PlexMountRoot, state.MediaMountRoot, Path.Combine(AppContext.BaseDirectory, "imported") });
        foreach (var root in roots.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            if (IsWithinSource(file, root!) || SourceEquals(file, root!)) return true;
            if (IsSafeSourcePath(root) && Path.IsPathFullyQualified(root!) && Directory.Exists(root))
            {
                try { var canonical = CanonicalDirectory(root!); if (IsWithinSource(file, canonical) || SourceEquals(file, canonical)) return true; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return true; }
            }
        }
        return false;
    }

    private static bool IsWithinSource(string file, string root)
        => file.Replace('\\', '/').StartsWith(root.Replace('\\', '/').TrimEnd('/') + "/", SourceComparison(root));
    private static bool SourceEquals(string a, string b)
        => string.Equals(a.Replace('\\', '/').TrimEnd('/'), b.Replace('\\', '/').TrimEnd('/'), SourceComparison(b));
    private static StringComparison SourceComparison(string path)
        => path.Length > 1 && path[1] == ':' || path.StartsWith("\\\\", StringComparison.Ordinal) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    internal static FileStream OpenOwnedFile(string root, string file)
    {
        // Resolve aliases in the explicitly approved root, but never links below it.
        var canonicalRoot = CanonicalDirectory(root);
        var relative = Path.GetRelativePath(root, file);
        if (Path.IsPathRooted(relative) || relative.Split(Path.DirectorySeparatorChar).Any(p => p is "." or "..")) throw new UnauthorizedAccessException();
        if (OperatingSystem.IsWindows() && relative.Contains(':')) throw new UnauthorizedAccessException();
        var current = canonicalRoot;
        foreach (var component in relative.Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, component);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new UnauthorizedAccessException();
        }
        SafeFileHandle handle;
        if (OperatingSystem.IsLinux())
        {
            // Nonblocking open avoids hanging on named pipes; NOFOLLOW protects the
            // final component. Verify type and the opened handle's actual path too.
            var descriptor = Open(current, 0x800 | 0x20000 | 0x80000);
            if (descriptor < 0) throw new IOException("The approved media file could not be opened.");
            handle = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
            var stat = new byte[256];
            if (Statx(descriptor, "", 0x1000, 1, stat) != 0 || (BitConverter.ToUInt16(stat, 28) & 0xf000) != 0x8000)
            { handle.Dispose(); throw new UnauthorizedAccessException(); }
        }
        else if (OperatingSystem.IsWindows())
            handle = File.OpenHandle(current, FileMode.Open, FileAccess.Read, FileShare.Read);
        else throw new NotSupportedException();

        try
        {
            var actual = OpenedPath(handle);
            if (!actual.StartsWith(canonicalRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, PathComparison)
                || !string.Equals(actual, current, PathComparison)) throw new UnauthorizedAccessException();
            return new FileStream(handle, FileAccess.Read, 65536, isAsync: false);
        }
        catch { handle.Dispose(); throw; }
    }

    private static string CanonicalDirectory(string directory)
    {
        var absolute = Path.GetFullPath(directory);
        var current = Path.GetPathRoot(absolute)!;
        foreach (var component in absolute[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var next = new DirectoryInfo(Path.Combine(current, component));
            current = next.LinkTarget == null ? next.FullName : next.ResolveLinkTarget(true)!.FullName;
        }
        return current.TrimEnd(Path.DirectorySeparatorChar);
    }

    private static string OpenedPath(SafeFileHandle handle)
    {
        if (OperatingSystem.IsLinux())
            return new FileInfo($"/proc/self/fd/{handle.DangerousGetHandle().ToInt64()}").LinkTarget ?? throw new IOException("Cannot verify the opened media file.");
        if (GetFileType(handle) != 1) throw new UnauthorizedAccessException();
        var buffer = new StringBuilder(32768);
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity) throw new IOException("Cannot verify the opened media file.");
        var result = buffer.ToString();
        return result.StartsWith("\\\\?\\UNC\\", StringComparison.Ordinal) ? "\\\\" + result[8..]
            : result.StartsWith("\\\\?\\", StringComparison.Ordinal) ? result[4..] : result;
    }

    private static string? ContentType(string file) => Path.GetExtension(file).ToLowerInvariant() switch {
        ".mp4" or ".m4v" => "video/mp4", ".mkv" => "video/x-matroska", ".webm" => "video/webm",
        ".mov" => "video/quicktime", ".avi" => "video/x-msvideo", ".ts" or ".m2ts" => "video/mp2t",
        ".mpg" or ".mpeg" => "video/mpeg", ".wmv" => "video/x-ms-wmv", ".flv" => "video/x-flv", ".ogv" => "video/ogg", _ => null };

    [DllImport("libc", EntryPoint = "open", SetLastError = true)] private static extern int Open(string path, int flags);
    [DllImport("libc", EntryPoint = "statx", SetLastError = true)] private static extern int Statx(int fd, string path, int flags, uint mask, [Out] byte[] result);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
    [DllImport("kernel32.dll")] private static extern uint GetFileType(SafeFileHandle handle);
}

public sealed class LocalFileRootMapping
{
    public string PlexRoot { get; set; } = string.Empty;
    public string LocalRoot { get; set; } = string.Empty;
}
