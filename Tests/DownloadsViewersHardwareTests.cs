using System;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.Federation.Configuration;
using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Session;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

/// <summary>
/// 0.0.190: where server downloads land, how friends' viewers are reported and
/// shown, and the hardware-based choice of who converts a friend's title.
/// </summary>
public class DownloadsViewersHardwareTests
{
    private const string PluginData = "/config/plugins/Jellyfin.Plugin.Federation";
    private const string DownloadsRoot = PluginData + "/federation-downloads";

    private static VirtualFolderInfo Folder(string name, CollectionTypeOptions? type, params string[] locations)
        => new() { Name = name, CollectionType = type, Locations = locations };

    // The live layout: each library also holds the plugin's own federated folder.
    private static readonly List<VirtualFolderInfo> Libraries = new()
    {
        Folder("Collections", CollectionTypeOptions.boxsets, "/config/data/collections"),
        Folder("Federation Downloads", CollectionTypeOptions.mixed, DownloadsRoot),
        Folder("Movies", CollectionTypeOptions.movies, PluginData + "/federation/Movies", "/media/movies"),
        Folder("Shows", CollectionTypeOptions.tvshows, PluginData + "/federation/Shows", "/media/tv"),
    };

    // ---- Download destinations ------------------------------------------

    [Fact]
    public void Auto_PutsFilmsInMovies_SkippingThePluginsOwnFolder()
    {
        var target = DownloadDestinations.Resolve(DownloadDestinations.Auto, "Movie", Libraries, PluginData, DownloadsRoot);
        Assert.True(target.Ok);
        Assert.Equal("/media/movies", target.Root);
        Assert.Equal("Movies", target.LibraryName);
        Assert.False(target.IsSeparate);
        Assert.Null(target.CreateLibrary);
    }

    [Fact]
    public void Auto_PutsEpisodesInShows()
    {
        var target = DownloadDestinations.Resolve(null, "Episode", Libraries, PluginData, DownloadsRoot);
        Assert.Equal("/media/tv", target.Root);
        Assert.Equal("Shows", target.LibraryName);
    }

    [Fact]
    public void Auto_FallsBackToTheSeparateLibrary_WhenNoLibraryMatches()
    {
        var onlyMovies = new List<VirtualFolderInfo> { Folder("Movies", CollectionTypeOptions.movies, "/media/movies") };
        var target = DownloadDestinations.Resolve("auto", "Episode", onlyMovies, PluginData, DownloadsRoot);
        Assert.True(target.IsSeparate);
        Assert.Equal(DownloadsRoot, target.Root);
    }

    [Fact]
    public void NamedLibrary_ResolvesToItsOwnFolder_AndUnknownOnesFail()
    {
        Assert.Equal("/media/tv", DownloadDestinations.Resolve("library:shows", "Movie", Libraries, PluginData, DownloadsRoot).Root);
        Assert.False(DownloadDestinations.Resolve("library:Anime", "Movie", Libraries, PluginData, DownloadsRoot).Ok);

        // A library made only of the plugin's own folders can't take files.
        var pluginOnly = new List<VirtualFolderInfo> { Folder("Odd", CollectionTypeOptions.movies, PluginData + "/federation/Odd") };
        Assert.False(DownloadDestinations.Resolve("library:Odd", "Movie", pluginOnly, PluginData, DownloadsRoot).Ok);
    }

    [Fact]
    public void NewFolderName_IsMadeBesideTheMatchingLibrary_AsItsOwnLibrary()
    {
        var target = DownloadDestinations.Resolve("folder:From friends", "Movie", Libraries, PluginData, DownloadsRoot);
        Assert.True(target.Ok, target.Error);
        Assert.Equal("/media/From friends", target.Root);
        Assert.Equal("From friends", target.CreateLibrary);
        Assert.Equal(CollectionTypeOptions.movies, target.CreateType);

        var shows = DownloadDestinations.Resolve("folder:Anime", "Episode", Libraries, PluginData, DownloadsRoot);
        Assert.Equal(CollectionTypeOptions.tvshows, shows.CreateType);
    }

