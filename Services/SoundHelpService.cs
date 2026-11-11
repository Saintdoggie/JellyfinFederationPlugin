using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Plays a friend's title on a device that shows the picture as stored but
    /// can't take the file as it is, on a server that may not convert:
    /// <list type="bullet">
    /// <item>only the wrapper is wrong: the picture and sound are copied into
    /// one the device plays (no conversion at all);</item>
    /// <item>the sound doesn't play (DTS in a browser, say): the picture is copied
    /// untouched, the sound alone goes to a linked server that may convert
    /// (<see cref="SoundHelpEncoder"/>), and comes back as AAC stereo.</item>
    /// </list>
    /// The player gets one progressive stream; seeking asks for a new one. The
    /// picture never leaves this server. In memory only.
    /// </summary>
    public sealed class SoundHelpService : IDisposable
    {
        /// <summary>Route of the stream the player is given.</summary>
        internal const string StreamRoute = "Plugins/Federation/SoundHelp/";

        /// <summary>A prepared play must start within this time.</summary>
        internal static readonly TimeSpan StartWithin = TimeSpan.FromMinutes(15);

        /// <summary>A started play is forgotten after this long without a request.</summary>
        internal static readonly TimeSpan ForgetAfter = TimeSpan.FromHours(12);

        /// <summary>Plays run at once on this server.</summary>
        internal const int MaxRuns = 3;

        /// <summary>Picture held while the sound comes back from the linked server.</summary>
        internal const long PictureBufferBytes = 64L * 1024 * 1024;

        private static readonly TimeSpan HelperCheckEvery = TimeSpan.FromMinutes(2);

        private readonly ConcurrentDictionary<string, Ticket> _tickets = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, (DateTime CheckedUtc, bool Available)> _helpers = new(StringComparer.Ordinal);
        private readonly ILogger<SoundHelpService> _logger;
        private readonly IRemoteServerClientFactory _clients;
        private readonly Func<string?> _encoderPath;
        private int _running;
        private double _convertStartSeconds = 8;
        private double _copyStartSeconds = 3;

        /// <summary>Initializes a new instance of the <see cref="SoundHelpService"/> class.</summary>
        public SoundHelpService(ILogger<SoundHelpService> logger, IRemoteServerClientFactory clients, Func<string?> encoderPath)
        {
            _logger = logger;
            _clients = clients;
            _encoderPath = encoderPath;
        }

        internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

        /// <summary>Test seam: the linked servers' answer to "can you convert sound?".</summary>
        internal Func<RemoteServer, CancellationToken, Task<bool>>? HelperCheck { get; set; }

        private static PluginConfiguration? Config => Plugin.Instance?.Configuration;

        /// <summary>
        /// Called with the answer to a player's play request: replaces the way a
        /// federated source would be played with sound help when nothing else
        /// can play it on this device. Returns how many sources were changed.
        /// </summary>
        internal async Task<int> ApplyAsync(MediaBrowser.Model.MediaInfo.PlaybackInfoResponse response, SoundHelpRequest request, CancellationToken cancellationToken)
        {
            if (TranscodePolicy.MayConvertForOwnViewers(Config) || response.MediaSources == null || string.IsNullOrWhiteSpace(_encoderPath()))
            {
                return 0;
            }

            var changed = 0;
            foreach (var source in response.MediaSources)
            {
                if (!NeedsHelp(source) || FriendConverts(request.UserId, request.ItemId, source.Id))
                {
                    continue;
                }

                var plan = DevicePlayFit.PlanSoundHelp(request.Profile, source.MediaStreams, request.AudioStreamIndex ?? source.DefaultAudioStreamIndex, request.Language);
                if (plan == null)
                {
                    continue;
                }

                RemoteServer? helper = null;
                if (plan.ConvertSound)
                {
                    helper = await FindHelperAsync(cancellationToken).ConfigureAwait(false);
                    if (helper == null)
                    {
                        continue;
                    }
                }

                var ticket = new Ticket(
                    NewId(),
                    request.UserId,
                    request.ItemId,
                    source.Path!,
                    source.RequiredHttpHeaders?.ToDictionary(p => p.Key, p => p.Value) ?? new Dictionary<string, string>(),
                    plan,
                    Math.Max(0, request.StartTimeTicks ?? 0),
                    helper?.Id,
                    helper?.Name,
                    UtcNow());
                _tickets[ticket.Id] = ticket;
                Sweep();

                source.SupportsDirectPlay = false;
                source.SupportsDirectStream = false;
                source.SupportsTranscoding = true;
                source.TranscodingUrl = "/" + StreamRoute + ticket.Id + "/stream." + plan.Wrapper
                    + "?StartTimeTicks=" + ticket.StartTicks.ToString(CultureInfo.InvariantCulture)
                    + (string.IsNullOrEmpty(response.PlaySessionId) ? string.Empty : "&PlaySessionId=" + Uri.EscapeDataString(response.PlaySessionId));
                source.TranscodingSubProtocol = Jellyfin.Data.Enums.MediaStreamProtocol.http;
                source.TranscodingContainer = plan.Wrapper;
                source.DefaultAudioStreamIndex = plan.Audio.Index;
                changed++;
                _logger.LogInformation(
                    "[Federation] {Item}: the device plays the picture as stored; {How}",
                    request.ItemId,
                    plan.ConvertSound ? $"the {plan.Audio.Codec} sound goes to {helper!.Name} to become AAC stereo" : "copying it into an " + plan.Wrapper.ToUpperInvariant() + " wrapper (no conversion)");
            }

            return changed;
        }

        /// <summary>
        /// A federated source this server would have to convert (or repackage) for
        /// the device, with no friend converting it: the only kind sound help takes.
        /// </summary>
        internal static bool NeedsHelp(MediaSourceInfo source)
        {
            if (source == null || string.IsNullOrWhiteSpace(source.Path) || source.Protocol != MediaProtocol.Http)
            {
                return false;
            }

            // Plays as stored. (Jellyfin's own SupportsTranscoding is no guide here:
            // it offers its converter whatever the source said.)
            return !(source.SupportsDirectPlay && string.IsNullOrEmpty(source.TranscodingUrl));
        }

        /// <summary>
        /// True when the friend owning this source converts it for this viewer
        /// (a handoff): that play needs nothing from here.
        /// </summary>
        internal static bool FriendConverts(Guid userId, Guid itemId, string? sourceId)
        {
            if (PeerPlaybackGrants.Find(userId, itemId, sourceId) is not { } grant)
            {
                return false;
            }

            var server = Config?.RemoteServers?.FirstOrDefault(s => s.Id == grant.ServerId);
            return FederationMediaSourceProvider.UsesRemoteTranscode(server);
        }

        /// <summary>
        /// What the player's loading bar shows while sound help starts: an ETA and
        /// why. Null when this viewer has no sound-help play starting for the title.
        /// </summary>
        internal (double Seconds, string Reason)? Starting(Guid userId, Guid itemId)
        {
            var now = UtcNow();
            var ticket = _tickets.Values
                .Where(t => t.UserId == userId && t.ItemId == itemId && t.FirstByteUtc == null && now - t.CreatedUtc < TimeSpan.FromMinutes(3))
                .OrderByDescending(t => t.CreatedUtc)
                .FirstOrDefault();
            if (ticket == null)
            {
                return null;
            }

            var expected = ticket.Plan.ConvertSound ? _convertStartSeconds : _copyStartSeconds;
            var since = (now - (ticket.RunStartedUtc ?? ticket.CreatedUtc)).TotalSeconds;
            var reason = ticket.Plan.ConvertSound
                ? $"This device can't play this title's {CodecName(ticket.Plan.Audio.Codec)} sound and this server doesn't convert, so {ticket.HelperName} is converting only the sound. The picture stays untouched; starting takes a few seconds longer."
                : $"This device needs the title in another file type. Repackaging it here, without converting anything.";
            return (Math.Max(1, Math.Round(expected - since)), reason);
        }

        /// <summary>Streams a prepared play to the player.</summary>
        internal async Task RunAsync(string ticketId, HttpContext context)
        {
            var response = context.Response;
            response.Headers.CacheControl = "no-store";
            if (!_tickets.TryGetValue(ticketId, out var ticket) || !Alive(ticket))
            {
                response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            if (HttpMethods.IsHead(context.Request.Method))
            {
                response.StatusCode = StatusCodes.Status200OK;
                response.ContentType = ContentType(ticket.Plan.Wrapper);
                return;
            }

            var encoder = _encoderPath();
            if (string.IsNullOrWhiteSpace(encoder))
            {
                response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }

            // A player that opens the stream again (a retry, a probe) replaces its earlier run.
            using var run = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            var previous = Interlocked.Exchange(ref ticket.Run, run);
            if (previous != null)
            {
                try
                {
                    previous.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            }

            if (Interlocked.Increment(ref _running) > MaxRuns)
            {
                Interlocked.Decrement(ref _running);
                response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }

            ticket.LastUsedUtc = UtcNow();
            ticket.RunStartedUtc = UtcNow();
            try
            {
                response.StatusCode = StatusCodes.Status200OK;
                response.ContentType = ContentType(ticket.Plan.Wrapper);
                if (ticket.Plan.ConvertSound)
                {
                    await RunWithHelperAsync(ticket, encoder!, response.Body, run.Token).ConfigureAwait(false);
                }
                else
                {
                    await RunCopyAsync(ticket, encoder!, response.Body, run.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // The player went away or opened it again.
            }
            catch (Exception ex) when (ex is IOException or SoundHelpException or SocketException or InvalidOperationException)
            {
                _logger.LogWarning("[Federation] Sound help for {Item} stopped: {Error}", ticket.ItemId, ex.Message);
            }
            finally
            {
                Interlocked.Decrement(ref _running);
                Interlocked.CompareExchange(ref ticket.Run, null, run);
                ticket.LastUsedUtc = UtcNow();
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            foreach (var ticket in _tickets.Values)
            {
                try
                {
                    ticket.Run?.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            }

            _tickets.Clear();
        }

        /// <summary>The ffmpeg arguments that read the title once: picture to standard output, sound to <paramref name="soundPort"/>.</summary>
        internal static List<string> ReaderArguments(Ticket ticket, int soundPort)
        {
            var args = Start(ticket, readsStdin: false);
            args.Add("-copyts");
            args.AddRange(Input(ticket));
            args.AddRange(new[] { "-map", "0:v:0", "-c", "copy", "-f", "matroska", "pipe:1" });
            args.AddRange(new[] { "-map", "0:" + ticket.Plan.Audio.Index.ToString(CultureInfo.InvariantCulture), "-c", "copy", "-f", "matroska", "tcp://127.0.0.1:" + soundPort.ToString(CultureInfo.InvariantCulture) });
            return args;
        }

        /// <summary>The ffmpeg arguments that join the picture (standard input) and the converted sound (<paramref name="soundPort"/>) for the player.</summary>
        internal static List<string> WriterArguments(Ticket ticket, int soundPort)
        {
            var args = new List<string> { "-hide_banner", "-loglevel", "error", "-copyts" };

            // Both inputs keep the title's own timestamps, so moving both back by
            // the start point keeps the sound on the picture.
            var offset = ticket.StartTicks > 0
                ? new[] { "-itsoffset", "-" + (ticket.StartTicks / 10_000_000.0).ToString("0.000", CultureInfo.InvariantCulture) }
                : Array.Empty<string>();
            args.AddRange(new[] { "-probesize", "2000000", "-analyzeduration", "1000000" });
            args.AddRange(offset);
            args.AddRange(new[] { "-f", "matroska", "-i", "pipe:0" });
            args.AddRange(new[] { "-probesize", "65536", "-analyzeduration", "1000000" });
            args.AddRange(offset);
            args.AddRange(new[] { "-f", "matroska", "-i", "tcp://127.0.0.1:" + soundPort.ToString(CultureInfo.InvariantCulture) });
            args.AddRange(new[] { "-map", "0:v:0", "-map", "1:a:0", "-c", "copy", "-avoid_negative_ts", "make_zero" });
            args.AddRange(Output(ticket.Plan));
            return args;
        }

        /// <summary>The ffmpeg arguments for a play that only changes the wrapper.</summary>
        internal static List<string> CopyArguments(Ticket ticket)
        {
            var args = Start(ticket, readsStdin: false);
            args.AddRange(Input(ticket));
            args.AddRange(new[] { "-map", "0:v:0", "-map", "0:" + ticket.Plan.Audio.Index.ToString(CultureInfo.InvariantCulture), "-c", "copy" });
            args.AddRange(Output(ticket.Plan));
            return args;
        }

        internal static string ContentType(string wrapper) => wrapper switch
        {
            "mp4" => "video/mp4",
            "mkv" => "video/x-matroska",
            _ => "video/mp2t"
        };

        private static List<string> Start(Ticket ticket, bool readsStdin)
        {
            var args = new List<string> { "-hide_banner", "-loglevel", "error" };
            if (!readsStdin)
            {
                args.Add("-nostdin");
            }

            return args;
        }

        private static IEnumerable<string> Input(Ticket ticket)
        {
            if (ticket.StartTicks > 0)
            {
                yield return "-ss";
                yield return (ticket.StartTicks / 10_000_000.0).ToString("0.000", CultureInfo.InvariantCulture);
            }

            if (ticket.Headers.Count > 0)
            {
                yield return "-headers";
                yield return string.Concat(ticket.Headers
                    .Where(h => !h.Key.Contains('\r') && !h.Key.Contains('\n') && !h.Value.Contains('\r') && !h.Value.Contains('\n'))
                    .Select(h => h.Key + ": " + h.Value + "\r\n"));
            }

            yield return "-i";
            yield return ticket.InputPath;
        }

        private static IEnumerable<string> Output(SoundHelpPlan plan)
        {
            if (plan.Wrapper == "mp4" && (plan.Video.Codec ?? string.Empty).ToLowerInvariant() is "hevc" or "h265")
            {
                yield return "-tag:v";
                yield return "hvc1";
            }

            switch (plan.Wrapper)
            {
                case "mp4":
                    yield return "-f";
                    yield return "mp4";
                    yield return "-movflags";
                    yield return "frag_keyframe+empty_moov+default_base_moof";
                    break;
                case "mkv":
                    yield return "-f";
                    yield return "matroska";
                    break;
                default:
                    yield return "-f";
                    yield return "mpegts";
                    break;
            }

            yield return "pipe:1";
        }

        private static string CodecName(string? codec) => (codec ?? "this").ToLowerInvariant() switch
        {
            "dts" or "dca" => "DTS",
            "truehd" => "Dolby TrueHD",
            "eac3" => "Dolby Digital Plus",
            "ac3" => "Dolby Digital",
            "flac" => "FLAC",
            "opus" => "Opus",
            var other => other.ToUpperInvariant()
        };

        private static string NewId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

        private async Task RunCopyAsync(Ticket ticket, string encoder, Stream output, CancellationToken cancellationToken)
        {
            using var copy = FfmpegRun.Start(encoder, CopyArguments(ticket), stdin: false);
            using var kill = cancellationToken.Register(copy.Kill);
            await SendAsync(ticket, copy, output, false, cancellationToken).ConfigureAwait(false);
        }

        private async Task RunWithHelperAsync(Ticket ticket, string encoder, Stream output, CancellationToken cancellationToken)
        {
            var helper = (Config?.RemoteServers ?? new List<RemoteServer>()).FirstOrDefault(s => s.Id == ticket.HelperId && s.Linked && s.Enabled)
                ?? throw new SoundHelpException("the linked server is gone");
            var client = _clients.GetClient(helper);
            var playId = NewId();

            var soundOut = new TcpListener(IPAddress.Loopback, 0);
            var soundBack = new TcpListener(IPAddress.Loopback, 0);
            soundOut.Start(1);
            soundBack.Start(1);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            FfmpegRun? reader = null;
            FfmpegRun? writer = null;
            try
            {
                reader = FfmpegRun.Start(encoder, ReaderArguments(ticket, ((IPEndPoint)soundOut.LocalEndpoint).Port), stdin: false);
                writer = FfmpegRun.Start(encoder, WriterArguments(ticket, ((IPEndPoint)soundBack.LocalEndpoint).Port), stdin: true);
                using var killReader = stop.Token.Register(reader.Kill);
                using var killWriter = stop.Token.Register(writer.Kill);

                // The picture waits here (up to PictureBufferBytes) while its sound
                // makes the round trip, so neither ffmpeg ever blocks the other.
                var picture = new Pipe(new PipeOptions(pauseWriterThreshold: PictureBufferBytes, resumeWriterThreshold: PictureBufferBytes / 2));
                var pictureIn = Task.Run(async () =>
                {
                    await reader.Output.CopyToAsync(picture.Writer, stop.Token).ConfigureAwait(false);
                    await picture.Writer.CompleteAsync().ConfigureAwait(false);
                });
                var pictureOut = Task.Run(async () =>
                {
                    await picture.Reader.CopyToAsync(writer.Input!, stop.Token).ConfigureAwait(false);
                    writer.Input!.Close();
                });

                var sound = Task.Run(() => RelaySoundAsync(soundOut, soundBack, client, helper, playId, stop.Token));
                var send = SendAsync(ticket, writer, output, true, stop.Token);

                var all = new List<Task> { pictureIn, pictureOut, sound, send };
                while (all.Count > 0)
                {
                    var done = await Task.WhenAny(all).ConfigureAwait(false);
                    all.Remove(done);
                    if (done.IsFaulted || done.IsCanceled || done == send)
                    {
                        stop.Cancel();
                        await done.ConfigureAwait(false);
                        break;
                    }
                }

                if (send.IsCompletedSuccessfully && writer.ExitCode is int code && code != 0)
                {
                    throw new SoundHelpException(writer.Errors);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && (reader?.Errors is { Length: > 0 } || writer?.Errors is { Length: > 0 }))
            {
                throw new SoundHelpException((reader?.Errors + " " + writer?.Errors).Trim());
            }
            finally
            {
                stop.Cancel();
                soundOut.Stop();
                soundBack.Stop();
                reader?.Dispose();
                writer?.Dispose();
                _ = Task.Run(async () =>
                {
                    using var quick = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await client.SendLinkSoundAsync(playId + "/Stop", Array.Empty<byte>(), helper.LinkKey, quick.Token).ConfigureAwait(false);
                });
            }
        }

        // Sound from the reader goes to the linked server in pieces; what comes
        // back goes to the writer, in order, as one continuous track.
        private async Task RelaySoundAsync(TcpListener soundOut, TcpListener soundBack, RemoteServerClient client, RemoteServer helper, string playId, CancellationToken cancellationToken)
        {
            using var acceptWindow = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            acceptWindow.CancelAfter(TimeSpan.FromSeconds(60));
            using var fromReader = await soundOut.AcceptTcpClientAsync(acceptWindow.Token).ConfigureAwait(false);
            using var toWriter = await soundBack.AcceptTcpClientAsync(acceptWindow.Token).ConfigureAwait(false);
            soundOut.Stop();
            soundBack.Stop();
            var into = toWriter.GetStream();

            var pending = new Pipe(new PipeOptions(pauseWriterThreshold: 8L * 1024 * 1024, resumeWriterThreshold: 4L * 1024 * 1024));
            var fill = Task.Run(async () =>
            {
                try
                {
                    await fromReader.GetStream().CopyToAsync(pending.Writer, cancellationToken).ConfigureAwait(false);
                    await pending.Writer.CompleteAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    await pending.Writer.CompleteAsync(ex).ConfigureAwait(false);
                }
            });

            var sequence = 0;
            while (true)
            {
                var read = pending.Reader.ReadAsync(cancellationToken);
                if (!read.IsCompleted)
                {
                    var waited = read.AsTask();
                    if (await Task.WhenAny(waited, Task.Delay(400, cancellationToken)).ConfigureAwait(false) != waited)
                    {
                        pending.Reader.CancelPendingRead();
                    }

                    read = new ValueTask<ReadResult>(waited);
                }

                var result = await read.ConfigureAwait(false);
                var piece = result.Buffer.Slice(0, Math.Min(result.Buffer.Length, SoundHelpEncoder.MaxPieceBytes / 2));
                var bytes = piece.ToArray();
                var end = result.IsCompleted && piece.Length == result.Buffer.Length;
                pending.Reader.AdvanceTo(piece.End);

                var (status, back) = await client.SendLinkSoundAsync(
                    playId + "?seq=" + sequence.ToString(CultureInfo.InvariantCulture) + "&end=" + (end ? "1" : "0"),
                    bytes,
                    helper.LinkKey,
                    cancellationToken).ConfigureAwait(false);
                if (status != 200 || back == null)
                {
                    throw new SoundHelpException(status switch
                    {
                        0 => $"{helper.Name} could not be reached",
                        503 => $"{helper.Name} can't convert sound right now",
                        _ => $"{helper.Name} answered {status}"
                    });
                }

                sequence++;
                if (back.Length > 0)
                {
                    await into.WriteAsync(back, cancellationToken).ConfigureAwait(false);
                }

                if (end)
                {
                    await into.FlushAsync(cancellationToken).ConfigureAwait(false);
                    toWriter.Client.Shutdown(SocketShutdown.Send);
                    await pending.Reader.CompleteAsync().ConfigureAwait(false);
                    await fill.ConfigureAwait(false);
                    return;
                }

                if (bytes.Length == 0 && back.Length == 0)
                {
                    await Task.Delay(150, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private async Task SendAsync(Ticket ticket, FfmpegRun run, Stream output, bool convert, CancellationToken cancellationToken)
        {
            var buffer = new byte[65536];
            var first = true;
            int read;
            while ((read = await run.Output.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (first)
                {
                    first = false;
                    var now = UtcNow();
                    ticket.FirstByteUtc ??= now;
                    var took = (now - (ticket.RunStartedUtc ?? now)).TotalSeconds;
                    if (convert)
                    {
                        _convertStartSeconds = (_convertStartSeconds * 0.7) + (Math.Clamp(took, 1, 60) * 0.3);
                    }
                    else
                    {
                        _copyStartSeconds = (_copyStartSeconds * 0.7) + (Math.Clamp(took, 0.5, 60) * 0.3);
                    }

                    _logger.LogInformation("[Federation] Sound help for {Item} started after {Seconds:0.0} s", ticket.ItemId, took);
                }

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                ticket.LastUsedUtc = UtcNow();
            }

            await run.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            if (run.ExitCode is int code && code != 0 && !cancellationToken.IsCancellationRequested)
            {
                throw new SoundHelpException(run.Errors);
            }
        }

        private async Task<RemoteServer?> FindHelperAsync(CancellationToken cancellationToken)
        {
            var linked = (Config?.RemoteServers ?? new List<RemoteServer>())
                .Where(s => s.Linked && s.Enabled && !string.IsNullOrEmpty(s.LinkKey) && !s.IsThroughLink)
                .ToList();
            foreach (var server in linked)
            {
                var now = UtcNow();
                if (_helpers.TryGetValue(server.Id, out var known) && now - known.CheckedUtc < HelperCheckEvery)
                {
                    if (known.Available)
                    {
                        return server;
                    }

                    continue;
                }

                bool available;
                try
                {
                    using var quick = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    quick.CancelAfter(TimeSpan.FromSeconds(3));
                    available = HelperCheck != null
                        ? await HelperCheck(server, quick.Token).ConfigureAwait(false)
                        : (await _clients.GetClient(server).SendLinkAsync<SoundHelpAvailability>(HttpMethod.Get, "Sound", null, server.LinkKey, quick.Token).ConfigureAwait(false)).Body?.Available == true;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    available = false;
                }

                _helpers[server.Id] = (now, available);
                if (available)
                {
                    return server;
                }
            }

            return null;
        }

        private bool Alive(Ticket ticket)
        {
            var now = UtcNow();
            return ticket.RunStartedUtc == null
                ? now - ticket.CreatedUtc <= StartWithin
                : now - ticket.LastUsedUtc <= ForgetAfter;
        }

        private void Sweep()
        {
            foreach (var pair in _tickets.ToList())
            {
                if (!Alive(pair.Value) && pair.Value.Run == null)
                {
                    _tickets.TryRemove(pair.Key, out _);
                }
            }
        }

        /// <summary>What the player asked for, read from its play request.</summary>
        internal sealed record SoundHelpRequest(Guid UserId, Guid ItemId, DeviceProfile? Profile, int? AudioStreamIndex, long? StartTimeTicks, string? Language);

        /// <summary>A prepared play.</summary>
        internal sealed class Ticket
        {
            public Ticket(string id, Guid userId, Guid itemId, string inputPath, Dictionary<string, string> headers, SoundHelpPlan plan, long startTicks, string? helperId, string? helperName, DateTime now)
            {
                Id = id;
                UserId = userId;
                ItemId = itemId;
                InputPath = inputPath;
                Headers = headers;
                Plan = plan;
                StartTicks = startTicks;
                HelperId = helperId;
                HelperName = helperName;
                CreatedUtc = now;
                LastUsedUtc = now;
            }

            public string Id { get; }

            public Guid UserId { get; }

            public Guid ItemId { get; }

            public string InputPath { get; }

            public Dictionary<string, string> Headers { get; }

            public SoundHelpPlan Plan { get; }

            public long StartTicks { get; }

            public string? HelperId { get; }

            public string? HelperName { get; }

            public DateTime CreatedUtc { get; }

            public DateTime LastUsedUtc { get; set; }

            public DateTime? RunStartedUtc { get; set; }

            public DateTime? FirstByteUtc { get; set; }

#pragma warning disable SA1401 // Interlocked needs a field.
            public CancellationTokenSource? Run;
#pragma warning restore SA1401
        }
    }

    /// <summary>The linked server's answer to "can you convert sound?".</summary>
    public sealed class SoundHelpAvailability
    {
        /// <summary>Gets or sets a value indicating whether sound can be sent now.</summary>
        public bool Available { get; set; }

        /// <summary>Gets or sets why not, in plain words.</summary>
        public string? Reason { get; set; }
    }

    /// <summary>Sound help could not go on; the message is safe to log.</summary>
    internal sealed class SoundHelpException : Exception
    {
        public SoundHelpException(string message)
            : base(message)
        {
        }
    }

    /// <summary>One ffmpeg with its pipes; stderr kept short for the log.</summary>
    internal sealed class FfmpegRun : IDisposable
    {
        private readonly Process _process;
        private readonly StringBuilder _errors = new();
        private int _disposed;

        private FfmpegRun(Process process)
        {
            _process = process;
            _ = Task.Run(ReadErrorsAsync);
        }

        public Stream Output => _process.StandardOutput.BaseStream;

        public Stream? Input { get; private set; }

        public int? ExitCode
        {
            get
            {
                try
                {
                    return _process.HasExited ? _process.ExitCode : null;
                }
                catch (InvalidOperationException)
                {
                    return null;
                }
            }
        }

        public string Errors
        {
            get
            {
                // ffmpeg names its input in errors; addresses can carry a friend's token.
                lock (_errors)
                {
                    return System.Text.RegularExpressions.Regex.Replace(_errors.ToString().Trim(), @"[a-zA-Z][a-zA-Z0-9+.-]*://\S+", "<address>");
                }
            }
        }

        public static FfmpegRun Start(string encoder, IEnumerable<string> arguments, bool stdin)
        {
            var info = new ProcessStartInfo
            {
                FileName = encoder,
                RedirectStandardInput = stdin,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var argument in arguments)
            {
                info.ArgumentList.Add(argument);
            }

            var process = Process.Start(info) ?? throw new InvalidOperationException("ffmpeg did not start");
            return new FfmpegRun(process) { Input = stdin ? process.StandardInput.BaseStream : null };
        }

        public Task WaitForExitAsync(CancellationToken cancellationToken) => _process.WaitForExitAsync(cancellationToken);

        public void Kill()
        {
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
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            Kill();
            _process.Dispose();
        }

        private async Task ReadErrorsAsync()
        {
            try
            {
                string? line;
                while ((line = await _process.StandardError.ReadLineAsync().ConfigureAwait(false)) != null)
                {
                    lock (_errors)
                    {
                        if (_errors.Length < 1500)
                        {
                            _errors.AppendLine(line);
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
