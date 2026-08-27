using System;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Thin seam over actually spawning an external process - exists purely so
    /// <see cref="TailscaleService"/> can be unit tested against scripted output
    /// instead of a real <c>tailscale</c> binary, which cannot exist in a CI/test
    /// environment. See <see cref="ProcessRunner"/> for the real implementation.
    /// </summary>
    public interface IProcessRunner
    {
        /// <summary>
        /// Runs a command to completion and captures its output. Never throws for
        /// "command not found" or a non-zero exit code - both are reported through
        /// the result so callers can distinguish "tailscale isn't installed" from
        /// "tailscale ran and refused."
        /// </summary>
        Task<ProcessRunResult> RunAsync(string fileName, string arguments, TimeSpan timeout, CancellationToken cancellationToken);

        /// <summary>
        /// Starts a long-running command and invokes <paramref name="onStdOutLine"/>
        /// as each line of output arrives, without waiting for the process to exit.
        /// Built specifically for <c>tailscale up</c>, which blocks until the admin
        /// finishes logging in but prints the login URL to stdout well before that -
        /// callers need that line the moment it appears, not after the whole command
        /// finally returns. The process is left running in the background; it is not
        /// killed when this method returns.
        /// </summary>
        Task StartStreamingAsync(string fileName, string arguments, Action<string> onStdOutLine, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Result of a completed <see cref="IProcessRunner.RunAsync"/> call.
    /// </summary>
    /// <param name="Started">
    /// False when the executable itself could not be found/launched (e.g. the
    /// <c>tailscale</c> CLI isn't installed) - distinct from <see cref="ExitCode"/>
    /// being non-zero, which means it launched and then failed.
    /// </param>
    public sealed record ProcessRunResult(bool Started, int ExitCode, string StdOut, string StdErr, bool TimedOut);
}
