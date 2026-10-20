using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Receiver side of the remote-transcode handoff: the viewer stays signed in
    /// here, so their progress, pause and stop reach this server. Progress is
    /// recorded here as usual (resume points, history); this only relays the
    /// pause ping and the stop to the friend converting the session, so a paused
    /// job isn't killed by the friend's 60 s idle timer and a stopped one frees
    /// the friend's GPU at once.
    /// </summary>
    public sealed class RemoteTranscodeSessionBridge : IHostedService
    {
        private readonly ISessionManager _sessions;
        private readonly RemoteTranscodeHandoffs _handoffs;
        private readonly FederationLibraryManager _federationManager;
        private readonly ILogger<RemoteTranscodeSessionBridge> _logger;
        private CancellationTokenSource? _stopping;

        /// <summary>
        /// Initializes a new instance of the <see cref="RemoteTranscodeSessionBridge"/> class.
        /// </summary>
        public RemoteTranscodeSessionBridge(
            ISessionManager sessions,
            RemoteTranscodeHandoffs handoffs,
            FederationLibraryManager federationManager,
            ILogger<RemoteTranscodeSessionBridge> logger)
        {
            _sessions = sessions;
            _handoffs = handoffs;
            _federationManager = federationManager;
            _logger = logger;
        }

        /// <inheritdoc />
        public Task StartAsync(CancellationToken cancellationToken)
        {
            _stopping = new CancellationTokenSource();
            _sessions.PlaybackProgress += OnProgress;
            _sessions.PlaybackStopped += OnStopped;
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task StopAsync(CancellationToken cancellationToken)
        {
            _sessions.PlaybackProgress -= OnProgress;
            _sessions.PlaybackStopped -= OnStopped;
            _stopping?.Cancel();
            _stopping?.Dispose();
            _stopping = null;
            return Task.CompletedTask;
        }

        private void OnProgress(object? sender, PlaybackProgressEventArgs e)
        {
            var serverId = _handoffs.TakePing(e.PlaySessionId, e.IsPaused);
            if (serverId != null)
            {
                Send(serverId, e.PlaySessionId, "ping", e.IsPaused);
            }
        }

        private void OnStopped(object? sender, PlaybackStopEventArgs e)
        {
            var serverId = _handoffs.TakeStop(e.PlaySessionId);
            if (serverId != null)
            {
                Send(serverId, e.PlaySessionId, "stop", null);
            }
        }

        private void Send(string serverId, string playSessionId, string action, bool? isPaused)
        {
            var client = _federationManager.GetClient(serverId);
            var token = _stopping?.Token ?? CancellationToken.None;
            if (client == null)
            {
                return;
            }

            _ = Task.Run(
                async () =>
                {
                    try
                    {
                        if (!await client.SendPlayControlAsync(playSessionId, action, isPaused, token).ConfigureAwait(false))
                        {
                            _logger.LogDebug("[Federation] Friend did not accept the {Action} for a handed-off playback", action);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // Shutting down.
                    }
                },
                token);
        }
    }
}
