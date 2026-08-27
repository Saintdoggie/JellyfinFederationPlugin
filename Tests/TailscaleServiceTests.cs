using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

/// <summary>
/// Covers <see cref="TailscaleService"/> against scripted <see cref="IProcessRunner"/>
/// output modeled on Tailscale's documented CLI behavior - there is no real
/// <c>tailscale</c> binary available to test against here (see the class-level
/// remark on <see cref="TailscaleService"/> itself). These tests verify this
/// service's own parsing/control-flow logic, not that a real tailscaled agrees
/// with the shapes scripted below.
/// </summary>
public class TailscaleServiceTests
{
    private sealed class FakeProcessRunner : IProcessRunner
    {
        public Func<string, string, ProcessRunResult>? OnRun { get; set; }

        public Func<string, string, Task<ProcessRunResult>>? OnRunAsync { get; set; }

        public Action<string, string, Action<string>>? OnStream { get; set; }

        public Task<ProcessRunResult> RunAsync(string fileName, string arguments, TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (OnRunAsync != null)
            {
                return OnRunAsync(fileName, arguments);
            }

            var result = OnRun?.Invoke(fileName, arguments) ?? new ProcessRunResult(false, -1, string.Empty, string.Empty, false);
            return Task.FromResult(result);
        }

        public Task StartStreamingAsync(string fileName, string arguments, Action<string> onStdOutLine, CancellationToken cancellationToken)
        {
            OnStream?.Invoke(fileName, arguments, onStdOutLine);
            return Task.CompletedTask;
        }
    }

    private const string RunningStatusJson = "{\"BackendState\":\"Running\",\"Self\":{\"DNSName\":\"my-server.tailnet-name.ts.net.\"}}";