    [Fact]
    public void NewFolder_ThatIsAlreadyALibraryFolder_UsesThatLibrary()
    {
        var existing = DownloadDestinations.Resolve("folder:/media/movies/", "Movie", Libraries, PluginData, DownloadsRoot);
        Assert.Equal("Movies", existing.LibraryName);
        Assert.Null(existing.CreateLibrary);

        var inside = DownloadDestinations.Resolve("folder:/media/movies/Friends", "Movie", Libraries, PluginData, DownloadsRoot);
        Assert.Equal("Movies", inside.LibraryName);
        Assert.Equal("/media/movies/Friends", inside.Root);
        Assert.Null(inside.CreateLibrary);
    }

    [Theory]
    [InlineData("folder:../etc")]
    [InlineData("folder:a/b")]
    [InlineData("folder:" + PluginData + "/mine")]
    [InlineData("folder:/")]
    [InlineData("somewhere")]
    public void BadDestinations_AreRefused(string choice)
    {
        Assert.False(DownloadDestinations.Resolve(choice, "Movie", Libraries, PluginData, DownloadsRoot).Ok);
    }

    [Fact]
    public void NewLibraryNames_DontCollide()
    {
        var target = DownloadDestinations.Resolve("folder:/data/Movies", "Movie", Libraries, PluginData, DownloadsRoot);
        Assert.Equal("Movies 2", target.CreateLibrary);
    }

    [Fact]
    public void List_OffersLibrariesWithAFolderOfTheirOwn()
    {
        var options = DownloadDestinations.List(Libraries, PluginData);
        Assert.Contains(options, o => o.Id == "library:Movies" && o.Path == "/media/movies");
        Assert.Contains(options, o => o.Id == "library:Shows" && o.Path == "/media/tv");
        Assert.DoesNotContain(options, o => o.Name == "Federation Downloads");
        Assert.DoesNotContain(options, o => o.Name == "Collections");
    }

    [Fact]
    public void Layout_NamesFilmsAndEpisodesTheWayJellyfinIdentifiesThem()
    {
        var movie = DownloadDestinations.Layout(new DownloadNaming("Ron's Gone Wrong", 2021, "Movie", null, null, null, "mp4"), flat: false);
        Assert.Equal("Ron's Gone Wrong (2021)", movie.Directory);
        Assert.Equal("Ron's Gone Wrong (2021).mp4", movie.FileName);

        var episode = DownloadDestinations.Layout(new DownloadNaming("Pilot", 2008, "Episode", "Breaking Bad", 1, 1, "mkv"), flat: false);
        Assert.Equal(Path.Combine("Breaking Bad", "Season 01"), episode.Directory);
        Assert.Equal("Breaking Bad S01E01.mkv", episode.FileName);

        var special = DownloadDestinations.Layout(new DownloadNaming("Extra", null, "Episode", "Show", 0, 3, "mkv"), flat: false);
        Assert.Equal(Path.Combine("Show", "Specials"), special.Directory);

        var flat = DownloadDestinations.Layout(new DownloadNaming("Ron's Gone Wrong", 2021, "Movie", null, null, null, "mp4"), flat: true);
        Assert.Equal(string.Empty, flat.Directory);
        Assert.Equal("Ron's Gone Wrong.mp4", flat.FileName);
    }

    [Fact]
    public void Layout_NeverLetsFriendMetadataEscapeTheFolder()
    {
        var hostile = DownloadDestinations.Layout(new DownloadNaming("../../etc/passwd", null, "Episode", "../x", 1, 2, "mkv"), flat: false);
        Assert.DoesNotContain("..", hostile.Directory.Replace("...", string.Empty, StringComparison.Ordinal));
        Assert.DoesNotContain("/", hostile.FileName);
    }

