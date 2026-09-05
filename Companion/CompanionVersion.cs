using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace FederationCompanion;

/// <summary>
/// Identifies this Companion build and the GitHub <c>companion-latest</c>
/// rolling release so the UI can offer an in-app update. Parsing is kept
/// here so tests do not need a live GitHub or a replaced binary.
/// </summary>
public static class CompanionVersion
{
    public const string Repo = "Saintdoggie/JellyfinFederationPlugin";
    public const string ReleaseTag = "companion-latest";

    private static readonly Regex NotesRevision = new(
        @"from ([0-9a-f]{7,40})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string Rid()
    {
        if (OperatingSystem.IsWindows())
        {
            return "win-x64";
        }

        if (OperatingSystem.IsMacOS())
        {
            return RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                ? "osx-arm64"
                : "osx-x64";
        }

        return "linux-x64";
    }

    public static string? LocalRevision()
    {
        var informational = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        return ParseLocalRevision(informational);
    }

    public static string? ParseLocalRevision(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion))
        {
            return null;
        }

        var plus = informationalVersion.IndexOf('+');
        if (plus < 0 || plus == informationalVersion.Length - 1)
        {
            return null;
        }

        var suffix = informationalVersion[(plus + 1)..];
        var hex = new string(suffix.TakeWhile(c => Uri.IsHexDigit(c)).ToArray());
        return hex.Length >= 7 ? hex[..Math.Min(hex.Length, 40)] : null;
    }

    public static string? ParseRevisionFromReleaseNotes(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        var match = NotesRevision.Match(body);
        return match.Success ? match.Groups[1].Value : null;
    }

    public static bool SameRevision(string? local, string? remote)
    {
        if (string.IsNullOrWhiteSpace(local) || string.IsNullOrWhiteSpace(remote))
        {
            return false;
        }

        var a = local.Trim();
        var b = remote.Trim();
        var n = Math.Min(Math.Min(a.Length, b.Length), 40);
        if (n < 7)
        {
            return false;
        }

        return a.StartsWith(b[..n], StringComparison.OrdinalIgnoreCase)
            || b.StartsWith(a[..n], StringComparison.OrdinalIgnoreCase);
    }

    public static bool LooksLikeInstalledBuild(string? processPath)
    {
        if (string.IsNullOrWhiteSpace(processPath))
        {
            return false;
        }

        var name = Path.GetFileNameWithoutExtension(processPath.Replace('\\', '/'));
        return string.Equals(name, "FederationCompanion", StringComparison.OrdinalIgnoreCase);
    }

    public static string AssetName() => $"FederationCompanion-{Rid()}.zip";

    public static string ReleaseApiUrl()
        => $"https://api.github.com/repos/{Repo}/releases/tags/{ReleaseTag}";

    public static string AssetDownloadUrl()
        => $"https://github.com/{Repo}/releases/download/{ReleaseTag}/{AssetName()}";
}
