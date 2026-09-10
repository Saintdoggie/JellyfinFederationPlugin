using System.Globalization;

namespace FederationCompanion;

/// <summary>
/// Minimal owner-facing log beside the executable. The Windows build runs as a
/// desktop app with no console, so startup, mount, update and shutdown
/// failures must land somewhere the tray can open. Never write credentials,
/// tokens or upstream URLs here - see the project invariants in AGENTS.md.
/// </summary>
public static class AppLog
{
    private const long MaxBytes = 2 * 1024 * 1024;
    private static readonly object Gate = new();
    private static string? _path;

    public static string LogPath
    {
        get
        {
            lock (Gate)
            {
                return _path ??= Path.Combine(AppContext.BaseDirectory, "companion.log");
            }
        }
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? exception = null)
        => Write("ERROR", exception == null ? message : $"{message} {exception.GetType().Name}");

    internal static string FormatLine(DateTimeOffset timestamp, string level, string message)
        => $"{timestamp.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)} [{level}] {message}{Environment.NewLine}";

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                var path = LogPath;
                Rotate(path);
                File.AppendAllText(path, FormatLine(DateTimeOffset.Now, level, message));
                RestrictPermissions(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Logging must never take the app down.
        }
    }

    private static void Rotate(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length <= MaxBytes)
        {
            return;
        }

        var previous = path + ".1";
        try
        {
            if (File.Exists(previous))
            {
                File.Delete(previous);
            }

            File.Move(path, previous);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void RestrictPermissions(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or IOException or UnauthorizedAccessException)
        {
        }
    }
}
