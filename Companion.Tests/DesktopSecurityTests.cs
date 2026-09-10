using System.Diagnostics;
using FederationCompanion;

namespace FederationCompanion.Tests;

public class DesktopSecurityTests
{
    [Fact]
    public async Task CredentialFileIsPrivateBeforeWritingSecrets()
    {
        var path = Path.Combine(Path.GetTempPath(), "fed-private-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var file = PrivateFile.Create(path))
            {
                if (!OperatingSystem.IsWindows())
                    Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
                await file.WriteAsync(new byte[] { 1, 2, 3 });
            }
            Assert.Equal(3, new FileInfo(path).Length);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ProcessIdentityRejectsReusedPidAndWrongExecutable()
    {
        using var process = Process.GetCurrentProcess();
        var identity = new LocalMediaMountService.OwnedMountIdentity(process.Id,
            process.StartTime.ToUniversalTime().Ticks, process.MainModule!.FileName!);
        Assert.True(LocalMediaMountService.MatchesOwnedIdentity(process, identity));
        Assert.False(LocalMediaMountService.MatchesOwnedIdentity(process, identity with { StartTimeUtcTicks = identity.StartTimeUtcTicks - 1 }));
        Assert.False(LocalMediaMountService.MatchesOwnedIdentity(process, identity with { Executable = "/unrelated/rclone" }));
    }

    [Fact]
    public void StartupEntryEscapesDesktopFieldCodesAndRejectsLineInjection()
    {
        Assert.Contains("%%f", LinuxAutostart.BuildDesktopEntry("/home/me/100%files/FederationCompanion"));
        Assert.Throws<ArgumentException>(() => LinuxAutostart.BuildDesktopEntry("/tmp/app\nHidden=true"));
    }

    [Fact]
    public void WindowsUpdaterRejectsBatchExpansionInPaths()
        => Assert.Throws<InvalidDataException>(() => CompanionUpdater.WindowsRestartCommands(
            @"C:\Users\%TEMP%\Companion", @"C:\stage", "FederationCompanion.exe", 42));

    [Fact]
    public void UnixUpdaterQuotesSubstitutionAndApostrophes()
        => Assert.Equal("'/tmp/$(id)/a'\"'\"'b'", CompanionUpdater.ShellQuote("/tmp/$(id)/a'b"));
}
