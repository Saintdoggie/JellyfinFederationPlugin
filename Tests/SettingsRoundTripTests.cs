using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Federation.Api;
using Jellyfin.Plugin.Federation.Configuration;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

/// <summary>
/// The settings page saves the whole main form, so every value it posts must also
/// come back from the sanitized GET. A switch missing from the GET reloads as its
/// default and the next save silently puts it back.
/// </summary>
public class SettingsRoundTripTests
{
    private static string RepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JellyfinFederationPlugin.csproj")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, relative);
    }

    [Fact]
    public void NewSwitchesComeBackFromTheSanitizedConfiguration()
    {
        var config = new PluginConfiguration { ShowPlaybackServerLabel = false, SmartSourceSelection = true, AdaptiveQualityLadder = true };
        var sanitized = FederationController.Sanitize(config);
        var type = sanitized.GetType();
        Assert.Equal(false, type.GetProperty("ShowPlaybackServerLabel")!.GetValue(sanitized));
        Assert.Equal(true, type.GetProperty("SmartSourceSelection")!.GetValue(sanitized));
        Assert.Equal(true, type.GetProperty("AdaptiveQualityLadder")!.GetValue(sanitized));
    }

    [Fact]
    public void EveryControlTheMainFormSavesIsAlsoReturnedByTheGet()
    {
        var page = File.ReadAllText(RepoFile("Web/admin/configPage.html"));
        var start = page.IndexOf("function saveConfiguration()", StringComparison.Ordinal);
        var end = page.IndexOf("\n                        };", start, StringComparison.Ordinal);
        Assert.True(start > 0 && end > start);
        var block = page.Substring(start, end - start);

        // Keys whose value is read from a control on the page.
        var saved = Regex.Matches(block, @"^\s+(?<key>[A-Z][A-Za-z0-9]+): q\('#", RegexOptions.Multiline)
            .Select(m => m.Groups["key"].Value)
            .Distinct()
            .ToList();
        Assert.Contains("ShowPlaybackServerLabel", saved);
        Assert.Contains("SmartSourceSelection", saved);
        Assert.True(saved.Count >= 10, "the save block was not found where expected");

        var returned = FederationController.Sanitize(new PluginConfiguration()).GetType().GetProperties().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var missing = saved.Where(key => !returned.Contains(key)).ToList();
        Assert.True(missing.Count == 0, "Saved by the page but never returned by GET Configuration: " + string.Join(", ", missing));

        // And each of them is a real persisted setting.
        var persisted = typeof(PluginConfiguration).GetProperties().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        Assert.All(saved, key => Assert.Contains(key, persisted));
    }
}
