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

    private static async Task DrainAndDisposeAsync(Process process)
    {
        using (process)
        {
            if (!process.StartInfo.RedirectStandardOutput) return;
            try
            {
                await Task.WhenAll(process.StandardOutput.BaseStream.CopyToAsync(Stream.Null),
                    process.StandardError.BaseStream.CopyToAsync(Stream.Null));
            }
            catch (IOException) { }
        }
    }

    internal static bool HasDesktopSession()
        => OperatingSystem.IsWindows()
            || OperatingSystem.IsMacOS()
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"));

    internal static bool OpenUrl(string target)
    {
        if (!HasDesktopSession())
        {
            return false;
        }

        try
        {
            var start = OperatingSystem.IsWindows()
                ? new ProcessStartInfo(target) { UseShellExecute = true }
                : new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            if (!OperatingSystem.IsWindows()) start.ArgumentList.Add(target);
            var process = Process.Start(start);
            if (process != null) _ = DrainAndDisposeAsync(process);
            return process != null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            return false;
        }
    }
}
