using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FederationCompanion;

/// <summary>
/// Detects whether Tailscale is installed and signed in on this machine, and
/// gives OS-specific guidance for whichever step is missing. A federated
/// Plex server needs to be reachable from the internet without exposing the
/// user's home network directly - Tailscale (with Funnel for the actual
/// public HTTPS ingress) is the path this whole plugin was designed around,
/// so getting a non-technical user through that setup correctly matters as
/// much as the Plex sign-in step does.
/// <para>
/// Deliberately detection-and-guidance only, not automatic execution: this
/// app runs on a stranger's (to Tailscale) home computer, and quietly
/// shelling out to change its network configuration is a bigger trust ask
/// than showing a copy-pasteable command the user reviews and runs
/// themselves.
/// </para>
/// </summary>
public static class TailscaleHelper
{
    public static async Task<TailscaleStatus> CheckAsync(CancellationToken cancellationToken)
    {
        var binaryPath = FindBinary();
        if (binaryPath == null)
        {
            return new TailscaleStatus(Installed: false, SignedIn: false, InstallCommand: GetInstallCommand());
        }

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(binaryPath, "status --json")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                }
            };

            process.Start();
            var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            // Exit code 0 with actual JSON back means signed in and running;
            // anything else (including "not logged in" and "not running")
            // exits non-zero, which is all this needs to distinguish -
            // parsing the JSON further would only matter for showing which
            // tailnet, and this app doesn't need that.
            var signedIn = process.ExitCode == 0 && !string.IsNullOrWhiteSpace(stdout);
            return new TailscaleStatus(Installed: true, SignedIn: signedIn, InstallCommand: null);
        }
        catch (Exception)
        {
            // Binary found on disk but couldn't actually run it (permissions,
            // daemon not started, etc.) - treat the same as "needs sign-in",
            // since the fix (run `tailscale up`) is the same either way.
            return new TailscaleStatus(Installed: true, SignedIn: false, InstallCommand: null);
        }
    }

    private static string? FindBinary()
    {
        var candidates = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new[] { @"C:\Program Files\Tailscale\tailscale.exe", @"C:\Program Files (x86)\Tailscale\tailscale.exe" }
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ? new[] { "/Applications/Tailscale.app/Contents/MacOS/Tailscale", "/usr/local/bin/tailscale", "/opt/homebrew/bin/tailscale" }
                : new[] { "/usr/bin/tailscale", "/usr/sbin/tailscale", "/usr/local/bin/tailscale" };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        // Fall back to PATH lookup - covers the common case where it's
        // installed somewhere not in the fixed list above but still callable
        // by name.
        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "tailscale.exe" : "tailscale";
    }

    private static string GetInstallCommand()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return "winget install -e --id Tailscale.Tailscale; tailscale up";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return "brew install --cask tailscale; tailscale up";
        }

        return "curl -fsSL https://tailscale.com/install.sh | sh && sudo tailscale up";
    }
}

public sealed record TailscaleStatus(bool Installed, bool SignedIn, string? InstallCommand);
