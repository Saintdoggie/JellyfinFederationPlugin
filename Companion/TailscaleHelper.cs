using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

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
/// Install/sign-in stay copy-paste guidance. Funnel is an explicit UI
/// button: Starlink/CGNAT friends have no port-forward, and Funnel HTTPS
/// certificates must be requested or TLS dies while DNS still looks live.
/// </para>
/// </summary>
public static class TailscaleHelper
{
    public static async Task<TailscaleStatus> CheckAsync(CancellationToken cancellationToken)
    {
        var binaryPath = FindBinary();
        if (binaryPath == null)
        {
            return new TailscaleStatus(Installed: false, SignedIn: false, InstallCommand: GetInstallCommand(), DnsName: null);
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
            var dnsName = signedIn ? ReadDnsName(stdout) : null;
            return new TailscaleStatus(Installed: true, SignedIn: signedIn, InstallCommand: null, DnsName: dnsName);
        }
        catch (Exception)
        {
            // Binary found on disk but couldn't actually run it (permissions,
            // daemon not started, etc.) - treat the same as "needs sign-in",
            // since the fix (run `tailscale up`) is the same either way.
            return new TailscaleStatus(Installed: true, SignedIn: false, InstallCommand: null, DnsName: null);
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

    public static string? ReadDnsName(string statusJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(statusJson);
            if (!doc.RootElement.TryGetProperty("Self", out var self)
                || !self.TryGetProperty("DNSName", out var dns))
            {
                return null;
            }

            var name = dns.GetString()?.Trim().TrimEnd('.');
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Turns on Funnel for Companion's local listen port so off-LAN friends
    /// (Starlink, no port-forward) can reach this app at https://name.ts.net.
    /// Also requests the HTTPS certificate Tailscale Funnel needs; without it
    /// DNS and HTTP-to-HTTPS redirects work while TLS immediately EOFs.
    /// </summary>
    public static async Task<TailscaleFunnelResult> SetUpFunnelAsync(int localPort, CancellationToken cancellationToken)
    {
        var binary = FindBinary();
        if (binary == null)
        {
            return new TailscaleFunnelResult(false, null, "Tailscale is not installed.");
        }

        var status = await CheckAsync(cancellationToken).ConfigureAwait(false);
        if (!status.SignedIn)
        {
            return new TailscaleFunnelResult(false, null, "Sign in to Tailscale first (`tailscale up`), then turn Funnel on.");
        }

        if (!string.IsNullOrWhiteSpace(status.DnsName))
        {
            await RunAsync(binary, $"cert {status.DnsName}", TimeSpan.FromSeconds(45), cancellationToken).ConfigureAwait(false);
        }

        var funnel = await RunAsync(binary, $"funnel --bg {localPort}", TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        if (!funnel.Ok)
        {
            var detail = string.IsNullOrWhiteSpace(funnel.Error) ? "tailscale funnel failed." : funnel.Error;
            return new TailscaleFunnelResult(false, null, detail);
        }

        var dnsName = status.DnsName ?? ReadDnsName((await RunAsync(binary, "status --json", TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false)).Output);
        if (string.IsNullOrWhiteSpace(dnsName))
        {
            return new TailscaleFunnelResult(false, null, "Funnel started but this machine's *.ts.net name is not ready yet. Try again in a few seconds.");
        }

        return new TailscaleFunnelResult(true, $"https://{dnsName}", "Funnel is on. Friends outside your home can use this HTTPS address — no port forwarding.");
    }

    private static async Task<(bool Ok, string Output, string Error)> RunAsync(
        string binary,
        string arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(binary, arguments)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                }
            };
            process.Start();
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return (false, string.Empty, "Timed out talking to Tailscale.");
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            return (process.ExitCode == 0, stdout, string.IsNullOrWhiteSpace(stderr) ? stdout : stderr);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return (false, string.Empty, ex.Message);
        }
    }
}

public sealed record TailscaleStatus(bool Installed, bool SignedIn, string? InstallCommand, string? DnsName = null);

public sealed record TailscaleFunnelResult(bool Success, string? FunnelUrl, string Message);
