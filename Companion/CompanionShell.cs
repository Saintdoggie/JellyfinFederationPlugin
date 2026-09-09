using System.Diagnostics;

namespace FederationCompanion;

/// <summary>Opens the owner dashboard in the default browser, cross-platform.</summary>
public static class CompanionShell
{
    public static bool OpenDashboard(CompanionRuntime runtime, string adminAccessKey)
    {
        if (runtime.Port <= 0 || string.IsNullOrWhiteSpace(adminAccessKey))
        {
            return false;
        }

        return OpenUrl(runtime.DashboardUrl(adminAccessKey));
    }

    public static bool OpenFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return false;
        }

        return OpenUrl(path);
    }

    internal static bool OpenUrl(string target)
    {
        try
        {
            var start = OperatingSystem.IsWindows()
                ? new ProcessStartInfo(target) { UseShellExecute = true }
                : OperatingSystem.IsMacOS()
                    ? new ProcessStartInfo("open", $"\"{target}\"") { UseShellExecute = false, CreateNoWindow = true }
                    : new ProcessStartInfo("xdg-open", $"\"{target}\"") { UseShellExecute = false, CreateNoWindow = true };
            using var process = Process.Start(start);
            return process != null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            return false;
        }
    }
}
