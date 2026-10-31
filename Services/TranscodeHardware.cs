using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// What a server can convert video with, as friends see it
    /// (<c>Peer/Hardware</c>). Only model names and codec lists: no addresses,
    /// paths or load beyond a count of running conversions.
    /// </summary>
    public sealed class TranscodeHardwareInfo
    {
        /// <summary>Gets or sets "auto" (read from Jellyfin's playback settings) or "manual" (the admin described it).</summary>
        public string Source { get; set; } = "auto";

        /// <summary>Gets or sets the hardware acceleration in use ("vaapi", "nvenc", "qsv", "amf", "videotoolbox", "none").</summary>
        public string Acceleration { get; set; } = "none";

        /// <summary>Gets or sets a free-text GPU name, when known.</summary>
        public string? Gpu { get; set; }

        /// <summary>Gets or sets the CPU model, when known.</summary>
        public string? Cpu { get; set; }

        public int CpuThreads { get; set; }

        /// <summary>Gets or sets the codecs decoded in hardware ("h264", "hevc", "hevc10", "av1", "vp9").</summary>
        public List<string> HardwareDecode { get; set; } = new();

        /// <summary>Gets or sets the codecs encoded in hardware ("h264", "hevc", "av1").</summary>
        public List<string> HardwareEncode { get; set; } = new();

        /// <summary>Gets or sets a value indicating whether conversions run on another machine than the server (the admin said so).</summary>
        public bool Offloaded { get; set; }

        /// <summary>Gets or sets how many conversions are running right now.</summary>
        public int ActiveTranscodes { get; set; }

        /// <summary>Gets or sets the most conversions this server runs for one friend at once (0 = no limit).</summary>
        public int MaxTranscodesPerFriend { get; set; }

        /// <summary>A one-line description: "Radeon RX 9070 XT (vaapi, separate machine)".</summary>
        public string Describe()
        {
            var parts = new List<string>();
            var gpu = TranscodeHardware.Trim(Gpu, 80);
            if (gpu != null)
            {
                parts.Add(gpu);
            }
            else if (!string.Equals(Acceleration, "none", StringComparison.OrdinalIgnoreCase))
            {
                parts.Add(Acceleration.ToUpperInvariant() + " GPU");
            }

            var cpu = TranscodeHardware.Trim(Cpu, 80);
            if (cpu != null)
            {
                parts.Add(cpu + (CpuThreads > 0 ? " (" + CpuThreads + " threads)" : string.Empty));
            }
            else if (CpuThreads > 0)
            {
                parts.Add(CpuThreads + " CPU threads");
            }

            if (parts.Count == 0 && HardwareEncode.Count == 0 && HardwareDecode.Count == 0)
            {
                // Nothing shared (a friend before 0.0.190, or sharing off): saying
                // "converts on the CPU" would be a guess presented as fact.
                return "Not shared by this server";
            }

            var text = parts.Count == 0 ? "Unknown hardware" : string.Join(" + ", parts);
            if (HardwareEncode.Count > 0)
            {
                text += "; hardware encodes " + string.Join(", ", HardwareEncode.Select(c => c.ToUpperInvariant()));
            }
            else
            {
                text += "; converts on the CPU";
            }

            return Offloaded ? text + "; runs on a separate machine" : text;
        }
    }

    /// <summary>How well a server converts one source.</summary>
    public enum ConvertLevel
    {
        /// <summary>CPU only.</summary>
        Cpu = 0,

        /// <summary>Hardware encode, CPU decode.</summary>
        Encode = 1,

        /// <summary>Hardware decode and encode.</summary>
        Full = 2
    }

    /// <summary>
    /// Builds this server's <see cref="TranscodeHardwareInfo"/> and decides who
    /// should convert a friend's title. Pure apart from reading /proc/cpuinfo.
    /// </summary>
    public static class TranscodeHardware
    {
        /// <summary>Codec names used in <see cref="TranscodeHardwareInfo"/>.</summary>
        public static readonly string[] Codecs = { "h264", "hevc", "hevc10", "av1", "vp9" };

        private static readonly Lazy<string?> CpuModel = new(ReadCpuModel);

        /// <summary>
        /// This server's hardware: the admin's description when set to manual
        /// (for conversions offloaded to another machine, which Jellyfin's own
        /// settings can't show), else read from Jellyfin's playback settings.
        /// </summary>
        public static TranscodeHardwareInfo Local(PluginConfiguration? config, EncodingOptions? encoding, int activeTranscodes)
        {
            var info = new TranscodeHardwareInfo
            {
                Cpu = CpuModel.Value,
                CpuThreads = Environment.ProcessorCount,
                ActiveTranscodes = activeTranscodes,
                MaxTranscodesPerFriend = config?.MaxRemoteTranscodesPerFriend ?? 0
            };

            var accel = encoding?.HardwareAccelerationType.ToString() ?? "none";
            info.Acceleration = string.IsNullOrEmpty(accel) ? "none" : accel.ToLowerInvariant();

            if (string.Equals(config?.TranscodeHardwareMode, "manual", StringComparison.OrdinalIgnoreCase))
            {
                info.Source = "manual";
                info.Offloaded = config!.TranscodeHardwareOffloaded;
                info.Gpu = Trim(config.ManualTranscodeGpu, 80);
                info.HardwareDecode = Normalize(config.ManualHardwareDecodeCodecs);
                info.HardwareEncode = Normalize(config.ManualHardwareEncodeCodecs);
                if (info.Offloaded)
                {
                    // The server's own CPU doesn't convert anything.
                    info.Cpu = Trim(config.ManualTranscodeCpu, 80);
                    info.CpuThreads = config.ManualTranscodeCpuThreads > 0 ? config.ManualTranscodeCpuThreads : 0;
                }

                return info;
            }

            if (encoding == null || info.Acceleration == "none")
            {
                return info;
            }

            var decode = new List<string>();
            var hw = encoding.HardwareDecodingCodecs ?? Array.Empty<string>();
            foreach (var codec in new[] { "h264", "hevc", "av1", "vp9" })
            {
                if (hw.Contains(codec, StringComparer.OrdinalIgnoreCase))
                {
                    decode.Add(codec);
                }
            }

            if (decode.Contains("hevc") && encoding.EnableDecodingColorDepth10Hevc)
            {
                decode.Add("hevc10");
            }

            info.HardwareDecode = decode;
            if (encoding.EnableHardwareEncoding)
            {
                info.HardwareEncode.Add("h264");
                if (encoding.AllowHevcEncoding)
                {
                    info.HardwareEncode.Add("hevc");
                }

                if (encoding.AllowAv1Encoding)
                {
                    info.HardwareEncode.Add("av1");
                }
            }

            return info;
        }

        /// <summary>
        /// How well <paramref name="info"/> converts a source in
        /// <paramref name="codec"/> (10-bit when <paramref name="tenBit"/>) to H.264,
        /// the format players almost always ask for.
        /// </summary>
        public static ConvertLevel Level(TranscodeHardwareInfo? info, string? codec, bool tenBit)
        {
            if (info == null || !info.HardwareEncode.Contains("h264", StringComparer.OrdinalIgnoreCase))
            {
                return ConvertLevel.Cpu;
            }

            var name = CodecKey(codec);
            var decodeKey = name == "hevc" && tenBit ? "hevc10" : name;
            return name != null && info.HardwareDecode.Contains(decodeKey, StringComparer.OrdinalIgnoreCase)
                ? ConvertLevel.Full
                : ConvertLevel.Encode;
        }

        /// <summary>
        /// The least a server must manage for a source of this height: the
        /// admin's minimums for 4K and HD; anything below HD has none. A CPU with
        /// at least <see cref="PluginConfiguration.PlacementCpuThreadsCountAsEncode"/>
        /// threads counts as a hardware encoder.
        /// </summary>
        public static ConvertLevel Required(PluginConfiguration? config, int? height)
        {
            var h = height ?? 1080;
            if (h > 1440)
            {
                return ParseLevel(config?.PlacementMinimum4k, ConvertLevel.Full);
            }

            return h >= 720 ? ParseLevel(config?.PlacementMinimumHd, ConvertLevel.Encode) : ConvertLevel.Cpu;
        }

        /// <summary>
        /// Who converts a friend's title. The friend (its server holds the file,
        /// and only the converted stream crosses the internet) unless it falls
        /// short of the minimum for this quality, or is at its limit, while this
        /// server meets it and the link can carry the original. Never moves the
        /// work to a server that is no better.
        /// </summary>
        public static PlacementDecision Decide(
            PluginConfiguration? config,
            TranscodeHardwareInfo? own,
            TranscodeHardwareInfo? friend,
            string? codec,
            bool tenBit,
            int? height,
            bool linkCarriesOriginal)
        {
            if (config?.SmartTranscodePlacement != true)
            {
                return new PlacementDecision(true, "Friend converts (hardware-based choice is off)");
            }

            if (friend == null)
            {
                return new PlacementDecision(true, "Friend converts (it hasn't shared its hardware)");
            }

            var required = Required(config, height);
            var friendLevel = Effective(config, friend, codec, tenBit);
            var ownLevel = Effective(config, own, codec, tenBit);
            var friendFull = friend.MaxTranscodesPerFriend > 0 && friend.ActiveTranscodes >= friend.MaxTranscodesPerFriend;
            var friendShort = friendLevel < required || friendFull;
            if (!friendShort)
            {
                return new PlacementDecision(true, "Friend converts (" + Describe(friendLevel) + ")");
            }

            if (!linkCarriesOriginal)
            {
                return new PlacementDecision(true, "Friend converts: the link can't carry the original here");
            }

            if (ownLevel < required || (!friendFull && ownLevel <= friendLevel))
            {
                return new PlacementDecision(true, "Friend converts: this server is no better (" + Describe(ownLevel) + ")");
            }

            return new PlacementDecision(
                false,
                friendFull
                    ? "Converted here: the friend is at its conversion limit"
                    : "Converted here: the friend has " + Describe(friendLevel) + ", this source needs " + Describe(required));
        }

        /// <summary>Short words for a level.</summary>
        public static string Describe(ConvertLevel level) => level switch
        {
            ConvertLevel.Full => "full hardware conversion",
            ConvertLevel.Encode => "hardware encoding",
            _ => "CPU-only conversion"
        };

        /// <summary>The codec key for a stream codec name ("h265" and "hevc" are the same).</summary>
        internal static string? CodecKey(string? codec)
        {
            var c = (codec ?? string.Empty).Trim().ToLowerInvariant();
            return c switch
            {
                "h264" or "avc" => "h264",
                "hevc" or "h265" => "hevc",
                "av1" => "av1",
                "vp9" => "vp9",
                _ => null
            };
        }

        /// <summary>The main video stream's codec, 10-bit flag and height.</summary>
        public static (string? Codec, bool TenBit, int? Height) VideoOf(IEnumerable<MediaStream>? streams)
        {
            var video = streams?.FirstOrDefault(s => s.Type == MediaStreamType.Video && !s.IsExternal);
            if (video == null)
            {
                return (null, false, null);
            }

            var tenBit = (video.BitDepth ?? 8) >= 10
                || (video.Profile ?? string.Empty).Contains("10", StringComparison.Ordinal);
            return (video.Codec, tenBit, video.Height);
        }

        private static ConvertLevel Effective(PluginConfiguration? config, TranscodeHardwareInfo? info, string? codec, bool tenBit)
        {
            var level = Level(info, codec, tenBit);
            var threads = config?.PlacementCpuThreadsCountAsEncode ?? 16;
            return level == ConvertLevel.Cpu && info != null && threads > 0 && info.CpuThreads >= threads ? ConvertLevel.Encode : level;
        }

        private static ConvertLevel ParseLevel(string? value, ConvertLevel fallback) => (value ?? string.Empty).ToLowerInvariant() switch
        {
            "full" => ConvertLevel.Full,
            "encode" => ConvertLevel.Encode,
            "any" or "cpu" => ConvertLevel.Cpu,
            _ => fallback
        };

        private static List<string> Normalize(IEnumerable<string>? codecs)
            => (codecs ?? Array.Empty<string>())
                .Select(c => (c ?? string.Empty).Trim().ToLowerInvariant())
                .Where(c => Codecs.Contains(c))
                .Distinct()
                .ToList();

        internal static string? Trim(string? value, int max)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var t = new string(value.Where(c => !char.IsControl(c)).ToArray()).Trim();
            return t.Length <= max ? t : t.Substring(0, max);
        }

        private static string? ReadCpuModel()
        {
            try
            {
                if (!File.Exists("/proc/cpuinfo"))
                {
                    return null;
                }

                foreach (var line in File.ReadLines("/proc/cpuinfo"))
                {
                    if (line.StartsWith("model name", StringComparison.Ordinal))
                    {
                        var at = line.IndexOf(':', StringComparison.Ordinal);
                        return at < 0 ? null : Trim(line.Substring(at + 1), 80);
                    }
                }
            }
            catch (Exception)
            {
                // Not Linux, or not readable.
            }

            return null;
        }
    }

    /// <summary>The placement result: whether the friend converts, and why.</summary>
    public sealed record PlacementDecision(bool FriendConverts, string Reason);

    /// <summary>
    /// Friends' shared hardware, fetched from <c>Peer/Hardware</c> at most every
    /// 10 minutes per friend. Reads never wait on the network.
    /// </summary>
    public sealed class FriendHardwareCache
    {
        internal static readonly TimeSpan Refresh = TimeSpan.FromMinutes(10);

        private readonly FederationLibraryManager _federationManager;
        private readonly ConcurrentDictionary<string, (TranscodeHardwareInfo? Info, DateTime FetchedUtc)> _cache = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, byte> _inFlight = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Initializes a new instance of the <see cref="FriendHardwareCache"/> class.
        /// </summary>
        public FriendHardwareCache(FederationLibraryManager federationManager)
        {
            _federationManager = federationManager;
        }

        /// <summary>The friend's hardware as last fetched (null when unknown), starting a refresh when due.</summary>
        public TranscodeHardwareInfo? Get(RemoteServer server)
        {
            var known = _cache.TryGetValue(server.Id, out var entry);
            if (!known || DateTime.UtcNow - entry.FetchedUtc > Refresh)
            {
                _ = RefreshAsync(server);
            }

            return known ? entry.Info : null;
        }

        /// <summary>Fetches now (used when a playback info panel asks and nothing is known yet).</summary>
        public async Task<TranscodeHardwareInfo?> GetFreshAsync(RemoteServer server, CancellationToken cancellationToken)
        {
            if (_cache.TryGetValue(server.Id, out var entry) && DateTime.UtcNow - entry.FetchedUtc <= Refresh)
            {
                return entry.Info;
            }

            await RefreshAsync(server, cancellationToken).ConfigureAwait(false);
            return _cache.TryGetValue(server.Id, out entry) ? entry.Info : null;
        }

        /// <summary>Test hook.</summary>
        internal void Set(string serverId, TranscodeHardwareInfo? info) => _cache[serverId] = (info, DateTime.UtcNow);

        private async Task RefreshAsync(RemoteServer server, CancellationToken cancellationToken = default)
        {
            if (server.Kind != ServerKind.Jellyfin || !_inFlight.TryAdd(server.Id, 0))
            {
                return;
            }

            try
            {
                var client = _federationManager.GetClient(server.Id);
                if (client == null)
                {
                    return;
                }

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                var (status, info) = await client.GetPeerJsonAsync<TranscodeHardwareInfo>("Hardware", timeout.Token).ConfigureAwait(false);
                if (status is 200 or 204 or 403 or 404)
                {
                    // 204/403: the friend doesn't share it; 404: an older friend.
                    _cache[server.Id] = (status == 200 ? info : null, DateTime.UtcNow);
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                _inFlight.TryRemove(server.Id, out _);
            }
        }
    }

    /// <summary>
    /// Ties the hardware pieces to the running server: this server's own
    /// hardware (Jellyfin's playback settings or the admin's description, plus
    /// the conversions running now) and friends' shared hardware, and the
    /// placement choice made when a friend's title is about to play.
    /// </summary>
    public sealed class TranscodePlacementService
    {
        private readonly MediaBrowser.Controller.Configuration.IServerConfigurationManager _config;
        private readonly MediaBrowser.Controller.Session.ISessionManager _sessions;
        private readonly FriendHardwareCache _friends;
        private readonly ConcurrentDictionary<string, PlacementDecision> _recent = new(StringComparer.Ordinal);

        /// <summary>
        /// Initializes a new instance of the <see cref="TranscodePlacementService"/> class.
        /// </summary>
        public TranscodePlacementService(
            MediaBrowser.Controller.Configuration.IServerConfigurationManager config,
            MediaBrowser.Controller.Session.ISessionManager sessions,
            FriendHardwareCache friends)
        {
            _config = config;
            _sessions = sessions;
            _friends = friends;
        }

        /// <summary>Gets the friends' hardware cache.</summary>
        public FriendHardwareCache Friends => _friends;

        /// <summary>This server's hardware as friends see it.</summary>
        public TranscodeHardwareInfo Local()
        {
            EncodingOptions? encoding = null;
            try
            {
                encoding = _config.GetConfiguration("encoding") as EncodingOptions;
            }
            catch (Exception)
            {
                // Settings not readable: report what is known without them.
            }

            var active = 0;
            try
            {
                active = _sessions.Sessions.Count(s => s.TranscodingInfo != null);
            }
            catch (Exception)
            {
            }

            return TranscodeHardware.Local(Plugin.Instance?.Configuration, encoding, active);
        }

        /// <summary>
        /// Whether <paramref name="server"/> should convert this source for the
        /// viewer, remembered per item so the Playback Info panel can say why.
        /// </summary>
        public PlacementDecision Decide(RemoteServer server, Guid itemId, IEnumerable<MediaStream>? streams, bool linkCarriesOriginal)
        {
            var config = Plugin.Instance?.Configuration;
            var (codec, tenBit, height) = TranscodeHardware.VideoOf(streams);
            var decision = config?.SmartTranscodePlacement == true
                ? TranscodeHardware.Decide(config, Local(), _friends.Get(server), codec, tenBit, height, linkCarriesOriginal)
                : new PlacementDecision(true, "Friend converts (hardware-based choice is off)");
            _recent[server.Id + "|" + itemId.ToString("N")] = decision;
            return decision;
        }

        /// <summary>The last decision for this friend and item, if any.</summary>
        public PlacementDecision? Last(string serverId, Guid itemId)
            => _recent.TryGetValue(serverId + "|" + itemId.ToString("N"), out var decision) ? decision : null;
    }
}
