using FederationCompanion;

namespace FederationCompanion.Tests;

public class CompanionAppShellTests
{
    [Fact]
    public void AutostartCommand_QuotesPathAndStartsInTray()
    {
        var command = AutostartCommand.Build(@"C:\Program Files\Federation Companion\FederationCompanion.exe");
        Assert.Equal(@"""C:\Program Files\Federation Companion\FederationCompanion.exe"" --tray", command);
    }

    [Fact]
    public void AutostartCommand_RejectsEmptyPath()
        => Assert.Throws<ArgumentException>(() => AutostartCommand.Build("  "));

    [Fact]
    public void LaunchOptions_DefaultOpensBrowserOnce()
    {
        var options = CompanionLaunchOptions.Parse(Array.Empty<string>());
        Assert.False(options.Background);
        Assert.True(options.OpenBrowser);
    }

    [Fact]
    public void LaunchOptions_TrayRunsQuietly()
    {
        var options = CompanionLaunchOptions.Parse(new[] { "--tray" });
        Assert.True(options.Background);
        Assert.False(options.OpenBrowser);
    }

    [Fact]
    public void LaunchOptions_NoBrowserSuppressesTheDashboard()
    {
        var options = CompanionLaunchOptions.Parse(new[] { "--no-browser" });
        Assert.False(options.Background);
        Assert.False(options.OpenBrowser);
    }

    [Fact]
    public void LaunchOptions_ExplicitOpenWinsOverBackground()
    {
        var options = CompanionLaunchOptions.Parse(new[] { "--tray", "--open" });
        Assert.True(options.Background);
        Assert.True(options.OpenBrowser);
    }

    [Fact]
    public void KestrelArgs_DropsCompanionFlagsButKeepsUrlBinding()
    {
        var args = CompanionLaunchOptions.KestrelArgs(new[] { "--tray", "--urls", "http://127.0.0.1:8123", "--no-browser" });
        Assert.Equal(new[] { "--urls", "http://127.0.0.1:8123" }, args);
    }

    [Fact]
    public void RuntimeDashboardUrl_BindsToLoopbackAndCarriesTheOwnerKey()
    {
        var runtime = new CompanionRuntime { Port = 8123 };
        Assert.Equal("http://127.0.0.1:8123/#access=abc123", runtime.DashboardUrl("abc123"));
    }

    [Fact]
    public void AppLogLine_HasTimestampAndLevel()
    {
        var line = AppLog.FormatLine(new DateTimeOffset(2026, 9, 9, 12, 34, 56, TimeSpan.FromHours(-5)), "INFO", "started");
        Assert.StartsWith("2026-09-09 12:34:56 -05:00 [INFO] started", line);
        Assert.EndsWith(Environment.NewLine, line);
    }

    [Fact]
    public void UnsupportedAutostart_ReportsUnsupportedAndNoOps()
    {
        var autostart = new UnsupportedAutostartRegistration();
        Assert.False(autostart.IsSupported);
        Assert.False(autostart.IsEnabled());
        Assert.False(autostart.Set(true));
    }

    [Fact]
    public void LinuxAutostart_BuildsXDGEntryWithQuotedTrayCommand()
    {
        var entry = LinuxAutostart.BuildDesktopEntry("/home/bob/Federation Companion/FederationCompanion");

        Assert.Contains("[Desktop Entry]", entry);
        Assert.Contains("Type=Application", entry);
        Assert.Contains("Name=Federation Companion", entry);
        Assert.Contains("Exec=\"/home/bob/Federation Companion/FederationCompanion\" --tray", entry);
        Assert.Contains("Terminal=false", entry);
    }

    [Fact]
    public void LinuxAutostart_EntryPathHonorsXdgConfigHome()
    {
        var previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", "/tmp/fed-xdg");
            Assert.Equal("/tmp/fed-xdg/autostart/federation-companion.desktop", LinuxAutostart.EntryPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous);
        }
    }

    [Fact]
    public async Task SingleInstance_SecondAcquireFailsAndPipeRoundTrips()
    {
        var suffix = "-test-" + Guid.NewGuid().ToString("N");
        using var first = new SingleInstance(suffix);
        Assert.True(first.TryAcquire());

        using var second = new SingleInstance(suffix);
        Assert.False(second.TryAcquire());

        var opened = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        first.StartListener(() => opened.TrySetResult(true));
        Assert.True(SingleInstance.TrySignalExistingInstance(TimeSpan.FromSeconds(5), first.InstancePipeName));
        await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Shell_RefusesMissingFolder()
    {
        Assert.False(CompanionShell.OpenFolder(Path.Combine(Path.GetTempPath(), "federation-companion-missing-" + Guid.NewGuid().ToString("N"))));
    }
}
