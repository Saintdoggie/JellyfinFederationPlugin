using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.Federation.Api;
using Jellyfin.Plugin.Federation.Configuration;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

public class ConfigurationUpdateTests
{
    [Fact]
    public void EmptyAndSparseJson_PreserveFriendsMappingsAndSecrets()
    {
        var existing = Existing();

        var empty = ConfigurationUpdate.Merge(JsonNode.Parse("{}")!.AsObject(), existing);
        var sparse = ConfigurationUpdate.Merge(JsonNode.Parse("{\"refreshIntervalHours\":8}")!.AsObject(), existing);

        foreach (var merged in new[] { empty, sparse })
        {
            var server = Assert.Single(merged.RemoteServers);
            Assert.Equal("friend", server.Id);
            Assert.Equal("stored-key", server.ApiKey);
            Assert.True(server.Paired);
            Assert.Equal("Movies", Assert.Single(merged.LibraryMappings).LocalLibraryName);
            Assert.Equal("local-id", merged.LocalFederationId);
        }

        Assert.Equal(8, sparse.RefreshIntervalHours);
        Assert.Equal(6, empty.RefreshIntervalHours);
    }

    [Fact]
    public void ExplicitEmptyCollections_StillMeanDeliberateRemoval()
    {
        var merged = ConfigurationUpdate.Merge(JsonNode.Parse("{\"RemoteServers\":[],\"LibraryMappings\":[]}")!.AsObject(), Existing());

        Assert.Empty(merged.RemoteServers);
        Assert.Empty(merged.LibraryMappings);
    }

    [Fact]
    public void FullSaveShape_AcceptsStringEnumNames()
    {
        var merged = ConfigurationUpdate.Merge(JsonNode.Parse("{\"RemoteServers\":[{\"Id\":\"friend\",\"Url\":\"https://friend.example\",\"WanCapMode\":\"Off\",\"StreamingMode\":\"Proxy\"}]}")!.AsObject(), Existing());

        Assert.Equal(WanCapMode.Off, Assert.Single(merged.RemoteServers).WanCapMode);
        Assert.Equal(StreamingMode.Proxy, merged.RemoteServers[0].StreamingMode);
    }

    [Fact]
    public void PartialSettings_DeserializeOmittedValuesAsNull_AndExplicitFalseAsFalse()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var limits = JsonSerializer.Deserialize<FriendLimitsBody>("{\"monthlyDataCapGb\":25}", options)!;
        Assert.Equal(25, limits.MonthlyDataCapGb);
        Assert.Null(limits.MaxConcurrentStreams);

        var sharing = JsonSerializer.Deserialize<UpdateSharingBody>("{\"shareAll\":false}", options)!;
        Assert.False(sharing.ShareAll);
        Assert.Null(sharing.ExcludedItemIds);
        Assert.Null(JsonSerializer.Deserialize<UpdateSharingBody>("{}", options)!.ShareAll);

        var parties = JsonSerializer.Deserialize<WatchPartySettingsBody>("{\"joinFriendsWatchParties\":false}", options)!;
        Assert.False(parties.JoinFriendsWatchParties);
        Assert.Null(parties.HostWatchPartiesWithFriends);

        var hardware = JsonSerializer.Deserialize<HardwareSettingsBody>("{\"smartTranscodePlacement\":true}", options)!;
        Assert.True(hardware.SmartTranscodePlacement);
        Assert.Null(hardware.ShareTranscodeHardware);
        Assert.Null(hardware.ManualTranscodeCpuThreads);

        var remote = JsonSerializer.Deserialize<RemoteTranscodeSettingsBody>("{\"maxRemoteTranscodesPerFriend\":0}", options)!;
        Assert.Equal(0, remote.MaxRemoteTranscodesPerFriend);
        Assert.Null(remote.EnableRemoteTranscodeForFriends);
        Assert.Null(JsonSerializer.Deserialize<DirectLinkSettingsBody>("{}", options)!.EnableDirectLink);
    }

    private static PluginConfiguration Existing() => new()
    {
        RefreshIntervalHours = 6,
        LocalFederationId = "local-id",
        RemoteServers = new()
        {
            new RemoteServer { Id = "friend", Url = "https://friend.example", ApiKey = "stored-key", Paired = true }
        },
        LibraryMappings = new() { new LibraryMapping { LocalLibraryName = "Movies" } }
    };
}
