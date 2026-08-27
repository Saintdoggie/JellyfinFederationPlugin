using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Drives the local <c>tailscale</c> CLI to install Tailscale, log this server
    /// in, and expose it through Funnel - the "non-public-facing" half of the
    /// connectivity setup wizard (see <see cref="Configuration.ServerConnectivityMode"/>).
    /// Every method shells out through <see cref="IProcessRunner"/> rather than
    /// calling a Tailscale API directly: there isn't a local one - the CLI is the
    /// only interface to the <c>tailscaled</c> daemon.
    ///
    /// IMPORTANT: this was written and unit-tested against scripted process output
    /// modeled on Tailscale's documented CLI behavior - there is no real
    /// <c>tailscale</c> binary in this development environment to verify against.
    /// Treat the very first live run (real install, real <c>tailscale up</c>, real
    /// <c>tailscale funnel</c>) as the actual first test of this code, particularly
    /// the login-URL regex in <see cref="StartLoginAsync"/> and the JSON shape
    /// assumed by <see cref="GetStatusAsync"/>.
    /// </summary>
    public class TailscaleService
    {
        private static readonly TimeSpan ShortCommandTimeout = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan FunnelSetupTimeout = TimeSpan.FromSeconds(30);
        private static readonly Regex LoginUrlRegex = new(@"https://login\.tailscale\.com/\S+", RegexOptions.Compiled);

        private readonly IProcessRunner _processRunner;
        private readonly ILogger<TailscaleService> _logger;

        public TailscaleService(IProcessRunner processRunner, ILogger<TailscaleService> logger)
        {
            _processRunner = processRunner;
            _logger = logger;
        }

        /// <summary>
        /// How long <see cref="StartLoginAsync"/> waits for a login URL before
        /// concluding none is coming. Internal and settable purely so tests can
        /// shrink it - the real 15s default would otherwise make the "no URL, but
        /// already logged in" branch take 15 real seconds to exercise.
        /// </summary>
        internal TimeSpan LoginUrlWaitTimeout { get; set; } = TimeSpan.FromSeconds(15);

        /// <summary>
        /// Checks whether this process can plausibly install and drive Tailscale
        /// itself. Deliberately conservative - false with an explanatory reason
        /// covers everything from "this is Windows/macOS" to "this is an
        /// unprivileged Docker container with no /dev/net/tun", so the UI can show
        /// the admin why the button is unavailable instead of letting them hit a
        /// confusing failure after clicking Install.
        /// </summary>
        public async Task<TailscaleEnvironmentCheck> CheckEnvironmentAsync(CancellationToken cancellationToken)
        {
            if (!OperatingSystem.IsLinux())
            {
                return new TailscaleEnvironmentCheck(
                    false,
                    "Auto-install only works on Linux. Install Tailscale yourself from tailscale.com/download, then come back here to set up Funnel.");
            }

            var idResult = await _processRunner.RunAsync("id", "-u", ShortCommandTimeout, cancellationToken).ConfigureAwait(false);
            if (!idResult.Started || idResult.ExitCode != 0 || idResult.StdOut.Trim() != "0")
            {
                return new TailscaleEnvironmentCheck(
                    false,
                    "This Jellyfin process isn't running as root, so it can't install system packages or configure networking. Install Tailscale yourself, then come back here to set up Funnel.");
            }

            if (!File.Exists("/dev/net/tun"))
            {
                return new TailscaleEnvironmentCheck(
                    false,
                    "No /dev/net/tun device found. If this is a container, it needs --device=/dev/net/tun and --cap-add=NET_ADMIN before Tailscale can run inside it at all.");
            }

            return new TailscaleEnvironmentCheck(true, null);
        }

        /// <summary>
        /// Reads the current state straight from <c>tailscale status --json</c>.
        /// </summary>
        public async Task<TailscaleStatus> GetStatusAsync(CancellationToken cancellationToken)
        {
            var result = await _processRunner.RunAsync("tailscale", "status --json", ShortCommandTimeout, cancellationToken).ConfigureAwait(false);
            if (!result.Started)
            {
                return new TailscaleStatus(TailscaleBackendState.NotInstalled, null, "Tailscale is not installed.");
            }

            if (result.ExitCode != 0)
            {
                // Most commonly "tailscaled is not running" right after a fresh
                // install, before the daemon has been started for the first time.
                return new TailscaleStatus(TailscaleBackendState.Unknown, null, string.IsNullOrWhiteSpace(result.StdErr) ? "tailscale status failed." : result.StdErr.Trim());
            }

            try
            {
                var parsed = JsonSerializer.Deserialize<TailscaleStatusJson>(result.StdOut, JsonOptions);
                var state = ParseBackendState(parsed?.BackendState);
                return new TailscaleStatus(state, parsed?.Self?.DnsName, null);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "[Federation] Could not parse tailscale status --json output");
                return new TailscaleStatus(TailscaleBackendState.Unknown, null, "Could not understand tailscale's status output.");
            }
        }

        /// <summary>
        /// Runs Tailscale's official install script. Only ever call after
        /// <see cref="CheckEnvironmentAsync"/> reports true - this makes no
        /// environment checks of its own.
        /// </summary>
        public async Task<(bool Success, string Message)> InstallAsync(CancellationToken cancellationToken)
        {
            var result = await _processRunner.RunAsync(
                "bash",
                "-c \"curl -fsSL https://tailscale.com/install.sh | sh\"",
                InstallTimeout,
                cancellationToken).ConfigureAwait(false);

            if (result.TimedOut)
            {
                return (false, "Timed out waiting for the Tailscale install script to finish.");
            }

            if (!result.Started || result.ExitCode != 0)
            {
                var detail = string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr;
                return (false, string.IsNullOrWhiteSpace(detail) ? "Install script failed." : detail.Trim());
            }

            return (true, "Tailscale installed.");
        }

        /// <summary>
        /// Starts <c>tailscale up</c> and waits briefly for the login URL it
        /// prints. <c>tailscale up</c> itself blocks until the admin finishes
        /// logging in through that link, so it is left running in the background
        /// rather than awaited - the caller polls <see cref="GetStatusAsync"/>
        /// afterward to notice when login actually completes.
        /// </summary>
        public async Task<TailscaleLoginResult> StartLoginAsync(CancellationToken cancellationToken)
        {
            var urlSource = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

            await _processRunner.StartStreamingAsync(
                "tailscale",
                "up",
                line =>
                {
                    var match = LoginUrlRegex.Match(line);
                    if (match.Success)
                    {
                        urlSource.TrySetResult(match.Value);
                    }
                },
                cancellationToken).ConfigureAwait(false);

            var urlTask = urlSource.Task;
            var winner = await Task.WhenAny(urlTask, Task.Delay(LoginUrlWaitTimeout, cancellationToken)).ConfigureAwait(false);
            if (winner == urlTask)
            {
                return new TailscaleLoginResult(true, urlTask.Result, "Open the link to finish logging in, then check status.");
            }

            // No URL appeared - most likely this server was already logged in
            // from a previous run (tailscale up is then a same-config no-op that
            // prints nothing), not a failure. Check the actual state instead of
            // guessing which one happened.
            var status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
            if (status.State == TailscaleBackendState.Running)
            {
                return new TailscaleLoginResult(true, null, "Already logged in.");
            }

            return new TailscaleLoginResult(false, null, "Timed out waiting for a login link from tailscale up.");
        }

        /// <summary>
        /// Turns on Funnel for this server's own local Jellyfin port, so it is
        /// reachable at an https://*.ts.net address without any port-forwarding.
        /// Only meaningful once <see cref="GetStatusAsync"/> reports
        /// <see cref="TailscaleBackendState.Running"/>.
        /// </summary>
        public async Task<TailscaleFunnelResult> SetUpFunnelAsync(int localPort, CancellationToken cancellationToken)
        {
            var result = await _processRunner.RunAsync("tailscale", $"funnel --bg {localPort}", FunnelSetupTimeout, cancellationToken).ConfigureAwait(false);
            if (!result.Started || result.ExitCode != 0)
            {
                var detail = string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr;
                return new TailscaleFunnelResult(false, null, string.IsNullOrWhiteSpace(detail) ? "tailscale funnel failed." : detail.Trim());
            }

            var status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(status.DnsName))
            {
                return new TailscaleFunnelResult(false, null, "Funnel command succeeded but this server's Tailscale DNS name is not available yet - try checking status again in a moment.");
            }

            var funnelUrl = $"https://{status.DnsName.TrimEnd('.')}/";
            return new TailscaleFunnelResult(true, funnelUrl, "Funnel is live.");
        }

        private static TailscaleBackendState ParseBackendState(string? raw)
        {
            return raw switch
            {
                "Running" => TailscaleBackendState.Running,
                "NeedsLogin" => TailscaleBackendState.NeedsLogin,
                "NeedsMachineAuth" => TailscaleBackendState.NeedsLogin,
                "Stopped" => TailscaleBackendState.Stopped,
                _ => TailscaleBackendState.Unknown
            };
        }

        private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

        private sealed class TailscaleStatusJson
        {
            [JsonPropertyName("BackendState")]
            public string? BackendState { get; set; }

            [JsonPropertyName("Self")]
            public TailscaleSelfJson? Self { get; set; }
        }

        private sealed class TailscaleSelfJson
        {
            [JsonPropertyName("DNSName")]
            public string? DnsName { get; set; }
        }
    }

    /// <summary>
    /// Whether this process can plausibly install/drive Tailscale itself - see
    /// <see cref="TailscaleService.CheckEnvironmentAsync"/>.
    /// </summary>
    public sealed record TailscaleEnvironmentCheck(bool CanAutoInstall, string? Reason);

    /// <summary>
    /// Coarse view of <c>tailscale status --json</c>'s BackendState.
    /// </summary>
    public enum TailscaleBackendState
    {
        /// <summary>The tailscale CLI could not be launched at all.</summary>
        NotInstalled,

        /// <summary>Installed, but status could not be read or understood.</summary>
        Unknown,

        /// <summary>Installed but never logged in, or logged out.</summary>
        NeedsLogin,

        /// <summary>Installed and logged in, but not currently connected.</summary>
        Stopped,

        /// <summary>Installed, logged in, and connected.</summary>
        Running
    }

    /// <summary>Result of <see cref="TailscaleService.GetStatusAsync"/>.</summary>
    public sealed record TailscaleStatus(TailscaleBackendState State, string? DnsName, string? Message);

    /// <summary>Result of <see cref="TailscaleService.StartLoginAsync"/>.</summary>
    public sealed record TailscaleLoginResult(bool Success, string? LoginUrl, string Message);

    /// <summary>Result of <see cref="TailscaleService.SetUpFunnelAsync"/>.</summary>
    public sealed record TailscaleFunnelResult(bool Success, string? FunnelUrl, string Message);
}
