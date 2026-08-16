using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.Federation.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

/// <summary>
/// Covers <see cref="FederationDirectoryStore"/>: registering/searching entries
/// and creating/resolving invite codes, including expiry. Persistence uses the
/// same temp-file-then-move pattern as <see cref="FederationItemCache"/>, so each
/// test points at its own temp file to stay isolated.
/// </summary>
public class FederationDirectoryStoreTests : IDisposable
{
    private readonly string _storePath;
    private readonly FederationDirectoryStore _store;

    public FederationDirectoryStoreTests()
    {
        _storePath = Path.Combine(Path.GetTempPath(), $"federation-directory-test-{Guid.NewGuid():N}.json");
        _store = new FederationDirectoryStore(NullLogger<FederationDirectoryStore>.Instance);
        _store.Initialize(_storePath);
    }

    public void Dispose()
    {
        if (File.Exists(_storePath))
        {
            File.Delete(_storePath);
        }

        var temp = _storePath + ".tmp";
        if (File.Exists(temp))
        {
            File.Delete(temp);
        }
    }

    [Fact]
    public void Register_ThenSearch_FindsByUsernameSubstring_CaseInsensitive()
    {
        _store.Register("movie_night_mike", "fed-1", "https://mike.example");
        _store.Register("book_club_bella", "fed-2", "https://bella.example");

        var results = _store.Search("NIGHT");

        var hit = Assert.Single(results);
        Assert.Equal("movie_night_mike", hit.Username);
        Assert.Equal("fed-1", hit.FederationId);
        Assert.Equal("https://mike.example", hit.ServerUrl);
    }

    [Fact]
    public void Register_SameFederationIdTwice_UpdatesRatherThanDuplicates()
    {
        _store.Register("old_name", "fed-1", "https://old.example");
        _store.Register("new_name", "fed-1", "https://new.example");

        var results = _store.Search("name");

        var hit = Assert.Single(results);
        Assert.Equal("new_name", hit.Username);
        Assert.Equal("https://new.example", hit.ServerUrl);
    }

    [Fact]
    public void CreateInvite_ThenResolve_ReturnsTheServerItWasCreatedFor()
    {
        var code = _store.CreateInvite("https://inviter.example", "fed-inviter");

        var resolved = _store.ResolveInvite(code);

        Assert.NotNull(resolved);
        Assert.Equal("https://inviter.example", resolved!.ServerUrl);
        Assert.Equal("fed-inviter", resolved.FederationId);
    }

    [Fact]
    public void ResolveInvite_UnknownCode_ReturnsNull()
    {
        Assert.Null(_store.ResolveInvite("NOPE0000"));
    }

    [Fact]
    public void Initialize_ReloadsPersistedEntriesAndInvites()
    {
        _store.Register("persisted_user", "fed-p", "https://persisted.example");
        var code = _store.CreateInvite("https://persisted.example", "fed-p");

        var reloaded = new FederationDirectoryStore(NullLogger<FederationDirectoryStore>.Instance);
        reloaded.Initialize(_storePath);

        Assert.Single(reloaded.Search("persisted_user"));
        Assert.NotNull(reloaded.ResolveInvite(code));
    }
}
