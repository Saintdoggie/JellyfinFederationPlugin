using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Services;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

/// <summary>Owner-side per-friend limits: monthly data cap and streams at once.</summary>
public class FriendDataLimitsTests
{
    private static FriendDataLimits Make(Func<string?>? path = null, DateTime? now = null)
    {
        var clock = now ?? new DateTime(2026, 3, 15, 12, 0, 0, DateTimeKind.Utc);
        return new FriendDataLimits(null, path ?? (() => null)) { UtcNow = () => clock };
    }

    [Fact]
    public void CapBytes_ZeroMeansNoLimit()
    {
        Assert.Equal(0, FriendDataLimits.CapBytes(0));
        Assert.Equal(5_000_000_000L, FriendDataLimits.CapBytes(5));
    }

    [Fact]
    public void Reserve_GrantsOnlyWhatFitsUnderTheCap()
    {
        var l = Make();
        Assert.Equal(60, l.Reserve("f", 60, 100));
        Assert.Equal(40, l.Reserve("f", 60, 100));
        Assert.Equal(0, l.Reserve("f", 1, 100));
        Assert.Equal(100, l.UsedThisMonth("f"));
        Assert.True(l.IsOverCap("f", 100));
    }

    [Fact]
    public void Reserve_NoCapCountsEverythingAndIsNeverOver()
    {
        var l = Make();
        Assert.Equal(1_000_000, l.Reserve("f", 1_000_000, 0));
        Assert.False(l.IsOverCap("f", 0));
        Assert.Equal(1_000_000, l.UsedThisMonth("f"));
    }

    [Fact]
    public void Reserve_ConcurrentWritersCannotOvershoot()
    {
        var l = Make();
        long total = 0;
        Parallel.For(0, 64, _ =>
        {
            for (var i = 0; i < 1000; i++)
            {
                Interlocked.Add(ref total, l.Reserve("f", 1000, 1_000_000));
            }
        });

        Assert.Equal(1_000_000, total);
        Assert.Equal(1_000_000, l.UsedThisMonth("f"));
    }

    [Fact]
    public async Task Meter_StopsMidStreamAtTheCapWithoutOvershoot()
    {
        var l = Make();
        l.Reserve("f", 70, 100);
        var sink = new MemoryStream();
        using var cts = new CancellationTokenSource();
        var metered = l.Meter(sink, "f", 100, cts);

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await metered.WriteAsync(new byte[50]));