    [Fact]
    public async Task CheckEnvironmentAsync_RefusesWithReason_WhenNotRoot()
    {
        var runner = new FakeProcessRunner
        {
            OnRun = (file, args) => file == "id"
                ? new ProcessRunResult(true, 0, "1000\n", string.Empty, false)
                : new ProcessRunResult(false, -1, string.Empty, string.Empty, false)
        };
        var service = new TailscaleService(runner, NullLogger<TailscaleService>.Instance);

        var check = await service.CheckEnvironmentAsync(CancellationToken.None);

        Assert.False(check.CanAutoInstall);
        Assert.Contains("root", check.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(check.Command);
        Assert.Contains("tailscale.com/install.sh", check.Command, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetStatusAsync_ReturnsNotInstalled_WhenTailscaleBinaryIsMissing()
    {
        var runner = new FakeProcessRunner { OnRun = (_, _) => new ProcessRunResult(false, -1, string.Empty, string.Empty, false) };
        var service = new TailscaleService(runner, NullLogger<TailscaleService>.Instance);

        var status = await service.GetStatusAsync(CancellationToken.None);

        Assert.Equal(TailscaleBackendState.NotInstalled, status.State);
    }

    [Fact]
    public async Task GetStatusAsync_ParsesRunningStateAndDnsName()
    {
        var runner = new FakeProcessRunner { OnRun = (_, _) => new ProcessRunResult(true, 0, RunningStatusJson, string.Empty, false) };
        var service = new TailscaleService(runner, NullLogger<TailscaleService>.Instance);

        var status = await service.GetStatusAsync(CancellationToken.None);

        Assert.Equal(TailscaleBackendState.Running, status.State);
        Assert.Equal("my-server.tailnet-name.ts.net.", status.DnsName);
    }

    [Fact]
    public async Task GetStatusAsync_ReturnsNeedsLogin_WhenNeverAuthenticated()
    {
        const string json = "{\"BackendState\":\"NeedsLogin\"}";
        var runner = new FakeProcessRunner { OnRun = (_, _) => new ProcessRunResult(true, 0, json, string.Empty, false) };
        var service = new TailscaleService(runner, NullLogger<TailscaleService>.Instance);

        var status = await service.GetStatusAsync(CancellationToken.None);

        Assert.Equal(TailscaleBackendState.NeedsLogin, status.State);
    }

    [Fact]
    public async Task InstallAsync_ReportsFailure_WhenScriptExitsNonZero()
    {
        var runner = new FakeProcessRunner { OnRun = (_, _) => new ProcessRunResult(true, 1, string.Empty, "permission denied", false) };
        var service = new TailscaleService(runner, NullLogger<TailscaleService>.Instance);

        var (success, message) = await service.InstallAsync(CancellationToken.None);

        Assert.False(success);
        Assert.Contains("permission denied", message);
    }

    [Fact]
    public async Task InstallAsync_ReportsSuccess_WhenScriptExitsZero()
    {
        var runner = new FakeProcessRunner { OnRun = (_, _) => new ProcessRunResult(true, 0, "Installed.", string.Empty, false) };
        var service = new TailscaleService(runner, NullLogger<TailscaleService>.Instance);

        var (success, _) = await service.InstallAsync(CancellationToken.None);

        Assert.True(success);
    }

    [Fact]
    public async Task StartLoginAsync_ReturnsLoginUrl_AsSoonAsTailscaleUpPrintsIt()
    {
        var runner = new FakeProcessRunner
        {
            OnStream = (_, _, onLine) => onLine("To authenticate, visit:\n\n\thttps://login.tailscale.com/a/abc123\n")
        };
        var service = new TailscaleService(runner, NullLogger<TailscaleService>.Instance);

        var result = await service.StartLoginAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("https://login.tailscale.com/a/abc123", result.LoginUrl);
    }

    /// <summary>
    /// Regression coverage for a real gap: tailscale up prints nothing when this
    /// server is already authenticated (it's a same-config no-op), which must not
    /// be reported as a failed login - the caller checks actual status instead of
    /// assuming "no URL" means "something went wrong".
    /// </summary>
    [Fact]
    public async Task StartLoginAsync_ReportsAlreadyLoggedIn_WhenNoUrlAppearsButStatusIsRunning()
    {
        var runner = new FakeProcessRunner
        {
            OnStream = (_, _, _) => { /* tailscale up prints nothing - already logged in */ },
            OnRun = (_, _) => new ProcessRunResult(true, 0, RunningStatusJson, string.Empty, false)
        };
        var service = new TailscaleService(runner, NullLogger<TailscaleService>.Instance) { LoginUrlWaitTimeout = TimeSpan.FromMilliseconds(10) };

        var result = await service.StartLoginAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.Null(result.LoginUrl);
    }

    [Fact]
    public async Task StartLoginAsync_ReportsFailure_WhenNoUrlAndNotRunning()
    {
        const string needsLoginJson = "{\"BackendState\":\"NeedsLogin\"}";
        var runner = new FakeProcessRunner
        {
            OnStream = (_, _, _) => { },
            OnRun = (_, _) => new ProcessRunResult(true, 0, needsLoginJson, string.Empty, false)
        };
        var service = new TailscaleService(runner, NullLogger<TailscaleService>.Instance) { LoginUrlWaitTimeout = TimeSpan.FromMilliseconds(10) };

        var result = await service.StartLoginAsync(CancellationToken.None);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task SetUpFunnelAsync_BuildsHttpsUrl_FromDnsName()
    {
        var runner = new FakeProcessRunner
        {
            OnRun = (file, args) => file == "tailscale" && args.StartsWith("funnel", StringComparison.Ordinal)
                ? new ProcessRunResult(true, 0, string.Empty, string.Empty, false)
                : new ProcessRunResult(true, 0, RunningStatusJson, string.Empty, false)
        };
        var service = new TailscaleService(runner, NullLogger<TailscaleService>.Instance);

        var result = await service.SetUpFunnelAsync(8096, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("https://my-server.tailnet-name.ts.net/", result.FunnelUrl);
    }

    [Fact]
    public async Task SetUpFunnelAsync_ReportsFailure_WhenFunnelCommandFails()
    {
        var runner = new FakeProcessRunner { OnRun = (_, _) => new ProcessRunResult(true, 1, string.Empty, "funnel requires a Running state", false) };
        var service = new TailscaleService(runner, NullLogger<TailscaleService>.Instance);

        var result = await service.SetUpFunnelAsync(8096, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Null(result.FunnelUrl);
    }

    /// <summary>
    /// Regression coverage for a "stress test this" review finding: nothing
    /// stopped a second admin click (or a second browser tab) from starting a
    /// second install while the first was still mid-flight - two install
    /// scripts, or two tailscale up processes, racing each other. Install and
    /// Funnel share one mutual-exclusion gate for exactly this reason; this
    /// exercises Install's side of it since the mechanism is identical for both.
    /// </summary>
    [Fact]
    public async Task InstallAsync_RefusesSecondCall_WhileFirstIsStillRunning()
    {
        var firstCallStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstCall = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callCount = 0;

        var runner = new FakeProcessRunner
        {
            OnRunAsync = async (_, _) =>
            {
                Interlocked.Increment(ref callCount);
                firstCallStarted.TrySetResult();
                await releaseFirstCall.Task;
                return new ProcessRunResult(true, 0, "Installed.", string.Empty, false);
            }
        };
        var service = new TailscaleService(runner, NullLogger<TailscaleService>.Instance);

        var firstInstall = service.InstallAsync(CancellationToken.None);
        await firstCallStarted.Task;

        var (secondSuccess, secondMessage) = await service.InstallAsync(CancellationToken.None);

        Assert.False(secondSuccess);
        Assert.Contains("already running", secondMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, callCount);

        releaseFirstCall.TrySetResult();
        var (firstSuccess, _) = await firstInstall;
        Assert.True(firstSuccess);
    }
}
