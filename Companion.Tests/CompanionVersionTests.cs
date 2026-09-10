using FederationCompanion;

namespace FederationCompanion.Tests;

public class CompanionVersionTests
{
    [Fact]
    public void ParseLocalRevision_ReadsSourceLinkSuffix()
        => Assert.Equal("c7dcb5c", CompanionVersion.ParseLocalRevision("1.0.0+c7dcb5c"));

    [Fact]
    public void ParseRevisionFromReleaseNotes_ReadsGitHubBody()
        => Assert.Equal("c7dcb5c", CompanionVersion.ParseRevisionFromReleaseNotes("Automated build from c7dcb5c. Installed via Companion/install.sh"));

    [Fact]
    public void SameRevision_MatchesShortAndLongSha()
        => Assert.True(CompanionVersion.SameRevision("c7dcb5cfc7c81920", "c7dcb5c"));

    [Fact]
    public void FederationPluginVersion_IsDottedThreePart()
    {
        var version = CompanionVersion.FederationPluginVersion();
        Assert.Matches(@"^\d+\.\d+\.\d+$", version);
        Assert.NotEqual("0.0.0", version);
    }

    [Fact]
    public void LooksLikeInstalledBuild_AcceptsCompanionBinary()
    {
        Assert.True(CompanionVersion.LooksLikeInstalledBuild("/home/bob/FederationCompanion/FederationCompanion"));
        Assert.True(CompanionVersion.LooksLikeInstalledBuild(@"C:\Users\bob\FederationCompanion\FederationCompanion.exe"));
        Assert.False(CompanionVersion.LooksLikeInstalledBuild("/usr/share/dotnet/dotnet"));
    }

    [Fact]
    public void ReadDnsName_TrimsTrailingDot()
        => Assert.Equal("freakbob.tail4e0b6f.ts.net", TailscaleHelper.ReadDnsName("{\"Self\":{\"DNSName\":\"freakbob.tail4e0b6f.ts.net.\"}}"));

    [Fact]
    public void RestartScript_LeavesProcessCleanupToHostAndStopsOnCopyFailure()
    {
        var text = CompanionUpdater.WindowsRestartCommands(@"C:\Users\bob\FederationCompanion", @"C:\Temp\stage", "FederationCompanion.exe", 4321);
        Assert.DoesNotContain("taskkill /F /IM rclone.exe", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("taskkill", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("if errorlevel 1 exit /b 1", text);
        Assert.Contains("xcopy", text, StringComparison.OrdinalIgnoreCase);
    }
}
