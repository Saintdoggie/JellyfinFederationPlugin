using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// The linked server's half of sound help (<see cref="SoundHelpService"/>):
    /// turns a sound track, sent in pieces by the linked server, into AAC stereo
    /// and hands back whatever is converted so far with each reply. Only sound
    /// ever arrives here; the picture never leaves the server that plays it.
    /// One ffmpeg per play, fed through its standard input, so the sound is
    /// converted as one continuous track (no gaps between pieces). In memory only.
    /// </summary>
    public sealed class SoundHelpEncoder : IDisposable
    {
        /// <summary>Largest piece of sound accepted per request.</summary>
        internal const int MaxPieceBytes = 4 * 1024 * 1024;

        /// <summary>Largest converted reply (a reply only holds what is ready).</summary>
        internal const int MaxReplyBytes = 16 * 1024 * 1024;

        /// <summary>Plays converted at once; more are refused (the player is told why).</summary>
        internal const int MaxSessions = 4;

        /// <summary>A play nobody fed for this long is stopped (a long pause ends it).</summary>
        internal static readonly TimeSpan IdleLimit = TimeSpan.FromMinutes(90);

        private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);
        private readonly ILogger<SoundHelpEncoder> _logger;
        private readonly Func<string?> _encoderPath;
        private int _disposed;

        /// <summary>Initializes a new instance of the <see cref="SoundHelpEncoder"/> class.</summary>
        public SoundHelpEncoder(ILogger<SoundHelpEncoder> logger, Func<string?> encoderPath)
        {
            _logger = logger;
            _encoderPath = encoderPath;
        }

        internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

        /// <summary>Plays being converted now.</summary>
        internal int Running => _sessions.Count;

        /// <summary>
        /// Why this server can't convert sound for its linked server right now,
        /// or null when it can.
        /// </summary>
        internal string? Unavailable(Configuration.PluginConfiguration? config)
        {
            if (!TranscodePolicy.MayConvertForOwnViewers(config))
            {
                return "this server is set never to convert";
            }

            var path = _encoderPath();
            if (string.IsNullOrWhiteSpace(path))
            {
                return "this server has no ffmpeg";
            }

            Sweep();
            return _sessions.Count >= MaxSessions ? "this server is already converting sound for " + MaxSessions + " plays" : null;
        }

        /// <summary>
        /// Feeds one piece of a play's sound and returns the converted sound ready
        /// so far. <paramref name="sequence"/> 0 starts the play; pieces must come
        /// in order. With <paramref name="end"/> the play's sound is complete: the
        /// reply holds everything that was left and the play is forgotten.
        /// </summary>
        /// <returns>The status code to answer and the converted bytes.</returns>
        internal async Task<(int Status, byte[] Body)> FeedAsync(string callerId, string playId, int sequence, bool end, Stream piece, Configuration.PluginConfiguration? config, CancellationToken cancellationToken)
        {
            if (!ValidPlayId(playId) || sequence < 0)
            {
                return (400, Array.Empty<byte>());
            }

            Sweep();
            var key = callerId + "|" + playId;
            Session? session;
            if (sequence == 0)
            {
                if (_sessions.TryRemove(key, out var previous))
                {
                    previous.Dispose();
                }

                if (Unavailable(config) != null)
                {
                    return (503, Array.Empty<byte>());
                }

                try
                {
                    session = Session.Start(_encoderPath()!, UtcNow());
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Federation] Could not start ffmpeg to convert sound for a linked server");
                    return (503, Array.Empty<byte>());
                }

                _sessions[key] = session;
                _logger.LogInformation("[Federation] Converting sound only for a play on a linked server ({Running} running)", _sessions.Count);
            }
            else if (!_sessions.TryGetValue(key, out session))
            {
                return (410, Array.Empty<byte>());
            }

            await session.Lock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (sequence != session.NextSequence)
                {
                    return (409, Array.Empty<byte>());
                }

                session.NextSequence++;
                session.LastFedUtc = UtcNow();
                var received = await CopyLimitedAsync(piece, session.Input, MaxPieceBytes, cancellationToken).ConfigureAwait(false);
                if (received < 0)
                {
                    Forget(key, session);
                    return (413, Array.Empty<byte>());
                }

                await session.Input.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (end)
                {
                    session.Input.Close();
                    using var finish = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    finish.CancelAfter(TimeSpan.FromSeconds(30));
                    try
                    {
                        await session.Pump.WaitAsync(finish.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                    }

                    var rest = session.Take(MaxReplyBytes);
                    Forget(key, session);
                    return (200, rest);
                }

                // Give the converter a moment to catch up, so most replies carry sound.
                await session.WaitForOutputAsync(received > 0 ? TimeSpan.FromMilliseconds(400) : TimeSpan.FromMilliseconds(1500), cancellationToken).ConfigureAwait(false);
                if (session.Failed)
                {
                    _logger.LogWarning("[Federation] Sound conversion for a linked server stopped: {Error}", session.Error);
                    Forget(key, session);
                    return (502, Array.Empty<byte>());
                }

                return (200, session.Take(MaxReplyBytes));
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "[Federation] Sound conversion for a linked server stopped: {Error}", session.Error);
                Forget(key, session);
                return (502, Array.Empty<byte>());
            }
            finally
            {
                session.Lock.Release();
            }
        }

        /// <summary>Stops a play's conversion (the viewer stopped).</summary>
        internal void Stop(string callerId, string playId)
        {
            if (_sessions.TryRemove(callerId + "|" + playId, out var session))
            {
                session.Dispose();
            }
        }

        /// <summary>Stops plays nobody fed for <see cref="IdleLimit"/>.</summary>
        internal void Sweep()
        {
            var now = UtcNow();
            foreach (var pair in _sessions.ToList())
            {
                if (now - pair.Value.LastFedUtc > IdleLimit && _sessions.TryRemove(pair.Key, out var stale))
                {
                    stale.Dispose();
                }
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            foreach (var key in _sessions.Keys.ToList())
            {
                if (_sessions.TryRemove(key, out var session))
                {
                    session.Dispose();
                }
            }
        }

        /// <summary>A play id is 32 lowercase hex characters.</summary>
        internal static bool ValidPlayId(string? playId)
            => playId is { Length: 32 } && playId.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

        /// <summary>The ffmpeg arguments: sound in (Matroska), AAC stereo out (Matroska), original timestamps kept.</summary>
        internal static IReadOnlyList<string> Arguments() => new[]
        {
            "-hide_banner", "-loglevel", "error",
            "-probesize", "65536", "-analyzeduration", "1000000",
            "-f", "matroska", "-i", "pipe:0",
            "-map", "0:a:0", "-vn", "-sn", "-dn",
            "-c:a", "aac", "-ac", "2", "-b:a", "192k",
            "-copyts", "-flush_packets", "1",
            "-f", "matroska", "-cluster_time_limit", "500", "-cluster_size_limit", "65536", "pipe:1"
        };

        private void Forget(string key, Session session)
        {
            if (_sessions.TryRemove(new KeyValuePair<string, Session>(key, session)))
            {
                session.Dispose();
            }
        }

        // Copies at most `limit` bytes; -1 when the piece is larger.
        private static async Task<int> CopyLimitedAsync(Stream from, Stream to, int limit, CancellationToken cancellationToken)
        {
            var buffer = new byte[81920];
            var total = 0;
            int read;
            while ((read = await from.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > limit)
                {
                    return -1;
                }

                await to.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            return total;
        }

        private sealed class Session : IDisposable
        {
            private readonly Process _process;
            private readonly MemoryStream _output = new();
            private readonly object _gate = new();
            private readonly StringBuilder _error = new();
            private TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private int _disposed;

            private Session(Process process, DateTime now)
            {
                _process = process;
                LastFedUtc = now;
                Input = process.StandardInput.BaseStream;
                Pump = Task.Run(PumpAsync);
                _ = Task.Run(ReadErrorsAsync);
            }

            public SemaphoreSlim Lock { get; } = new(1, 1);

            public Stream Input { get; }

            public Task Pump { get; }

            public int NextSequence { get; set; }

            public DateTime LastFedUtc { get; set; }

            public bool Failed { get; private set; }

            public string Error
            {
                get
                {
                    lock (_error)
                    {
                        return _error.Length == 0 ? "ffmpeg ended" : _error.ToString().Trim();
                    }
                }
            }

            public static Session Start(string encoderPath, DateTime now)
            {
                var info = new ProcessStartInfo
                {
                    FileName = encoderPath,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                foreach (var argument in Arguments())
                {
                    info.ArgumentList.Add(argument);
                }

                var process = Process.Start(info) ?? throw new InvalidOperationException("ffmpeg did not start");
                return new Session(process, now);
            }

            public byte[] Take(int limit)
            {
                lock (_gate)
                {
                    var length = (int)Math.Min(_output.Length, limit);
                    var buffer = _output.GetBuffer();
                    var taken = buffer.AsSpan(0, length).ToArray();
                    var left = (int)_output.Length - length;
                    if (left > 0)
                    {
                        Buffer.BlockCopy(buffer, length, buffer, 0, left);
                    }

                    _output.SetLength(left);
                    if (left == 0 && _ready.Task.IsCompleted)
                    {
                        _ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    }

                    return taken;
                }
            }

            public async Task WaitForOutputAsync(TimeSpan wait, CancellationToken cancellationToken)
            {
                Task ready;
                lock (_gate)
                {
                    ready = _ready.Task;
                }

                try
                {
                    var first = await Task.WhenAny(ready, Pump, Task.Delay(wait, cancellationToken)).ConfigureAwait(false);
                    if (first == ready)
                    {
                        // Sound is coming out: collect a little more so replies are not tiny.
                        await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                }
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                {
                    return;
                }

                try
                {
                    if (!_process.HasExited)
                    {
                        _process.Kill(true);
                    }
                }
                catch (Exception)
                {
                }

                _process.Dispose();
            }

            private async Task PumpAsync()
            {
                var buffer = new byte[65536];
                var stdout = _process.StandardOutput.BaseStream;
                try
                {
                    int read;
                    while ((read = await stdout.ReadAsync(buffer).ConfigureAwait(false)) > 0)
                    {
                        lock (_gate)
                        {
                            if (_output.Length + read > MaxReplyBytes * 4L)
                            {
                                // The other side stopped collecting: stop rather than grow.
                                Failed = true;
                                break;
                            }

                            _output.Write(buffer, 0, read);
                            _ready.TrySetResult();
                        }
                    }
                }
                catch (Exception)
                {
                    // Killed or broken pipe.
                }

                try
                {
                    if (_process.WaitForExit(5000) && _process.ExitCode != 0)
                    {
                        Failed = true;
                    }
                }
                catch (Exception)
                {
                }

                lock (_gate)
                {
                    _ready.TrySetResult();
                }
            }

            private async Task ReadErrorsAsync()
            {
                try
                {
                    string? line;
                    while ((line = await _process.StandardError.ReadLineAsync().ConfigureAwait(false)) != null)
                    {
                        lock (_error)
                        {
                            if (_error.Length < 2000)
                            {
                                _error.AppendLine(line);
                            }
                        }
                    }
                }
                catch (Exception)
                {
                }
            }
        }
    }
}
