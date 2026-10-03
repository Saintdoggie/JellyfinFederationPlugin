using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Configuration;
using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

/// <summary>
/// Federated sources get a bounded ffmpeg analysis window so a transcode or remux does
/// not first read up to a gigabyte of a remote file (see <see cref="FastStartTuning"/>).
/// Only the analysis window may change; the sources must otherwise be left alone.
/// </summary>
[Collection("PluginInstance")]
public class FastStartTuningTests : IDisposable
{
    private readonly RealPluginInstance _plugin = new();

    public void Dispose() => _plugin.Dispose();

    private static Movie FederatedMovie() => new()
    {
        Id = Guid.NewGuid(),
        ProviderIds = new Dictionary<string, string> { ["FederationKey"] = "Movies/imdb/tt1" },
    };

    private static Movie LocalMovie() => new() { Id = Guid.NewGuid() };

    private static MediaSourceInfo HttpSource(int? bitrate = 85_289_000) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Protocol = MediaProtocol.Http,
        Bitrate = bitrate,
    };

    [Theory]
    [InlineData(null, FastStartTuning.DefaultAnalyzeDurationMs)]
    [InlineData(0L, FastStartTuning.DefaultAnalyzeDurationMs)]
    [InlineData(-5L, FastStartTuning.DefaultAnalyzeDurationMs)]
    [InlineData(85_289_000L, FastStartTuning.MinAnalyzeDurationMs)]   // 4K remux: floor
    [InlineData(48_000_000L, 6666)]                                    // ~40 MB worth
    [InlineData(5_000_000L, FastStartTuning.MaxAnalyzeDurationMs)]    // small file: ceiling
    public void ComputeAnalyzeDurationMs_ScalesWithBitrate_WithinBounds(long? bitrate, int expected)
        => Assert.Equal(expected, FastStartTuning.ComputeAnalyzeDurationMs(bitrate));

    [Fact]
    public void ComputeAnalyzeDurationMs_AlwaysWithinBounds()
    {
        for (long mbps = 1; mbps <= 500; mbps++)
        {
            var ms = FastStartTuning.ComputeAnalyzeDurationMs(mbps * 1_000_000);
            Assert.InRange(ms, FastStartTuning.MinAnalyzeDurationMs, FastStartTuning.MaxAnalyzeDurationMs);
        }
    }

    [Fact]
    public void ComputeAnalyzeDurationMs_FarShorterThanJellyfinDefault()
    {
        // Jellyfin's server default is 200M microseconds = 200,000 ms.
        Assert.True(FastStartTuning.ComputeAnalyzeDurationMs(85_289_000) * 20 < 200_000);
    }

    [Fact]
    public void Apply_SetsWindowOnFederatedHttpSource()
    {
        var source = HttpSource();
        var changed = FastStartTuning.Apply(FederatedMovie(), new[] { source });

        Assert.Equal(1, changed);
        Assert.Equal(FastStartTuning.MinAnalyzeDurationMs, source.AnalyzeDurationMs);
    }

    [Fact]
    public void Apply_ChangesOnlyAnalyzeDuration()
    {
        var source = HttpSource();
        source.Container = "mkv";
        source.Path = "http://127.0.0.1:8096/Plugins/Federation/Stream?x=1";
        source.SupportsTranscoding = true;
        source.SupportsDirectPlay = true;
        source.Size = 68_259_671_320;
        source.MediaStreams = new List<MediaStream> { new() { Type = MediaStreamType.Video, Index = 0, Codec = "hevc" } };
        var before = System.Text.Json.JsonSerializer.Serialize(source);

        FastStartTuning.Apply(FederatedMovie(), new[] { source });
        source.AnalyzeDurationMs = null;

        Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(source));
    }

    [Fact]
    public void Apply_LeavesNonFederatedItemsAlone()
    {
        var source = HttpSource();
        Assert.Equal(0, FastStartTuning.Apply(LocalMovie(), new[] { source }));
        Assert.Null(source.AnalyzeDurationMs);
    }

    [Fact]
    public void Apply_LeavesLocalFileSourcesAlone()
    {
        var source = HttpSource();
        source.Protocol = MediaProtocol.File;
        Assert.Equal(0, FastStartTuning.Apply(FederatedMovie(), new[] { source }));
        Assert.Null(source.AnalyzeDurationMs);
    }

    [Fact]
    public void Apply_KeepsAnExistingWindow()
    {
        var source = HttpSource();
        source.AnalyzeDurationMs = 20_000;
        Assert.Equal(0, FastStartTuning.Apply(FederatedMovie(), new[] { source }));
        Assert.Equal(20_000, source.AnalyzeDurationMs);
    }

    [Fact]
    public void Apply_FallsBackToSummedStreamBitrate()
    {
        var source = HttpSource(bitrate: null);
        source.MediaStreams = new List<MediaStream>
        {
            new() { Type = MediaStreamType.Video, BitRate = 4_000_000 },
            new() { Type = MediaStreamType.Audio, BitRate = 1_000_000 },
        };

        FastStartTuning.Apply(FederatedMovie(), new[] { source });

        Assert.Equal(FastStartTuning.MaxAnalyzeDurationMs, source.AnalyzeDurationMs);
    }

    [Fact]
    public void Apply_UnknownBitrate_UsesDefault()
    {
        var source = HttpSource(bitrate: null);
        FastStartTuning.Apply(FederatedMovie(), new[] { source });
        Assert.Equal(FastStartTuning.DefaultAnalyzeDurationMs, source.AnalyzeDurationMs);
    }

    [Fact]
    public void Apply_TunesEveryHttpSource_AndSkipsOthers()
    {
        var a = HttpSource(85_289_000);
        var b = HttpSource(5_000_000);
        var local = HttpSource();
        local.Protocol = MediaProtocol.File;

        Assert.Equal(2, FastStartTuning.Apply(FederatedMovie(), new[] { a, b, local }));
        Assert.Equal(FastStartTuning.MinAnalyzeDurationMs, a.AnalyzeDurationMs);
        Assert.Equal(FastStartTuning.MaxAnalyzeDurationMs, b.AnalyzeDurationMs);
        Assert.Null(local.AnalyzeDurationMs);
    }

    [Fact]
    public void Apply_DisabledInConfiguration_ChangesNothing()
    {
        _plugin.Configuration.FastStartProbing = false;
        var source = HttpSource();

        Assert.Equal(0, FastStartTuning.Apply(FederatedMovie(), new[] { source }));
        Assert.Null(source.AnalyzeDurationMs);
    }

    [Fact]
    public void Apply_NullInputs_AreHarmless()
    {
        Assert.Equal(0, FastStartTuning.Apply(FederatedMovie(), null));
        Assert.Equal(0, FastStartTuning.Apply(null, new[] { HttpSource() }));
        Assert.Equal(0, FastStartTuning.Apply(FederatedMovie(), new MediaSourceInfo[] { null! }));
    }

    [Fact]
    public void FastStartProbing_DefaultsOn()
        => Assert.True(new PluginConfiguration().FastStartProbing);

    // ---- the IMediaSourceManager wrapper ----

    [Fact]
    public async Task Wrapper_GetPlaybackMediaSources_TunesFederatedSources()
    {
        var item = FederatedMovie();
        var source = HttpSource();
        var inner = new Mock<IMediaSourceManager>();
        inner.Setup(m => m.GetPlaybackMediaSources(item, null!, true, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MediaSourceInfo> { source });

        var result = await new FederationMediaSourceManager(inner.Object)
            .GetPlaybackMediaSources(item, null!, true, false, CancellationToken.None);

        Assert.Same(source, Assert.Single(result));
        Assert.Equal(FastStartTuning.MinAnalyzeDurationMs, source.AnalyzeDurationMs);
    }

    [Fact]
    public void Wrapper_GetStaticMediaSources_TunesFederatedSources()
    {
        var item = FederatedMovie();
        var source = HttpSource();
        var inner = new Mock<IMediaSourceManager>();
        inner.Setup(m => m.GetStaticMediaSources(item, false, null!)).Returns(new List<MediaSourceInfo> { source });

        new FederationMediaSourceManager(inner.Object).GetStaticMediaSources(item, false);

        Assert.Equal(FastStartTuning.MinAnalyzeDurationMs, source.AnalyzeDurationMs);
    }

    [Fact]
    public async Task Wrapper_GetMediaSource_TunesTheSourceFfmpegWillRead()
    {
        var item = FederatedMovie();
        var source = HttpSource();
        var inner = new Mock<IMediaSourceManager>();
        inner.Setup(m => m.GetMediaSource(item, source.Id, null!, false, It.IsAny<CancellationToken>())).ReturnsAsync(source);

        var result = await new FederationMediaSourceManager(inner.Object)
            .GetMediaSource(item, source.Id, null!, false, CancellationToken.None);

        Assert.Same(source, result);
        Assert.Equal(FastStartTuning.MinAnalyzeDurationMs, source.AnalyzeDurationMs);
    }

    [Fact]
    public async Task Wrapper_GetMediaSource_UnknownId_ReturnsNull()
    {
        var inner = new Mock<IMediaSourceManager>();
        inner.Setup(m => m.GetMediaSource(It.IsAny<BaseItem>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MediaSourceInfo)null!);

        Assert.Null(await new FederationMediaSourceManager(inner.Object)
            .GetMediaSource(FederatedMovie(), "missing", null!, false, CancellationToken.None));
    }

    [Fact]
    public async Task Wrapper_NonFederatedItem_PassesThroughUnchanged()
    {
        var item = LocalMovie();
        var source = HttpSource();
        var inner = new Mock<IMediaSourceManager>();
        inner.Setup(m => m.GetPlaybackMediaSources(item, null!, false, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MediaSourceInfo> { source });

        await new FederationMediaSourceManager(inner.Object)
            .GetPlaybackMediaSources(item, null!, false, false, CancellationToken.None);

        Assert.Null(source.AnalyzeDurationMs);
    }

    [Fact]
    public async Task Wrapper_InnerFailureStillPropagates()
    {
        var inner = new Mock<IMediaSourceManager>();
        inner.Setup(m => m.GetPlaybackMediaSources(It.IsAny<BaseItem>(), It.IsAny<Jellyfin.Database.Implementations.Entities.User>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => new FederationMediaSourceManager(inner.Object)
            .GetPlaybackMediaSources(FederatedMovie(), null!, false, false, CancellationToken.None));
    }

    [Fact]
    public void Wrapper_ForwardsTheRestOfTheInterface()
    {
        var inner = new Mock<IMediaSourceManager>();
        var wrapper = new FederationMediaSourceManager(inner.Object);

        wrapper.AddParts(Array.Empty<IMediaSourceProvider>());
        wrapper.GetMediaStreams(Guid.Empty);
        wrapper.GetMediaAttachments(Guid.Empty);
        wrapper.SupportsDirectStream("p", MediaProtocol.Http);
        wrapper.GetPathProtocol("p");
        wrapper.GetLiveStreamInfo("id");
        wrapper.CloseLiveStream("id");

        inner.Verify(m => m.AddParts(It.IsAny<IEnumerable<IMediaSourceProvider>>()), Times.Once);
        inner.Verify(m => m.GetMediaStreams(Guid.Empty), Times.Once);
        inner.Verify(m => m.GetMediaAttachments(Guid.Empty), Times.Once);
        inner.Verify(m => m.SupportsDirectStream("p", MediaProtocol.Http), Times.Once);
        inner.Verify(m => m.GetPathProtocol("p"), Times.Once);
        inner.Verify(m => m.GetLiveStreamInfo("id"), Times.Once);
        inner.Verify(m => m.CloseLiveStream("id"), Times.Once);
    }

    // ---- registration ----

    private interface IFakeDependency { }

    private sealed class FakeDependency : IFakeDependency { }

    [Fact]
    public void Decorate_ReplacesPlainSingletonRegistration_AndWrapsTheOriginalType()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IFakeDependency, FakeDependency>();
        services.AddSingleton<IMediaSourceManager, StubMediaSourceManager>();

        Assert.True(PluginServiceRegistrator.DecorateMediaSourceManager(services));

        using var provider = services.BuildServiceProvider();
        var resolved = provider.GetRequiredService<IMediaSourceManager>();
        Assert.IsType<FederationMediaSourceManager>(resolved);
        Assert.Same(resolved, provider.GetRequiredService<IMediaSourceManager>());
        Assert.Single(services, d => d.ServiceType == typeof(IMediaSourceManager));
    }

    [Fact]
    public void Decorate_ConstructsTheOriginalWithItsOwnDependencies()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IFakeDependency, FakeDependency>();
        services.AddSingleton<IMediaSourceManager, StubMediaSourceManagerWithDependency>();
        PluginServiceRegistrator.DecorateMediaSourceManager(services);

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IMediaSourceManager>().GetPathProtocol("p");

        Assert.True(StubMediaSourceManagerWithDependency.ConstructedWithDependency);
    }

    [Fact]
    public void Decorate_WhenNothingRegistered_DoesNothing()
        => Assert.False(PluginServiceRegistrator.DecorateMediaSourceManager(new ServiceCollection()));

    [Fact]
    public void Decorate_FactoryRegistration_IsLeftAlone()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IMediaSourceManager>(_ => new StubMediaSourceManager());

        Assert.False(PluginServiceRegistrator.DecorateMediaSourceManager(services));

        using var provider = services.BuildServiceProvider();
        Assert.IsType<StubMediaSourceManager>(provider.GetRequiredService<IMediaSourceManager>());
    }

    [Fact]
    public void Decorate_NonSingleton_IsLeftAlone()
    {
        var services = new ServiceCollection();
        services.AddScoped<IMediaSourceManager, StubMediaSourceManager>();
        Assert.False(PluginServiceRegistrator.DecorateMediaSourceManager(services));
    }

    [Fact]
    public void Decorate_RunTwice_DoesNotDoubleWrap()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IMediaSourceManager, StubMediaSourceManager>();

        Assert.True(PluginServiceRegistrator.DecorateMediaSourceManager(services));
        Assert.False(PluginServiceRegistrator.DecorateMediaSourceManager(services));
    }

    private class StubMediaSourceManager : IMediaSourceManager
    {
        public virtual void AddParts(IEnumerable<IMediaSourceProvider> providers) { }
        public IReadOnlyList<MediaStream> GetMediaStreams(Guid itemId) => Array.Empty<MediaStream>();
        public IReadOnlyList<MediaStream> GetMediaStreams(MediaStreamQuery query) => Array.Empty<MediaStream>();
        public IReadOnlyList<MediaAttachment> GetMediaAttachments(Guid itemId) => Array.Empty<MediaAttachment>();
        public IReadOnlyList<MediaAttachment> GetMediaAttachments(MediaAttachmentQuery query) => Array.Empty<MediaAttachment>();
        public Task<IReadOnlyList<MediaSourceInfo>> GetPlaybackMediaSources(BaseItem item, Jellyfin.Database.Implementations.Entities.User user, bool allowMediaProbe, bool enablePathSubstitution, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<MediaSourceInfo>>(Array.Empty<MediaSourceInfo>());
        public IReadOnlyList<MediaSourceInfo> GetStaticMediaSources(BaseItem item, bool enablePathSubstitution, Jellyfin.Database.Implementations.Entities.User user = null!) => Array.Empty<MediaSourceInfo>();
        public Task<MediaSourceInfo> GetMediaSource(BaseItem item, string mediaSourceId, string liveStreamId, bool enablePathSubstitution, CancellationToken cancellationToken) => Task.FromResult<MediaSourceInfo>(null!);
        public Task<LiveStreamResponse> OpenLiveStream(LiveStreamRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Tuple<LiveStreamResponse, IDirectStreamProvider>> OpenLiveStreamInternal(LiveStreamRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<MediaSourceInfo> GetLiveStream(string id, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Tuple<MediaSourceInfo, IDirectStreamProvider>> GetLiveStreamWithDirectStreamProvider(string id, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ILiveStream GetLiveStreamInfo(string id) => throw new NotSupportedException();
        public ILiveStream GetLiveStreamInfoByUniqueId(string uniqueId) => throw new NotSupportedException();
        public Task<IReadOnlyList<MediaSourceInfo>> GetRecordingStreamMediaSources(MediaBrowser.Controller.LiveTv.ActiveRecordingInfo info, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task CloseLiveStream(string id) => Task.CompletedTask;
        public Task<MediaSourceInfo> GetLiveStreamMediaInfo(string id, CancellationToken cancellationToken) => throw new NotSupportedException();
        public bool SupportsDirectStream(string path, MediaProtocol protocol) => false;
        public MediaProtocol GetPathProtocol(string path) => MediaProtocol.Http;
        public void SetDefaultAudioAndSubtitleStreamIndices(BaseItem item, MediaSourceInfo source, Jellyfin.Database.Implementations.Entities.User user) { }
        public Task AddMediaInfoWithProbe(MediaSourceInfo mediaSource, bool isAudio, string cacheKey, bool addProbeDelay, bool isLiveStream, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class StubMediaSourceManagerWithDependency : StubMediaSourceManager
    {
        public static bool ConstructedWithDependency;

        public StubMediaSourceManagerWithDependency(IFakeDependency dependency)
        {
            ConstructedWithDependency = dependency is FakeDependency;
        }
    }
}
