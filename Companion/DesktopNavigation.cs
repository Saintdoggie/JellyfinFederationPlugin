namespace FederationCompanion;

public static class DesktopNavigation
{
    public static bool IsDashboard(string? target, int port)
        => Uri.TryCreate(target, UriKind.Absolute, out var uri)
            && uri.Scheme == "http" && uri.Host == "127.0.0.1" && uri.Port == port
            && string.IsNullOrEmpty(uri.UserInfo) && uri.AbsolutePath == "/";

    public static bool IsLocalDownload(string target, int port)
        => target.StartsWith($"blob:http://127.0.0.1:{port}/", StringComparison.Ordinal);

    public static bool IsExternalLink(string? target)
        => Uri.TryCreate(target, UriKind.Absolute, out var uri)
            && uri.Scheme == "https" && string.IsNullOrEmpty(uri.UserInfo)
            && string.IsNullOrEmpty(uri.Fragment)
            && (uri.Host == "app.plex.tv" || uri.Host == "www.plex.tv"
                || uri.Host == "developer.microsoft.com" || uri.Host == "rclone.org" || uri.Host == "github.com" || uri.Host == "tailscale.com");
}