        Assert.Equal(30, sink.Length);
        Assert.True(cts.IsCancellationRequested);
        Assert.Equal(100, l.UsedThisMonth("f"));
    }

    [Fact]
    public async Task Meter_TwoStreamsShareTheRemainder()
    {
        var l = Make();
        using var c1 = new CancellationTokenSource();
        using var c2 = new CancellationTokenSource();
        var a = new MemoryStream();
        var b = new MemoryStream();
        var m1 = l.Meter(a, "f", 100, c1);
        var m2 = l.Meter(b, "f", 100, c2);

        await m1.WriteAsync(new byte[80]);
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await m2.WriteAsync(new byte[80]));

        Assert.Equal(100, a.Length + b.Length);
    }

    [Fact]
    public void Monthly_TotalsRollOverOnTheUtcMonth()
    {
        var clock = new DateTime(2026, 3, 31, 23, 59, 0, DateTimeKind.Utc);
        var l = new FriendDataLimits(null, () => null) { UtcNow = () => clock };
        l.Reserve("f", 100, 100);
        Assert.True(l.IsOverCap("f", 100));

        clock = new DateTime(2026, 4, 1, 0, 0, 1, DateTimeKind.Utc);
        Assert.Equal(0, l.UsedThisMonth("f"));
        Assert.False(l.IsOverCap("f", 100));
        Assert.Equal(100, l.Reserve("f", 100, 100));
    }

    [Fact]
    public void Persistence_SurvivesARestartAndKeepsThirteenMonths()
    {
        var path = Path.Combine(Path.GetTempPath(), "fed-usage-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var clock = new DateTime(2025, 1, 10, 0, 0, 0, DateTimeKind.Utc);
            var l = new FriendDataLimits(null, () => path) { UtcNow = () => clock };
            for (var i = 0; i < 16; i++)
            {
                clock = new DateTime(2025, 1, 10, 0, 0, 0, DateTimeKind.Utc).AddMonths(i);
                l.Reserve("f", 10, 0);
            }

            l.Flush();
            var again = new FriendDataLimits(null, () => path) { UtcNow = () => clock };
            Assert.Equal(10, again.UsedThisMonth("f"));
            var months = System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, long>>>(File.ReadAllText(path))!;
            Assert.Equal(13, months["f"].Count);
            Assert.DoesNotContain(Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".*.tmp"), _ => true);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Persistence_ConcurrentSavesDoNotCollide()
    {
        var path = Path.Combine(Path.GetTempPath(), "fed-usage-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var l = new FriendDataLimits(null, () => path);
            Parallel.For(0, 200, i =>
            {
                l.Reserve("f" + (i % 4), 5, 0);
                l.Flush();
            });
            l.Flush();
            var again = new FriendDataLimits(null, () => path);
            Assert.Equal(1000, Enumerable.Range(0, 4).Sum(i => again.UsedThisMonth("f" + i)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Streams_LimitIsEnforcedAtomically()
    {
        var l = Make();
        var leases = new System.Collections.Concurrent.ConcurrentBag<FriendDataLimits.StreamLease>();
        Parallel.For(0, 50, i =>
        {
            var lease = l.TryOpenStream("f", "p:" + i, 3);
            if (lease != null)
            {
                leases.Add(lease);
            }
        });

        Assert.Equal(3, leases.Count);
        Assert.Equal(3, l.ActiveStreams("f"));
    }

    [Fact]
    public void Streams_RequestsOfOnePlayCountOnce_AndLimitIsPerFriend()
    {
        var l = Make();
        using var a1 = l.TryOpenStream("f", "p:session-1", 1);
        using var a2 = l.TryOpenStream("f", "P:SESSION-1", 1);
        Assert.NotNull(a1);
        Assert.NotNull(a2);
        Assert.False(a2!.Created);
        Assert.Equal(1, l.ActiveStreams("f"));
        Assert.Null(l.TryOpenStream("f", "d:item:user", 1));
        Assert.NotNull(l.TryOpenStream("other", "p:session-9", 1));
    }

    [Fact]
    public void Streams_DirectAndHandoffShareOneLimit_AndMidPlayRequestsAreNotRefused()
    {
        var l = Make();
        using var direct = l.TryOpenStream("f", "d:item:user", 2);
        using var handoff = l.TryOpenStream("f", "p:s1", 2);
        Assert.Null(l.TryOpenStream("f", "p:s2", 2, enforce: true));

        // A request with an unknown session that is not a play start still registers.
        using var late = l.TryOpenStream("f", "p:s3", 2, enforce: false);
        Assert.NotNull(late);
        Assert.Equal(3, l.ActiveStreams("f"));
    }

    [Fact]
    public void Streams_ForgetAndIdleExpiryFreeASlot()
    {
        var clock = new DateTime(2026, 3, 15, 12, 0, 0, DateTimeKind.Utc);
        var l = new FriendDataLimits(null, () => null) { UtcNow = () => clock };
        l.TryOpenStream("f", "p:s1", 1)!.Dispose();
        Assert.Null(l.TryOpenStream("f", "p:s2", 1));

        l.ForgetStream("f", "p:s1");
        using (var s2 = l.TryOpenStream("f", "p:s2", 1))
        {
            Assert.NotNull(s2);
        }

        clock = clock.AddMinutes(3);
        Assert.Equal(0, l.ActiveStreams("f"));
        Assert.NotNull(l.TryOpenStream("f", "p:s3", 1));
    }

    [Fact]
    public void Streams_InFlightRequestKeepsAStreamActiveWhileIdle()
    {
        var clock = new DateTime(2026, 3, 15, 12, 0, 0, DateTimeKind.Utc);
        var l = new FriendDataLimits(null, () => null) { UtcNow = () => clock };
        using var lease = l.TryOpenStream("f", "d:item:user", 1);
        clock = clock.AddHours(2);
        Assert.Equal(1, l.ActiveStreams("f"));
    }

    [Fact]
    public async Task Refusal_Is429WithMachineReadableReason()
    {
        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();
        await FriendDataLimits.WriteRefusalAsync(ctx.Response, FriendDataLimits.DataLimitReason, new DateTime(2026, 3, 15, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(429, ctx.Response.StatusCode);
        Assert.Equal("data-limit", ctx.Response.Headers[FriendDataLimits.RefusalHeader]);
        Assert.False(string.IsNullOrEmpty(ctx.Response.Headers.RetryAfter));
        ctx.Response.Body.Position = 0;
        Assert.Contains("\"reason\":\"data-limit\"", new StreamReader(ctx.Response.Body).ReadToEnd());
    }

    [Fact]
    public void MainSave_PreservesTheLimitFieldsAndConfigExposesThem()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "Configuration", "FederationPluginController.cs")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        Assert.NotNull(dir);
        var src = File.ReadAllText(Path.Combine(dir!, "Configuration", "FederationPluginController.cs"));
        var presentation = File.ReadAllText(Path.Combine(dir!, "Services", "FederationServerPresentation.cs"));
        Assert.Contains("server.MonthlyDataCapGb = oldServer.MonthlyDataCapGb;", src);
        Assert.Contains("server.MaxConcurrentStreams = oldServer.MaxConcurrentStreams;", src);
        Assert.Contains("server.MonthlyDataCapGb,", presentation);
        Assert.Contains("server.MaxConcurrentStreams,", presentation);
    }
}
