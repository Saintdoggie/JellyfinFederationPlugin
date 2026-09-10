using System.Globalization;

namespace FederationCompanion;

/// <summary>
/// Command line the desktop launcher and shortcuts use. <c>--tray</c> is what
/// the Windows sign-in entry starts with: run in the background and do not
/// pop a browser. A bare launch (or <c>--open</c>) opens the dashboard once.
/// Unknown arguments are passed through to Kestrel so <c>--urls</c> still
/// works.
/// </summary>
public sealed record CompanionLaunchOptions(bool Background, bool OpenBrowser)
{
    public static CompanionLaunchOptions Parse(string[] args)
    {
        var background = args.Any(IsBackgroundFlag);
        var open = args.Any(a => string.Equals(a, "--open", StringComparison.OrdinalIgnoreCase));
        var suppress = args.Any(a => string.Equals(a, "--no-browser", StringComparison.OrdinalIgnoreCase));
        return new CompanionLaunchOptions(background, !suppress && (open || !background));
    }

    /// <summary>Arguments Kestrel/configuration should see, without our own switches.</summary>
    public static string[] KestrelArgs(string[] args)
        => args.Where(a => !IsOwnFlag(a)).ToArray();

    /// <summary>
    /// Kestrel has no "next free port" of its own. If the owner did not pass
    /// <c>--urls</c> or <c>ASPNETCORE_URLS</c>, bind loopback on 5000, then
    /// the next few ports, so a WinExe with no console does not die silently
    /// when something else already took 5000.
    /// </summary>
    public static string[] WithListenUrl(string[] kestrelArgs, string url)
    {
        if (HasListenUrl(kestrelArgs))
        {
            return kestrelArgs;
        }

        var copy = new string[kestrelArgs.Length + 2];
        kestrelArgs.CopyTo(copy, 0);
        copy[kestrelArgs.Length] = "--urls";
        copy[kestrelArgs.Length + 1] = url;
        return copy;
    }

    public static bool HasListenUrl(string[] kestrelArgs)
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
        {
            return true;
        }

        for (var i = 0; i < kestrelArgs.Length; i++)
        {
            var arg = kestrelArgs[i];
            if (string.Equals(arg, "--urls", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (arg.StartsWith("--urls=", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsOwnFlag(string arg)
        => IsBackgroundFlag(arg)
            || string.Equals(arg, "--open", StringComparison.OrdinalIgnoreCase)
            || string.Equals(arg, "--no-browser", StringComparison.OrdinalIgnoreCase);

    private static bool IsBackgroundFlag(string arg)
        => string.Equals(arg, "--tray", StringComparison.OrdinalIgnoreCase)
            || string.Equals(arg, "--background", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Live process facts the owner UI and the tray show: where the app is
/// installed, which port it actually bound, how long it has run and how much
/// memory it is using. Registered once at startup.
/// </summary>
public sealed class CompanionRuntime
{
    public int Port { get; set; }

    public DateTimeOffset StartedUtc { get; } = DateTimeOffset.UtcNow;

    public bool BackgroundLaunch { get; init; }

    public string InstallDirectory => AppContext.BaseDirectory;

    public string LogPath => AppLog.LogPath;

    public TimeSpan Uptime => DateTimeOffset.UtcNow - StartedUtc;

    public long WorkingSetBytes => Environment.WorkingSet;

    public string DashboardUrl(string adminAccessKey)
        => $"http://127.0.0.1:{Port.ToString(CultureInfo.InvariantCulture)}/#access={Uri.EscapeDataString(adminAccessKey)}";
}
