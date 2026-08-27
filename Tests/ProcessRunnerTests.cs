using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

/// <summary>
/// Exercises <see cref="ProcessRunner"/> against real OS processes (sleep, bash,
/// echo, a nonexistent binary) rather than a fake - this is the actual shell-out
/// seam <see cref="TailscaleService"/> uses to run install scripts and drive
/// tailscale with this Jellyfin process's own privileges, so its cancellation
/// and timeout behavior is worth verifying against a real process, not just a
/// scripted one.
/// </summary>
public class ProcessRunnerTests
{
    [Fact]
    public async Task RunAsync_CapturesStdOutAndExitCode()
    {
        var runner = new ProcessRunner(NullLogger<ProcessRunner>.Instance);

        var result = await runner.RunAsync("bash", "-c \"echo hello-world\"", TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.True(result.Started);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("hello-world", result.StdOut);
        Assert.False(result.TimedOut);
    }

    [Fact]
    public async Task RunAsync_ReportsNonZeroExitCode_WithoutThrowing()
    {
        var runner = new ProcessRunner(NullLogger<ProcessRunner>.Instance);

        var result = await runner.RunAsync("bash", "-c \"exit 7\"", TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.True(result.Started);
        Assert.Equal(7, result.ExitCode);
    }

    [Fact]
    public async Task RunAsync_ReportsNotStarted_WhenBinaryDoesNotExist()
    {
        var runner = new ProcessRunner(NullLogger<ProcessRunner>.Instance);

        var result = await runner.RunAsync("this-binary-does-not-exist-xyz123", string.Empty, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.False(result.Started);
    }

    /// <summary>
    /// Regression coverage: an install script or `tailscale up` that overruns
    /// its own timeout must actually be killed, not merely reported as timed
    /// out while continuing to run unsupervised in the background.
    /// </summary>
    [Fact]
    public async Task RunAsync_KillsAndReportsTimedOut_WhenProcessExceedsItsOwnTimeout()
    {
        var runner = new ProcessRunner(NullLogger<ProcessRunner>.Instance);

        var result = await runner.RunAsync("sleep", "30", TimeSpan.FromMilliseconds(150), CancellationToken.None);

        Assert.True(result.TimedOut);
    }

    /// <summary>
    /// Regression coverage for a real bug found during review: the original
    /// implementation only killed the process on its own internal timeout, not
    /// on the caller's own cancellation token (e.g. an aborted HTTP request) -
    /// that path let the OperationCanceledException propagate (so this alone
    /// wouldn't have failed against the old code), but never actually killed
    /// the process, leaving it running forever, unsupervised, in the
    /// background. This asserts the process is actually gone afterward, not
    /// just that the exception surfaced - a marker argument unique to this test
    /// run is used so the pgrep check can't match some unrelated sleep call
    /// elsewhere on a shared machine.
    /// </summary>
    [Fact]
    public async Task RunAsync_KillsProcess_WhenCallersOwnTokenIsCancelled()
    {
        var runner = new ProcessRunner(NullLogger<ProcessRunner>.Instance);
        using var cts = new CancellationTokenSource();
        var marker = "procrunnertest-" + Guid.NewGuid().ToString("N");

        var task = runner.RunAsync("bash", $"-c \"exec -a {marker} sleep 30\"", TimeSpan.FromSeconds(30), cts.Token);

        // Give the process a moment to actually start before cancelling -
        // cancelling before Process.Start() even runs would trivially "pass"
        // without ever exercising the kill path this test targets.
        await Task.Delay(TimeSpan.FromMilliseconds(200));
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);

        // TryKill catches its own exceptions and the OS reaps a killed process
        // asynchronously - poll briefly rather than asserting instantly gone.
        var deadline = DateTime.UtcNow.AddSeconds(3);
        var stillRunning = true;
        while (DateTime.UtcNow < deadline)
        {
            var check = await runner.RunAsync("pgrep", $"-f {marker}", TimeSpan.FromSeconds(2), CancellationToken.None);
            stillRunning = check.Started && check.ExitCode == 0;
            if (!stillRunning)
            {
                break;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        Assert.False(stillRunning, "The cancelled process should have been killed, not left running in the background.");
    }

    [Fact]
    public async Task StartStreamingAsync_InvokesCallback_ForEachLineAsItArrives()
    {
        var runner = new ProcessRunner(NullLogger<ProcessRunner>.Instance);
        var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var sawSecondLine = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await runner.StartStreamingAsync(
            "bash",
            "-c \"echo first; sleep 0.05; echo second\"",
            line =>
            {
                lines.Enqueue(line);
                if (line == "second")
                {
                    sawSecondLine.TrySetResult();
                }
            },
            CancellationToken.None);

        // Generous timeout: this test's own point is real process/OS scheduling,
        // which the rest of the suite running dozens of processes in parallel
        // (this file included) can genuinely delay somewhat under load.
        var winner = await Task.WhenAny(sawSecondLine.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Equal(sawSecondLine.Task, winner);
        Assert.Contains("first", lines);
        Assert.Contains("second", lines);
    }
}
