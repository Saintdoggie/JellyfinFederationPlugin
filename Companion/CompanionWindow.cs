#if WINDOWS
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace FederationCompanion;

/// <summary>
/// The Companion's own app window: the local dashboard hosted in WebView2
/// (preinstalled on Windows 10/11) instead of a browser tab. Navigation is
/// pinned to the loopback dashboard origin; any other link opens in the
/// default browser. Closing the window hides it — the tray keeps Companion
/// running — and Exit in the tray menu still stops everything.
/// </summary>
internal sealed class CompanionWindow : Form
{
    private static readonly Color Background = Color.FromArgb(0x10, 0x14, 0x1c);
    private readonly WebView2 _view = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Background };
    private readonly Label _status = new()
    {
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleCenter,
        ForeColor = Color.FromArgb(0xc8, 0xd0, 0xdc),
        BackColor = Background,
        Font = new Font("Segoe UI", 11f),
        Text = "Starting Federation Companion…"
    };
    private readonly Uri _origin;
    private string _url;
    private bool _ready;
    private bool _allowClose;

    private CompanionWindow(string url, Icon? icon)
    {
        _url = url;
        _origin = new Uri(new Uri(url).GetLeftPart(UriPartial.Authority));
        Text = "Federation Companion";
        if (icon != null) Icon = icon;
        BackColor = Background;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        MinimumSize = new Size(760, 560);
        Size = new Size(1180, 820);
        Controls.Add(_view);
        Controls.Add(_status);
        _status.BringToFront();
    }

    /// <summary>Owned by the tray's STA thread; created on first open.</summary>
    private static CompanionWindow? _instance;
    private static SynchronizationContext? _ui;
    private static Icon? _icon;

    /// <summary>Registers the window as the dashboard opener. Call on the tray's UI thread.</summary>
    internal static void Register(Icon icon)
    {
        _ui = SynchronizationContext.Current;
        _icon = icon;
        CompanionShell.WindowOpener = url =>
        {
            if (_ui == null || !WebViewAvailable()) return false;
            _ui.Post(_ => ShowOrActivate(url), null);
            return true;
        };
    }

    internal static void CloseForExit()
    {
        var window = _instance;
        if (window == null || _ui == null) return;
        _ui.Send(_ => { window._allowClose = true; window.Close(); }, null);
    }

    private static bool WebViewAvailable()
    {
        try
        {
            return !string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString());
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void ShowOrActivate(string url)
    {
        if (_instance == null || _instance.IsDisposed)
        {
            _instance = new CompanionWindow(url, _icon);
            _instance.Show();
        }
        else
        {
            _instance._url = url;
            if (_instance._ready && _instance._view.CoreWebView2 != null
                && !string.Equals(_instance._view.Source?.GetLeftPart(UriPartial.Authority), _instance._origin.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase))
            {
                _instance._view.CoreWebView2.Navigate(url);
            }
            if (!_instance.Visible) _instance.Show();
            if (_instance.WindowState == FormWindowState.Minimized) _instance.WindowState = FormWindowState.Normal;
        }
        _instance.Activate();
        _instance.BringToFront();
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        try
        {
            var dataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FederationCompanion", "WebView2");
            var environment = await CoreWebView2Environment.CreateAsync(null, dataFolder);
            await _view.EnsureCoreWebView2Async(environment);
            var core = _view.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsZoomControlEnabled = true;
            core.Settings.AreBrowserAcceleratorKeysEnabled = true;
            core.NavigationStarting += (_, args) =>
            {
                if (IsDashboard(args.Uri)) return;
                args.Cancel = true;
                OpenExternal(args.Uri);
            };
            core.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                if (IsDashboard(args.Uri)) core.Navigate(args.Uri);
                else OpenExternal(args.Uri);
            };
            core.DocumentTitleChanged += (_, _) =>
            {
                var title = core.DocumentTitle;
                Text = string.IsNullOrWhiteSpace(title) || title.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? "Federation Companion" : title;
            };
            core.NavigationCompleted += (_, _) =>
            {
                _status.Visible = false;
                _view.BringToFront();
            };
            _ready = true;
            core.Navigate(_url);
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or InvalidOperationException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            AppLog.Warn("App window unavailable; opening the dashboard in the browser instead: " + ex.GetType().Name);
            CompanionShell.OpenUrl(_url);
            _allowClose = true;
            Close();
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Closing the window is not quitting: Companion keeps serving friends
        // from the tray, exactly like closing a chat app's window.
        if (!_allowClose && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }

    private bool IsDashboard(string uri)
        => Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
            && string.Equals(parsed.GetLeftPart(UriPartial.Authority), _origin.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);

    private static void OpenExternal(string uri)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && (parsed.Scheme == Uri.UriSchemeHttps || parsed.Scheme == Uri.UriSchemeHttp))
        {
            CompanionShell.OpenUrl(parsed.AbsoluteUri);
        }
    }
}
#endif
