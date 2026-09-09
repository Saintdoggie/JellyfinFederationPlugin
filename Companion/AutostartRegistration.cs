namespace FederationCompanion;

/// <summary>
/// "Start Companion when I sign in" for the current user. Windows uses the
/// per-user Run key; Linux uses the XDG autostart directory; macOS is not
/// implemented yet and reports unsupported so the UI hides the control.
/// </summary>
public interface IAutostartRegistration
{
    bool IsSupported { get; }

    bool IsEnabled();

    bool Set(bool enabled);
}

public static class AutostartCommand
{
    public const string WindowsRunValueName = "FederationCompanion";

    /// <summary>
    /// Command stored in a per-user startup entry. The executable is quoted
    /// (the install folder can contain spaces) and <c>--tray</c> keeps sign-in
    /// quiet: no browser popup, just the background app.
    /// </summary>
    public static string Build(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new ArgumentException("An executable path is required.", nameof(executablePath));
        }

        return $"\"{executablePath.Trim()}\" --tray";
    }
}

/// <summary>XDG autostart entry shared by Linux desktops.</summary>
public static class LinuxAutostart
{
    internal const string FileName = "federation-companion.desktop";

    public static string EntryPath
        => Path.Combine(
            Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } config ? config : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
            "autostart",
            FileName);

    public static string BuildDesktopEntry(string executablePath)
        => $"""
            [Desktop Entry]
            Type=Application
            Name=Federation Companion
            Comment=Start the Plex and Jellyfin federation bridge in the background
            Exec={AutostartCommand.Build(executablePath)}
            Terminal=false
            X-GNOME-Autostart-enabled=true
            """;
}

public sealed class LinuxAutostartRegistration : IAutostartRegistration
{
    /// <summary>
    /// Only an installed apphost (not <c>dotnet run</c> or <c>dotnet app.dll</c>)
    /// can register itself: <see cref="Environment.ProcessPath"/> would otherwise
    /// be the shared dotnet host without the application argument.
    /// </summary>
    public bool IsSupported => CompanionVersion.LooksLikeInstalledBuild(Environment.ProcessPath);

    public bool IsEnabled()
    {
        var executable = Environment.ProcessPath;
        if (!IsSupported || string.IsNullOrWhiteSpace(executable))
        {
            return false;
        }

        try
        {
            return File.Exists(LinuxAutostart.EntryPath)
                && File.ReadAllText(LinuxAutostart.EntryPath).Contains(
                    LinuxAutostart.BuildDesktopEntry(executable),
                    StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public bool Set(bool enabled)
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            return false;
        }

        try
        {
            if (enabled)
            {
                var directory = Path.GetDirectoryName(LinuxAutostart.EntryPath)!;
                Directory.CreateDirectory(directory);
                File.WriteAllText(LinuxAutostart.EntryPath, LinuxAutostart.BuildDesktopEntry(executable));
            }
            else if (File.Exists(LinuxAutostart.EntryPath))
            {
                File.Delete(LinuxAutostart.EntryPath);
            }

            return IsEnabled() == enabled;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

public sealed class UnsupportedAutostartRegistration : IAutostartRegistration
{
    public bool IsSupported => false;

    public bool IsEnabled() => false;

    public bool Set(bool enabled) => false;
}
