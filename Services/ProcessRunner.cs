using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <inheritdoc cref="IProcessRunner"/>
    public class ProcessRunner : IProcessRunner
    {
        private readonly ILogger<ProcessRunner> _logger;

        public ProcessRunner(ILogger<ProcessRunner> logger)
        {
            _logger = logger;
        }

        /// <inheritdoc />
        public async Task<ProcessRunResult> RunAsync(string fileName, string arguments, TimeSpan timeout, CancellationToken cancellationToken)
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            var stdOut = new StringBuilder();
            var stdErr = new StringBuilder();
            process.OutputDataReceived += (_, e) => { if (e.Data != null) { stdOut.AppendLine(e.Data); } };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) { stdErr.AppendLine(e.Data); } };

            try
            {
                process.Start();
            }
            catch (Win32Exception ex)
            {
                // The overwhelmingly common cause is "no such file or directory" -
                // the binary (tailscale, curl, ...) simply isn't on PATH. Reported
                // as Started=false rather than thrown, so callers like
                // TailscaleService.GetStatusAsync can treat "not installed" as an
                // ordinary result instead of an exceptional one.
                _logger.LogDebug(ex, "[Federation] Could not launch {FileName}", fileName);
                return new ProcessRunResult(false, -1, string.Empty, ex.Message, false);
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Either our own timeout fired, or the caller's token did (e.g. an
                // aborted HTTP request) - either way, the point of waiting was to
                // report on this process, so there is no correct outcome that
                // leaves it running unsupervised in the background. Killed
                // unconditionally before deciding how to report the cancellation,
                // otherwise only our own timeout branch used to kill it and a
                // caller-cancelled install script or tailscale command was
                // silently orphaned to run to completion on its own.
                TryKill(process);

                if (!cancellationToken.IsCancellationRequested)
                {
                    return new ProcessRunResult(true, -1, stdOut.ToString(), stdErr.ToString(), true);
                }

                throw;
            }

            return new ProcessRunResult(true, process.ExitCode, stdOut.ToString(), stdErr.ToString(), false);
        }

        /// <inheritdoc />
        public async Task StartStreamingAsync(string fileName, string arguments, Action<string> onStdOutLine, CancellationToken cancellationToken)
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                },
                EnableRaisingEvents = true
            };

            // tailscale up prints its login URL to stderr, not stdout, on every
            // version this was checked against - both streams feed the same
            // callback since callers only care about spotting the URL line, not
            // which stream it arrived on.
            process.OutputDataReceived += (_, e) => { if (e.Data != null) { onStdOutLine(e.Data); } };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) { onStdOutLine(e.Data); } };

            // This Process object is deliberately not wrapped in `using` (it has
            // to outlive this method - see the comment below), so nothing would
            // otherwise ever call Dispose() on it. Freed as soon as the real OS
            // process exits, whether that's from the admin finishing login or the
            // admin never doing so and the command eventually giving up - without
            // this, every "Log in" click permanently leaked one Process handle.
            process.Exited += (_, _) => process.Dispose();

            try
            {
                process.Start();
            }
            catch (Win32Exception ex)
            {
                _logger.LogWarning(ex, "[Federation] Could not launch {FileName}", fileName);
                process.Dispose();
                return;
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // Deliberately not awaiting process exit: tailscale up blocks until the
            // admin finishes logging in in their browser, which this call must not
            // block on - the login URL callers actually want arrives on stdout/
            // stderr long before that. The process is intentionally left running;
            // Dispose() alone (no Kill()) doesn't stop it.
            await Task.CompletedTask.ConfigureAwait(false);
        }

        private void TryKill(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[Federation] Failed to kill timed-out process");
            }
        }
    }
}
