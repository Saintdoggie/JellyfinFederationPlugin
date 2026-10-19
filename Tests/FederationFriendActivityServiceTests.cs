using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Federation.Configuration;
using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Activity;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

public sealed class FederationFriendActivityServiceTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 10, 19, 20, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private readonly Clock _clock = new();
    private readonly Mock<IActivityManager> _activity = new();
    private readonly Mock<ILibraryManager> _library = new();
    private readonly List<ActivityLog> _logged = new();
    private readonly RemoteServer _friend = new() { Id = "f1", Name = "Bob's server", FederationId = "fed-bob" };
    private readonly Guid _movieId = Guid.NewGuid();

    public FederationFriendActivityServiceTests()
    {
        _activity.Setup(a => a.CreateAsync(It.IsAny<ActivityLog>()))
            .Callback<ActivityLog>(_logged.Add)
            .Returns(Task.CompletedTask);
        _library.Setup(l => l.GetItemById(_movieId)).Returns(new Movie { Id = _movieId, Name = "Heat", ProductionYear = 1995 });
    }

    private FederationFriendActivityService Create(string? storePath = null)
        => new(_activity.Object, _library.Object, null, NullLogger<FederationFriendActivityService>.Instance, _clock, startTimer: false, storePath: storePath);

    private void Advance(TimeSpan by) => _clock.Now += by;

    [Fact]
    public void ShortProbe_IsNeverReportedOrLogged()
    {
        var service = Create();
        using (service.Begin(_friend, _movieId, "u1", download: false, transcoded: false))
        {
            Advance(TimeSpan.FromSeconds(3));
        }

        service.Sweep();
        Advance(TimeSpan.FromMinutes(5));
        service.Sweep();

        Assert.Empty(service.GetActive());
        Assert.Empty(service.GetRecent());
        Assert.Empty(_logged);
    }

    [Fact]
    public void LongStream_IsReportedByServerAndLoggedOnStartAndStop()
    {
        var service = Create();
        var lease = service.Begin(_friend, _movieId, "u1", download: false, transcoded: true);
        Advance(TimeSpan.FromSeconds(30));
        service.Sweep();

        var active = Assert.Single(service.GetActive());
        Assert.Equal("Bob's server", active.FriendName);
        Assert.Equal("Heat (1995)", active.ItemName);
        Assert.True(active.IsStreaming);
        Assert.True(active.Transcoded);
        var start = Assert.Single(_logged);
        Assert.Equal("Bob's server started watching Heat (1995)", start.Name);
        Assert.Equal(Guid.Empty, start.UserId);
        Assert.Equal(_movieId.ToString("N"), start.ItemId);

        Advance(TimeSpan.FromMinutes(40));
        lease.Dispose();
        service.Sweep();
        Assert.Single(service.GetActive());
        Assert.False(service.GetActive()[0].IsStreaming);

        Advance(FederationFriendActivityService.IdleTimeout);
        service.Sweep();
        Assert.Empty(service.GetActive());
        var ended = Assert.Single(service.GetRecent());
        Assert.NotNull(ended.Ended);
        Assert.Equal(2, _logged.Count);
        Assert.Equal("Bob's server stopped watching Heat (1995)", _logged[1].Name);
        Assert.Equal("After 40 min", _logged[1].ShortOverview);
    }

    [Fact]
    public void RepeatedRangeRequests_WithShortGaps_StayOneSession()
    {
        var service = Create();
        for (var i = 0; i < 6; i++)
        {
            using (service.Begin(_friend, _movieId, "u1", download: false, transcoded: false))
            {
                Advance(TimeSpan.FromSeconds(10));
            }

            Advance(TimeSpan.FromSeconds(30));
            service.Sweep();
        }

        Assert.Single(service.GetActive());
        Assert.Single(_logged);
    }

    [Fact]
    public void TwoViewersOfOneFriend_AreSeparateSessionsWithoutNames()
    {
        var service = Create();
        using var a = service.Begin(_friend, _movieId, "u1", download: false, transcoded: false);
        using var b = service.Begin(_friend, _movieId, "u2", download: false, transcoded: false);
        Advance(TimeSpan.FromSeconds(25));
        service.Sweep();

        var active = service.GetActive();
        Assert.Equal(2, active.Count);
        Assert.All(active, e => Assert.Equal("Bob's server", e.FriendName));
    }

    [Fact]
    public void Download_IsLoggedAsDownload_AndEpisodesNameTheirShow()
    {
        var episodeId = Guid.NewGuid();
        _library.Setup(l => l.GetItemById(episodeId)).Returns(new Episode { Id = episodeId, Name = "Pilot", SeriesName = "Lost", ParentIndexNumber = 1, IndexNumber = 1 });
        var service = Create();
        using var lease = service.Begin(_friend, episodeId, null, download: true, transcoded: false);
        Advance(TimeSpan.FromSeconds(21));
        service.Sweep();

        Assert.True(Assert.Single(service.GetActive()).IsDownload);
        Assert.Equal("Bob's server started downloading Lost S01E01 - Pilot", Assert.Single(_logged).Name);
    }

    [Fact]
    public void ActivityLogFailure_DoesNotBreakTracking()
    {
        _activity.Setup(a => a.CreateAsync(It.IsAny<ActivityLog>())).Throws(new InvalidOperationException("db down"));
        var service = Create();
        using var lease = service.Begin(_friend, _movieId, "u1", download: false, transcoded: false);
        Advance(TimeSpan.FromSeconds(25));

        service.Sweep();

        Assert.Single(service.GetActive());
    }

    [Fact]
    public void StreamLimit_RefusesANewTitleButNotTheOneAlreadyPlaying()
    {
        var service = Create();
        _friend.MaxConcurrentStreams = 1;
        var other = Guid.NewGuid();
        using var playing = service.Begin(_friend, _movieId, "u1", download: false, transcoded: false);

        Assert.Null(service.CheckLimits(_friend, _movieId, "u1", download: false));
        Assert.NotNull(service.CheckLimits(_friend, other, "u2", download: false));
        Assert.Null(service.CheckLimits(_friend, other, "u2", download: true));
        Assert.Null(service.CheckLimits(new RemoteServer { Id = "f2", MaxConcurrentStreams = 1 }, other, "u2", download: false));
    }

    [Fact]
    public void StreamLimit_FreesTheSlotShortlyAfterAStreamStops()
    {
        var service = Create();
        _friend.MaxConcurrentStreams = 1;
        service.Begin(_friend, _movieId, "u1", download: false, transcoded: false).Dispose();

        Assert.NotNull(service.CheckLimits(_friend, Guid.NewGuid(), "u2", download: false));
        Advance(FederationFriendActivityService.LiveWindow);
        Assert.Null(service.CheckLimits(_friend, Guid.NewGuid(), "u2", download: false));
    }

    [Fact]
    public void DataCap_CountsRelayedBytesForTheCurrentMonthOnly()
    {
        var service = Create();
        _friend.MonthlyDataCapGb = 1;
        using (var lease = service.Begin(_friend, _movieId, "u1", download: false, transcoded: false))
        {
            lease.AddBytes(600_000_000);
            Assert.Null(service.CheckLimits(_friend, _movieId, "u1", download: false));
            lease.AddBytes(400_000_000);
        }

        Assert.NotNull(service.CheckLimits(_friend, _movieId, "u1", download: true));
        service.Sweep();
        Assert.NotNull(service.CheckLimits(_friend, Guid.NewGuid(), "u1", download: false));

        Advance(TimeSpan.FromDays(31));
        service.Sweep();
        Assert.Null(service.CheckLimits(_friend, Guid.NewGuid(), "u1", download: false));
    }

    [Fact]
    public void History_SurvivesARestart()
    {
        var path = Path.Combine(Path.GetTempPath(), "federation-usage-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var service = Create(path);
            using (var lease = service.Begin(_friend, _movieId, "u1", download: false, transcoded: false))
            {
                lease.AddBytes(1234);
                Advance(TimeSpan.FromMinutes(30));
            }

            Advance(FederationFriendActivityService.IdleTimeout);
            service.Sweep();
            service.Dispose();

            var reloaded = Create(path);
            var record = Assert.Single(reloaded.GetRecent());
            Assert.Equal("Heat (1995)", record.ItemName);
            Assert.Equal(1234, record.Bytes);
            var usage = Assert.Single(reloaded.GetUsageSummary().Friends);
            Assert.Equal(1234, usage.BytesThisMonth);
            Assert.Equal(0.5, usage.HoursThisMonth);
            Assert.Equal("Heat (1995)", Assert.Single(usage.TopTitles).Name);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void PopularTitles_CountFriendSessionsAndLocalPlaysPerShow()
    {
        var seriesId = Guid.NewGuid();
        var episodeId = Guid.NewGuid();
        var episode = new Episode { Id = episodeId, Name = "Pilot", SeriesName = "Lost", SeriesId = seriesId };
        _library.Setup(l => l.GetItemById(episodeId)).Returns(episode);
        var service = Create();

        using (service.Begin(_friend, episodeId, "u1", download: false, transcoded: false))
        {
            Advance(TimeSpan.FromMinutes(10));
        }

        Advance(FederationFriendActivityService.IdleTimeout);
        service.Sweep();
        service.RecordLocalPlay(new Episode { Id = Guid.NewGuid(), SeriesId = seriesId }, null, playedToCompletion: true, userId: Guid.NewGuid());
        service.RecordLocalPlay(new Movie { Id = _movieId }, TimeSpan.FromMinutes(10).Ticks, playedToCompletion: false);
        service.RecordLocalPlay(new Movie { Id = Guid.NewGuid() }, TimeSpan.FromMinutes(1).Ticks, playedToCompletion: false);
        var federated = new Movie { Id = Guid.NewGuid() };
        federated.ProviderIds["FederationKey"] = "k";
        service.RecordLocalPlay(federated, null, playedToCompletion: true);

        var popular = service.GetPopularTitles();
        Assert.Equal((seriesId, 2), popular[0]);
        Assert.Equal((_movieId, 1), popular[1]);
        Assert.Equal(2, popular.Count);
    }

    [Fact]
    public void Shutdown_KeepsTheDataAndSessionsOfStreamsStillRunning()
    {
        var path = Path.Combine(Path.GetTempPath(), "federation-usage-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var service = Create(path);
            var lease = service.Begin(_friend, _movieId, "u1", download: false, transcoded: false);
            lease.AddBytes(5000);
            Advance(TimeSpan.FromSeconds(30));
            service.Sweep();
            lease.AddBytes(7000);
            service.Dispose();

            var reloaded = Create(path);
            Assert.Equal(12000, Assert.Single(reloaded.GetUsageSummary().Friends).BytesThisMonth);
            Assert.Equal("Heat (1995)", Assert.Single(reloaded.GetRecent()).ItemName);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void PopularTitles_CountAnAlbumOrBingeOncePerViewerADay()
    {
        var seriesId = Guid.NewGuid();
        var anna = Guid.NewGuid();
        var ben = Guid.NewGuid();
        var service = Create();

        for (var i = 0; i < 10; i++)
        {
            service.RecordLocalPlay(new Episode { Id = Guid.NewGuid(), SeriesId = seriesId }, null, playedToCompletion: true, userId: anna);
        }

        service.RecordLocalPlay(new Episode { Id = Guid.NewGuid(), SeriesId = seriesId }, null, playedToCompletion: true, userId: ben);
        service.RecordLocalPlay(new Movie { Id = _movieId }, null, playedToCompletion: true, userId: anna);
        service.RecordLocalPlay(new Movie { Id = _movieId }, null, playedToCompletion: true, userId: ben);
        Assert.Equal(new[] { (seriesId, 2), (_movieId, 2) }.OrderBy(t => t.Item1), service.GetPopularTitles().OrderBy(t => t.TitleId));

        Advance(TimeSpan.FromDays(1));
        service.RecordLocalPlay(new Episode { Id = Guid.NewGuid(), SeriesId = seriesId }, null, playedToCompletion: true, userId: anna);
        Assert.Equal(3, service.GetPopularTitles().Single(t => t.TitleId == seriesId).Plays);
    }

    [Fact]
    public void StreamLimit_AViewerMovingToTheNextEpisodeIsNotRefused()
    {
        var service = Create();
        _friend.MaxConcurrentStreams = 1;
        service.Begin(_friend, _movieId, "u1", download: false, transcoded: false).Dispose();
        Advance(TimeSpan.FromSeconds(5));

        Assert.Null(service.CheckLimits(_friend, Guid.NewGuid(), "u1", download: false));
        Assert.NotNull(service.CheckLimits(_friend, Guid.NewGuid(), "u2", download: false));
    }

    [Fact]
    public void TryBegin_RegistersAtomically_SoASecondTitleIsRefused()
    {
        var service = Create();
        _friend.MaxConcurrentStreams = 1;

        Assert.Null(service.TryBegin(_friend, _movieId, "u1", download: false, transcoded: false, out var first));
        Assert.NotNull(service.TryBegin(_friend, Guid.NewGuid(), "u2", download: false, transcoded: false, out var second));
        Assert.NotNull(first);
        Assert.Null(second);
        first!.Dispose();
    }

    [Fact]
    public void DataLimit_TellsTheRunningRequestToStopOnceItUsesUpTheRest()
    {
        var service = Create();
        _friend.MonthlyDataCapGb = 1;
        using (var earlier = service.Begin(_friend, Guid.NewGuid(), "u1", download: true, transcoded: false))
        {
            earlier.AddBytes(900_000_000);
        }

        Assert.Null(service.TryBegin(_friend, _movieId, "u1", download: false, transcoded: false, out var lease));
        Assert.True(lease!.AddBytes(60_000_000));
        Assert.True(lease.AddBytes(40_000_000));
        Assert.False(lease.AddBytes(1));
        lease.Dispose();
    }
}
