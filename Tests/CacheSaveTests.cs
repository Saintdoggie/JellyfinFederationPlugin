using System;
using System.IO;
using Jellyfin.Plugin.Federation.Services;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

/// <summary>An unchanged cache is not rewritten after every sync; a changed one always is.</summary>
public sealed class CacheSaveTests
{
    [Fact]
    public void UnchangedCache_IsNotRewritten_ButChangesAndRestartsAreHandled()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fed-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "federation-cache.json");
            var cache = new FederationItemCache(NullLogger<FederationItemCache>.Instance);
            cache.Initialize(path);
            var id = Guid.NewGuid();
            cache.UpsertRaw("Movies", "s", id, new BaseItemDto { Id = id, Name = "One" }, 0, "Movie");
            cache.SaveAsync().GetAwaiter().GetResult();
            var written = File.GetLastWriteTimeUtc(path);

            File.SetLastWriteTimeUtc(path, written.AddMinutes(-5));
            cache.SaveAsync().GetAwaiter().GetResult();
            Assert.Equal(written.AddMinutes(-5), File.GetLastWriteTimeUtc(path));

            var id2 = Guid.NewGuid();
            cache.UpsertRaw("Movies", "s", id2, new BaseItemDto { Id = id2, Name = "Two" }, 0, "Movie");
            cache.SaveAsync().GetAwaiter().GetResult();
            Assert.NotEqual(written.AddMinutes(-5), File.GetLastWriteTimeUtc(path));

            // After a restart the file just read counts as already saved.
            var restarted = new FederationItemCache(NullLogger<FederationItemCache>.Instance);
            restarted.Initialize(path);
            Assert.Equal(2, restarted.Count);
            File.SetLastWriteTimeUtc(path, written.AddMinutes(-10));
            restarted.SaveAsync().GetAwaiter().GetResult();
            Assert.Equal(written.AddMinutes(-10), File.GetLastWriteTimeUtc(path));

            // A deleted file is always written again.
            File.Delete(path);
            restarted.SaveAsync().GetAwaiter().GetResult();
            Assert.True(File.Exists(path));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}

public sealed class CacheLoadFailureTests
{
    [Fact]
    public void CorruptCache_IsMovedAside_AndBlocksReconcileUntilRepopulated()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fed-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "federation-cache.json");
            File.WriteAllText(path, "{ not json");
            var cache = new FederationItemCache(NullLogger<FederationItemCache>.Instance);
            cache.Initialize(path);

            Assert.True(cache.DestructiveReconcileBlocked);
            Assert.False(File.Exists(path));
            Assert.Single(Directory.GetFiles(dir, "federation-cache.json.corrupt-*"));

            var id = Guid.NewGuid();
            cache.UpsertRaw("Movies", "s", id, new BaseItemDto { Id = id, Name = "One" }, 0, "Movie");
            Assert.False(cache.DestructiveReconcileBlocked);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void MissingCache_DoesNotBlockReconcile()
    {
        var cache = new FederationItemCache(NullLogger<FederationItemCache>.Instance);
        cache.Initialize(Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid().ToString("N") + ".json"));
        Assert.False(cache.DestructiveReconcileBlocked);
    }
}
