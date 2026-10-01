using System.Runtime.InteropServices;
using System.Text.Json;

namespace FederationCompanion;

/// <summary>Discovers the installed network helper and offers explicit private or public relay setup.</summary>
public static class TailscaleHelper
{
    internal static readonly TailscaleNetworkService Network = new();

    public static Task<TailscaleShareResult> SetUpPrivateAsync(int localPort, int? previousPort, int? previousLocalPort, CancellationToken cancellationToken)
        => Network.SetUpPrivateAsync(localPort, previousPort, previousLocalPort, cancellationToken);

    public static Task<TailscaleStatus> CheckAsync(CancellationToken cancellationToken)
        => Network.CheckAsync(cancellationToken);

    private static string? FindBinary() => TailscaleCommandRunner.FindBinary();

    internal static string InstallCommand()
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
            return !string.IsNullOrWhiteSpace(name) && name.EndsWith(".ts.net", StringComparison.OrdinalIgnoreCase)
                && Uri.CheckHostName(name) == UriHostNameType.Dns && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-')
                ? name : null;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
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
        string binary, string arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var result = await new TailscaleCommandRunner().RunAsync(arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries), timeout, cancellationToken).ConfigureAwait(false);
        return (result.Success, result.Output, result.Error);
    }

}

public sealed record TailscaleStatus(bool Installed, bool SignedIn, string? InstallCommand, string? DnsName = null);

public sealed record TailscaleFunnelResult(bool Success, string? FunnelUrl, string Message);
