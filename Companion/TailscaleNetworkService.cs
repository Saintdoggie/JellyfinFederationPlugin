using System.Diagnostics;
using System.Text.Json;

namespace FederationCompanion;

public sealed record TailscaleCommandResult(bool Success, string Output, string Error, bool Installed = true);

public interface ITailscaleCommandRunner
{
    Task<TailscaleCommandResult> RunAsync(IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>Owner-controlled private HTTPS sharing of Companion's existing scoped relay.</summary>
public sealed class TailscaleNetworkService
{
    private readonly ITailscaleCommandRunner _runner;
    private readonly SemaphoreSlim _setupGate = new(1, 1);
    internal SemaphoreSlim StateGate { get; } = new(1, 1);

    public TailscaleNetworkService(ITailscaleCommandRunner? runner = null)
        => _runner = runner ?? new TailscaleCommandRunner();

    public async Task<TailscaleStatus> CheckAsync(CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(new[] { "status", "--json" }, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        if (!result.Installed) return new(false, false, TailscaleHelper.InstallCommand(), null);
        try
        {
            using var doc = JsonDocument.Parse(result.Output);
            var running = result.Success && doc.RootElement.TryGetProperty("BackendState", out var state)
                && state.GetString() == "Running";
            return new(true, running, null, running ? TailscaleHelper.ReadDnsName(result.Output) : null);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { return new(true, false, null, null); }
    }

    public async Task<TailscaleShareResult> SetUpPrivateAsync(int localPort, int? previousPort, int? previousLocalPort, CancellationToken cancellationToken)
    {
        if (localPort is < 1 or > 65535) return new(false, null, null, "Companion has not opened a local port yet.");
        if (!await _setupGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return new(false, null, null, "Another network setup is running. Wait for it to finish.");
        try
        {
            var status = await CheckAsync(cancellationToken).ConfigureAwait(false);
            if (!status.Installed) return new(false, null, null, "Install Tailscale on the server computer, then retry private sharing.");
            if (!status.SignedIn || string.IsNullOrWhiteSpace(status.DnsName))
                return new(false, null, null, "Sign in to Tailscale and enable HTTPS certificates, then retry private sharing.");
            var configuration = await _runner.RunAsync(new[] { "serve", "status", "--json" }, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            if (!configuration.Success) return new(false, null, null, "Could not inspect existing Tailscale services. No service was changed.");
            var port = ChoosePrivatePort(configuration.Output, localPort, previousPort, previousLocalPort);
            if (!port.HasValue) return new(false, null, null, "No unused sharing port is available. Existing Tailscale services were preserved.");
            var target = $"http://127.0.0.1:{localPort}";
            var enabled = await _runner.RunAsync(new[] { "serve", "--bg", "--yes", $"--https={port.Value}", target },
                TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            if (!enabled.Success) return new(false, null, null, "Private sharing could not start. Check Tailscale permissions and HTTPS settings, then retry.");
            var configured = await _runner.RunAsync(new[] { "serve", "status", "--json" }, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            if (!configured.Success || !IsPrivateProxy(configured.Output, status.DnsName, port.Value, target))
                return new(false, null, null, "Tailscale did not confirm a private relay for this Companion. A share address was not saved.");
            var address = $"https://{status.DnsName}" + (port.Value == 443 ? string.Empty : $":{port.Value}");
            return new(true, address, port, "Private sharing is ready. Both server computers need Tailscale access to this machine; approve your friend in the Tailscale device-sharing page.");
        }
        finally { _setupGate.Release(); }
    }

    public async Task<TailscaleShareResult> StopPrivateAsync(int port, int localPort, CancellationToken cancellationToken)
    {
        if (port is < 1 or > 65535 || localPort is < 1 or > 65535)
            return new(false, null, null, "No managed private listener is configured.");
        if (!await _setupGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return new(false, null, null, "Another network setup is running. Wait for it to finish.");
        try
        {
            var status = await CheckAsync(cancellationToken).ConfigureAwait(false);
            var configuration = await _runner.RunAsync(new[] { "serve", "status", "--json" }, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            if (!configuration.Success || status.DnsName == null
                || !IsPrivateProxy(configuration.Output, status.DnsName, port, $"http://127.0.0.1:{localPort}"))
                return new(false, null, null, "The managed listener is no longer this Companion. No network service was changed.");
            var stopped = await _runner.RunAsync(new[] { "serve", $"--https={port}", "off" }, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            if (!stopped.Success) return new(false, null, null, "Could not stop private sharing. Check Tailscale permissions.");
            var remaining = await _runner.RunAsync(new[] { "serve", "status", "--json" }, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            return remaining.Success && IsPortAbsent(remaining.Output, port)
                ? new(true, null, null, "Private sharing stopped.")
                : new(false, null, null, "Tailscale did not confirm the listener stopped. Retry from the dashboard.");
        }
        finally { _setupGate.Release(); }
    }

    internal static int? ChoosePrivatePort(string json, int localPort, int? previousPort, int? previousLocalPort)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            var root = doc.RootElement;
            if (root.TryGetProperty("Foreground", out var foreground)
                && (foreground.ValueKind != JsonValueKind.Object || foreground.EnumerateObject().Any())) return null;
            var occupied = new HashSet<int>();
            int? owned = null;
            if (root.TryGetProperty("TCP", out var tcp))
            {
                if (tcp.ValueKind != JsonValueKind.Object) return null;
                foreach (var field in tcp.EnumerateObject())
                {
                    if (!int.TryParse(field.Name, out var p) || p is < 1 or > 65535 || field.Value.ValueKind != JsonValueKind.Object) return null;
                    occupied.Add(p);
                }
            }
            if (root.TryGetProperty("Web", out var web))
            {
                if (web.ValueKind != JsonValueKind.Object) return null;
                foreach (var field in web.EnumerateObject())
                {
                    if (!int.TryParse(field.Name[(field.Name.LastIndexOf(':') + 1)..], out var port) || port is < 1 or > 65535) return null;
                    occupied.Add(port);
                    if (SingleProxy(field.Value) is string proxy
                        && (proxy == $"http://127.0.0.1:{localPort}"
                            || previousPort == port && previousLocalPort.HasValue && proxy == $"http://127.0.0.1:{previousLocalPort}")
                        && root.TryGetProperty("TCP", out var listeners) && listeners.TryGetProperty(port.ToString(), out var listener)
                        && listener.TryGetProperty("HTTPS", out var https) && https.ValueKind == JsonValueKind.True
                        && web.EnumerateObject().Count(w => w.Name.EndsWith($":{port}", StringComparison.Ordinal)) == 1)
                        owned ??= port; // Only replace this Companion's sole HTTPS root handler.
                }
            }
            if (owned.HasValue) return owned;
            return new[] { 8443, 10000, 443 }.Cast<int?>().FirstOrDefault(p => !occupied.Contains(p!.Value));
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { return null; }
    }

    private static bool IsPortAbsent(string json, int port)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (root.TryGetProperty("TCP", out var tcp)
                && (tcp.ValueKind != JsonValueKind.Object || tcp.TryGetProperty(port.ToString(), out _))) return false;
            return !root.TryGetProperty("Web", out var web)
                || web.ValueKind == JsonValueKind.Object && !web.EnumerateObject().Any(w => w.Name.EndsWith($":{port}", StringComparison.Ordinal));
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { return false; }
    }

    private static string? SingleProxy(JsonElement web)
    {
        if (web.ValueKind != JsonValueKind.Object || !web.TryGetProperty("Handlers", out var handlers)
            || handlers.ValueKind != JsonValueKind.Object || handlers.EnumerateObject().Count() != 1
            || !handlers.TryGetProperty("/", out var handler) || handler.ValueKind != JsonValueKind.Object
            || !handler.TryGetProperty("Proxy", out var proxy) || proxy.ValueKind != JsonValueKind.String) return null;
        return proxy.GetString();
    }

    internal static bool IsPrivateProxy(string json, string dns, int port, string target)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var endpoint = $"{dns}:{port}";
            return root.TryGetProperty("Web", out var web) && web.TryGetProperty(endpoint, out var handler)
                && SingleProxy(handler) == target
                && root.TryGetProperty("TCP", out var tcp) && tcp.TryGetProperty(port.ToString(), out var listener)
                && listener.TryGetProperty("HTTPS", out var https) && https.ValueKind == JsonValueKind.True
                && (!root.TryGetProperty("AllowFunnel", out var funnel) || !funnel.TryGetProperty(endpoint, out var allowed)
                    || allowed.ValueKind == JsonValueKind.False);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { return false; }
    }
}

public sealed record TailscaleShareResult(bool Success, string? Url, int? Port, string Message);

internal sealed class TailscaleCommandRunner : ITailscaleCommandRunner
{
    internal static string? FindBinary()
    {
        var executable = OperatingSystem.IsWindows() ? "tailscale.exe" : "tailscale";
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            var fullDirectory = directory.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(fullDirectory) || !Path.IsPathFullyQualified(fullDirectory)) continue;
            var candidate = Path.Combine(fullDirectory, executable);
            if (File.Exists(candidate)) return candidate;
        }
        var candidates = OperatingSystem.IsWindows()
            ? new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) }
                .Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => Path.Combine(p, "Tailscale", executable))
            : OperatingSystem.IsMacOS()
                ? new[] { "/Applications/Tailscale.app/Contents/MacOS/Tailscale", "/opt/homebrew/bin/tailscale", "/usr/local/bin/tailscale" }
                : new[] { "/usr/bin/tailscale", "/usr/sbin/tailscale", "/usr/local/bin/tailscale" };
        return candidates.FirstOrDefault(File.Exists);
    }

    public async Task<TailscaleCommandResult> RunAsync(IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var binary = FindBinary();
        if (binary == null) return new(false, string.Empty, string.Empty, Installed: false);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);
        using var process = new Process { StartInfo = new ProcessStartInfo(binary) {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true } };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        try
        {
            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync(linked.Token);
            var stderr = process.StandardError.ReadToEndAsync(linked.Token);
            try
            {
                await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
                return new(process.ExitCode == 0, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                try { await Task.WhenAll(stdout, stderr).ConfigureAwait(false); } catch (OperationCanceledException) { }
                cancellationToken.ThrowIfCancellationRequested();
                return new(false, string.Empty, "Timed out talking to Tailscale.");
            }
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        { return new(false, string.Empty, "Could not run the installed Tailscale client."); }
    }
}
