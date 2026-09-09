#if WINDOWS
using Microsoft.Win32;

namespace FederationCompanion;

/// <summary>
/// Per-user sign-in entry under <c>HKCU\...\CurrentVersion\Run</c>. No
/// elevation and no machine-wide changes: only this Windows account starts
/// Companion, matching the tray app's single-owner model.
/// </summary>
public sealed class WindowsAutostartRegistration : IAutostartRegistration
{
    internal const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>
    /// Only an installed apphost (not <c>dotnet run</c>) can register itself:
    /// <see cref="Environment.ProcessPath"/> would otherwise be the shared dotnet
    /// host without the application argument.
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
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            var stored = key?.GetValue(AutostartCommand.WindowsRunValueName) as string;
            return stored != null
                && string.Equals(stored.Trim(), AutostartCommand.Build(executable), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
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
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key == null)
            {
                return false;
            }

            if (enabled)
            {
                key.SetValue(AutostartCommand.WindowsRunValueName, AutostartCommand.Build(executable), RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(AutostartCommand.WindowsRunValueName, throwOnMissingValue: false);
            }

            return IsEnabled() == enabled;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }
}
#endif
