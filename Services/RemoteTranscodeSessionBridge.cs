using System;
using System.Collections.Concurrent;
using System.IO;
using MediaBrowser.Common.Configuration;
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
        private readonly IApplicationPaths? _paths;
        private readonly FederationBandwidthAllocator? _bandwidth;
        private readonly ConcurrentDictionary<string, string> _logFiles = new(StringComparer.OrdinalIgnoreCase);
        private CancellationTokenSource? _stopping;

        /// <summary>
        /// Initializes a new instance of the <see cref="RemoteTranscodeSessionBridge"/> class.
        /// </summary>
        public RemoteTranscodeSessionBridge(
            ISessionManager sessions,
            RemoteTranscodeHandoffs handoffs,
            FederationLibraryManager federationManager,
            ILogger<RemoteTranscodeSessionBridge> logger,
            IApplicationPaths? paths = null,
            FederationBandwidthAllocator? bandwidth = null)
        {
            _bandwidth = bandwidth;
            _paths = paths;
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
                SaveLog(serverId, e.PlaySessionId, e.Item?.Id ?? Guid.Empty, final: false);
            }
        }

        private void OnStopped(object? sender, PlaybackStopEventArgs e)
        {
            _bandwidth?.ReleaseEverywhere(e.PlaySessionId);
            var serverId = _handoffs.TakeStop(e.PlaySessionId);
            if (serverId != null)
            {
                Send(serverId, e.PlaySessionId, "stop", null);
                SaveLog(serverId, e.PlaySessionId, e.Item?.Id ?? Guid.Empty, final: true);
            }
        }

        /// <summary>
        /// Copies the friend's conversion log of this play into this server's log
        /// folder (Dashboard → Logs), as if the conversion had run here. Updated
        /// with every relayed ping while playing, and once more after the stop.
        /// </summary>
        private void SaveLog(string serverId, string playSessionId, Guid localItemId, bool final)
        {
            var logDirectory = _paths?.LogDirectoryPath;
            var server = _federationManager.GetServer(serverId);
            var client = _federationManager.GetClient(serverId);
            if (string.IsNullOrEmpty(logDirectory) || server == null || client == null)
            {
                return;
            }

            var token = _stopping?.Token ?? CancellationToken.None;
            var fileName = _logFiles.GetOrAdd(playSessionId, id => FriendConversionLogs.LocalFileName(DateTime.Now, localItemId, server.Name, id));
            _ = Task.Run(
                async () =>
                {
                    try
                    {
                        if (final)
                        {
                            // Let the friend's ffmpeg finish writing after the stop.
                            await Task.Delay(TimeSpan.FromSeconds(3), token).ConfigureAwait(false);
                        }

                        var text = await client.GetPlayLogAsync(playSessionId, token).ConfigureAwait(false);
                        if (!string.IsNullOrEmpty(text))
                        {
                            var header = FriendConversionLogs.DescribeSource(server.Name, playSessionId)
                                + $" (it converted this play; fetched {DateTime.Now:yyyy-MM-dd HH:mm:ss})\n\n";
                            await File.WriteAllTextAsync(Path.Combine(logDirectory, fileName), header + text, token).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // Shutting down.
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "[Federation] Could not save a friend's conversion log");
                    }
                    finally
                    {
                        if (final)
                        {
                            _logFiles.TryRemove(playSessionId, out _);
                        }
                    }
                },
                token);
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