    [Fact]
    public void SecondCopyOfAFilm_IsNamedAsAVersion()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fed-dest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "Film (2021).mkv"), "x");
            var path = FederationDownloadService.GetUniqueDestinationPath(dir, "Film (2021).mkv", "friendflix");
            Assert.Equal(Path.Combine(dir, "Film (2021) - friendflix.mkv"), path);

            File.WriteAllText(path, "x");
            Assert.Equal(Path.Combine(dir, "Film (2021) - friendflix (2).mkv"), FederationDownloadService.GetUniqueDestinationPath(dir, "Film (2021).mkv", "friendflix"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void DownloadedFiles_OutsideTheDownloadsFolder_AreRemembered()
    {
        var store = Path.Combine(Path.GetTempPath(), "fed-downloaded-" + Guid.NewGuid().ToString("N") + ".json");
        DownloadedFileRegistry.StorePathOverride = store;
        DownloadedFileRegistry.Reset();
        try
        {
            Assert.False(DownloadedFileRegistry.Contains("/media/movies/A (2020)/A (2020).mkv"));
            DownloadedFileRegistry.Add("/media/movies/A (2020)/A (2020).mkv");
            Assert.True(DownloadedFileRegistry.Contains("/media/movies/A (2020)/A (2020).mkv"));

            // Survives a restart.
            DownloadedFileRegistry.Reset();
            Assert.True(DownloadedFileRegistry.Contains("/media/movies/A (2020)/A (2020).mkv"));
            Assert.True(FederationDownloadService.IsDownloadedFilePath("/media/movies/A (2020)/A (2020).mkv"));
            Assert.False(FederationDownloadService.IsDownloadedFilePath("/media/movies/B (2020)/B (2020).mkv"));
        }
        finally
        {
            DownloadedFileRegistry.StorePathOverride = null;
            DownloadedFileRegistry.Reset();
            if (File.Exists(store))
            {
                File.Delete(store);
            }
        }
    }

    // ---- Friends' viewers --------------------------------------------------

    private static RemoteServer Friend(StreamingMode mode = StreamingMode.Direct)
        => new() { Id = "f1", Name = "friendflix", Kind = ServerKind.Jellyfin, StreamingMode = mode, Enabled = true };

    [Fact]
    public void Route_FollowsPlayMethodModeAndHandoff()
    {
        Assert.Equal(FederatedRoute.Direct, FederatedRoute.Classify(Friend(), PlayMethod.DirectPlay, false));
        Assert.Equal(FederatedRoute.Relay, FederatedRoute.Classify(Friend(StreamingMode.Proxy), PlayMethod.DirectStream, false));
        Assert.Equal(FederatedRoute.FriendConverts, FederatedRoute.Classify(Friend(), PlayMethod.Transcode, true));
        Assert.Equal(FederatedRoute.ConvertedHere, FederatedRoute.Classify(Friend(), PlayMethod.Transcode, false));
    }

    [Fact]
    public void Owner_SeesConversionHereAsServingTheOriginal()
    {
        Assert.Equal("DirectStream", FederatedRoute.OwnerPlayMethod(FederatedRoute.ConvertedHere, PlayMethod.Transcode));
        Assert.Equal("Transcode", FederatedRoute.OwnerPlayMethod(FederatedRoute.FriendConverts, PlayMethod.Transcode));
        Assert.Equal("DirectPlay", FederatedRoute.OwnerPlayMethod(FederatedRoute.Direct, null));
    }

    [Fact]
    public void ProgressReports_AreSpacedButPauseAndSeeksGoAtOnce()
    {
        var t0 = new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
        var sent = new FederationViewerReporter.Sent(Friend(), Guid.NewGuid(), t0, 600 * TimeSpan.TicksPerSecond, false, FederatedRoute.Direct);
        var s = TimeSpan.TicksPerSecond;

        Assert.False(FederationViewerReporter.IsDue(sent, t0.AddSeconds(5), 605 * s, false, FederatedRoute.Direct));
        Assert.True(FederationViewerReporter.IsDue(sent, t0.AddSeconds(15), 615 * s, false, FederatedRoute.Direct));
        Assert.True(FederationViewerReporter.IsDue(sent, t0.AddSeconds(2), 602 * s, true, FederatedRoute.Direct));
        Assert.True(FederationViewerReporter.IsDue(sent, t0.AddSeconds(2), 1200 * s, false, FederatedRoute.Direct));
        Assert.True(FederationViewerReporter.IsDue(sent, t0.AddSeconds(2), 602 * s, false, FederatedRoute.FriendConverts));
    }

    [Fact]
    public void ReportKeys_AreStableAndDontRevealSessionIds()
    {
        var a = FederationViewerReporter.ReportKey("session-1", "play-1");
        Assert.Equal(a, FederationViewerReporter.ReportKey("session-1", "play-1"));
        Assert.NotEqual(a, FederationViewerReporter.ReportKey("session-1", "play-2"));
        Assert.DoesNotContain("session", a);
        Assert.Equal(24, a.Length);
    }

    [Fact]
    public void FriendsViewers_AreLabelledWithTheirServer()
    {
        Assert.Equal("bob (friendflix)", FederationRemoteViewerService.Label("bob", "friendflix"));
        Assert.Equal("Someone (friendflix)", FederationRemoteViewerService.Label("  ", "friendflix"));
        Assert.Equal("Chrome · via friendflix", FederationRemoteViewerService.DeviceLabel("Chrome", "friendflix"));
        Assert.Equal("Device · via friendflix", FederationRemoteViewerService.DeviceLabel(null, "friendflix"));
        Assert.Equal("dev-1", FederationRemoteViewerService.DeviceIdFor(Friend(), new PeerViewerReport { DeviceId = "dev-1", Key = "k" }));
        Assert.StartsWith("federation-viewer-f1-", FederationRemoteViewerService.DeviceIdFor(Friend(), new PeerViewerReport { Key = "k" }), StringComparison.Ordinal);
    }

    // ---- Conversion hardware ------------------------------------------------

    private static EncodingOptions Vaapi() => new()
    {
        HardwareAccelerationType = HardwareAccelerationType.vaapi,
        HardwareDecodingCodecs = new[] { "h264", "hevc", "vp9", "av1" },
        EnableDecodingColorDepth10Hevc = true,
        EnableHardwareEncoding = true,
        AllowHevcEncoding = true,
        AllowAv1Encoding = false
    };

    [Fact]
    public void Auto_ReadsJellyfinsPlaybackSettings()
    {
        var info = TranscodeHardware.Local(new PluginConfiguration(), Vaapi(), activeTranscodes: 2);
        Assert.Equal("auto", info.Source);
        Assert.Equal("vaapi", info.Acceleration);
        Assert.Equal(new[] { "h264", "hevc", "av1", "vp9", "hevc10" }, info.HardwareDecode);
        Assert.Equal(new[] { "h264", "hevc" }, info.HardwareEncode);
        Assert.Equal(2, info.ActiveTranscodes);

        var cpuOnly = TranscodeHardware.Local(new PluginConfiguration(), new EncodingOptions { HardwareAccelerationType = HardwareAccelerationType.none }, 0);
        Assert.Empty(cpuOnly.HardwareEncode);
        Assert.Contains("converts on the CPU", cpuOnly.Describe(), StringComparison.Ordinal);

        // A friend that shares nothing (before 0.0.190) is not guessed to be CPU-only.
        Assert.Equal("Not shared by this server", new TranscodeHardwareInfo().Describe());
    }

    [Fact]
    public void Manual_DescribesOffloadedConversion()
    {
        var config = new PluginConfiguration
        {
            TranscodeHardwareMode = "manual",
            TranscodeHardwareOffloaded = true,
            ManualTranscodeGpu = "Radeon RX 9070 XT",
            ManualTranscodeCpu = "Ryzen 9 7900X",
            ManualTranscodeCpuThreads = 24,
            ManualHardwareDecodeCodecs = new List<string> { "h264", "hevc", "hevc10", "av1", "bogus" },
            ManualHardwareEncodeCodecs = new List<string> { "h264", "hevc", "av1" }
        };
        var info = TranscodeHardware.Local(config, Vaapi(), 0);
        Assert.Equal("manual", info.Source);
        Assert.True(info.Offloaded);
        Assert.Equal(24, info.CpuThreads);
        Assert.DoesNotContain("bogus", info.HardwareDecode);
        Assert.Equal("Radeon RX 9070 XT + Ryzen 9 7900X (24 threads); hardware encodes H264, HEVC, AV1; runs on a separate machine", info.Describe());
    }

    [Fact]
    public void Levels_DependOnSourceCodecAndDepth()
    {
        var info = TranscodeHardware.Local(new PluginConfiguration(), Vaapi(), 0);
        Assert.Equal(ConvertLevel.Full, TranscodeHardware.Level(info, "hevc", tenBit: true));
        Assert.Equal(ConvertLevel.Full, TranscodeHardware.Level(info, "h264", false));
        Assert.Equal(ConvertLevel.Encode, TranscodeHardware.Level(info, "mpeg2video", false));
        Assert.Equal(ConvertLevel.Cpu, TranscodeHardware.Level(new TranscodeHardwareInfo(), "h264", false));
    }

    [Fact]
    public void Minimums_DependOnHeight()
    {
        var config = new PluginConfiguration();
        Assert.Equal(ConvertLevel.Full, TranscodeHardware.Required(config, 2160));
        Assert.Equal(ConvertLevel.Encode, TranscodeHardware.Required(config, 1080));
        Assert.Equal(ConvertLevel.Cpu, TranscodeHardware.Required(config, 480));
        config.PlacementMinimum4k = "any";
        Assert.Equal(ConvertLevel.Cpu, TranscodeHardware.Required(config, 2160));
    }

    private static TranscodeHardwareInfo Rx550 => new() { Gpu = "RX 550", CpuThreads = 4, HardwareDecode = new() { "h264" }, HardwareEncode = new() { "h264" } };

    private static TranscodeHardwareInfo Rx9070 => new() { Gpu = "RX 9070 XT", CpuThreads = 24, HardwareDecode = new() { "h264", "hevc", "hevc10", "av1" }, HardwareEncode = new() { "h264", "hevc", "av1" } };

    private static PluginConfiguration Smart() => new() { SmartTranscodePlacement = true };

    [Fact]
    public void Placement_IsOffByDefault()
    {
        var decision = TranscodeHardware.Decide(new PluginConfiguration(), Rx9070, Rx550, "hevc", true, 2160, linkCarriesOriginal: true);
        Assert.True(decision.FriendConverts);
    }

    [Fact]
    public void Placement_ConvertsHere_WhenTheFriendFallsShortAndThisServerIsBetter()
    {
        var decision = TranscodeHardware.Decide(Smart(), Rx9070, Rx550, "hevc", true, 2160, linkCarriesOriginal: true);
        Assert.False(decision.FriendConverts);
        Assert.Contains("needs full hardware conversion", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Placement_KeepsTheFriend_WhenItMeetsTheMinimum()
    {
        Assert.True(TranscodeHardware.Decide(Smart(), Rx9070, Rx550, "h264", false, 1080, true).FriendConverts);
        Assert.True(TranscodeHardware.Decide(Smart(), Rx550, Rx9070, "hevc", true, 2160, true).FriendConverts);
    }

    [Fact]
    public void Placement_NeverPullsAnOriginalTheLinkCantCarry()
    {
        var decision = TranscodeHardware.Decide(Smart(), Rx9070, Rx550, "hevc", true, 2160, linkCarriesOriginal: false);
        Assert.True(decision.FriendConverts);
        Assert.Contains("link", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Placement_NeverMovesWorkToAServerThatIsNoBetter()
    {
        var weak = new TranscodeHardwareInfo { CpuThreads = 4 };
        Assert.True(TranscodeHardware.Decide(Smart(), weak, Rx550, "hevc", true, 2160, true).FriendConverts);
        Assert.True(TranscodeHardware.Decide(Smart(), Rx9070, null, "hevc", true, 2160, true).FriendConverts);
    }

    [Fact]
    public void Placement_ConvertsHere_WhenTheFriendIsAtItsLimit()
    {
        var busy = new TranscodeHardwareInfo { CpuThreads = 24, HardwareDecode = new() { "h264", "hevc", "hevc10" }, HardwareEncode = new() { "h264" }, ActiveTranscodes = 2, MaxTranscodesPerFriend = 2 };
        var decision = TranscodeHardware.Decide(Smart(), Rx9070, busy, "hevc", true, 2160, true);
        Assert.False(decision.FriendConverts);
        Assert.Contains("limit", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ManyCpuThreads_CountAsAnEncoder()
    {
        var bigCpu = new TranscodeHardwareInfo { CpuThreads = 32 };
        var smallGpu = new TranscodeHardwareInfo { CpuThreads = 4 };
        Assert.False(TranscodeHardware.Decide(Smart(), bigCpu, smallGpu, "h264", false, 1080, true).FriendConverts);

        var config = Smart();
        config.PlacementCpuThreadsCountAsEncode = 0;
        Assert.True(TranscodeHardware.Decide(config, bigCpu, smallGpu, "h264", false, 1080, true).FriendConverts);
    }

    [Fact]
    public void VideoOf_ReadsCodecDepthAndHeight()
    {
        var streams = new[]
        {
            new MediaStream { Type = MediaStreamType.Audio, Codec = "aac" },
            new MediaStream { Type = MediaStreamType.Video, Codec = "hevc", BitDepth = 10, Height = 2160 }
        };
        var (codec, tenBit, height) = TranscodeHardware.VideoOf(streams);
        Assert.Equal("hevc", codec);
        Assert.True(tenBit);
        Assert.Equal(2160, height);
        Assert.Equal((null, false, null), TranscodeHardware.VideoOf(null));
    }
}
