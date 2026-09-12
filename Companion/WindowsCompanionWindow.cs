#if WINDOWS
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace FederationCompanion;

/// <summary>The owner dashboard in its own taskbar window. Closing releases the
/// renderer; the tray, server and media folder continue running.</summary>
internal sealed class WindowsCompanionWindow : Form
{
    private readonly CompanionRuntime _runtime;
    private readonly string _ownerKey;
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(8, 11, 16) };

    public WindowsCompanionWindow(CompanionRuntime runtime, string ownerKey, Icon icon)
    {
        _runtime = runtime;
        _ownerKey = ownerKey;
        Text = "Federation Companion";
        Icon = icon;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1180, 860);
        MinimumSize = new Size(680, 520);
        BackColor = Color.FromArgb(8, 11, 16);
        Controls.Add(_web);
        Shown += async (_, _) => await InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            var profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FederationCompanion", "WebView2");
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: profile);
            if (IsDisposed) return;
            await _web.EnsureCoreWebView2Async(environment);
            if (IsDisposed) return;
            var core = _web.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsWebMessageEnabled = false;
            core.NavigationStarting += (_, e) =>
            {
                if (DesktopNavigation.IsDashboard(e.Uri, _runtime.Port)) return;
                e.Cancel = true;
                if (DesktopNavigation.IsExternalLink(e.Uri)) CompanionShell.OpenUrl(e.Uri);
            };
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                if (DesktopNavigation.IsExternalLink(e.Uri)) CompanionShell.OpenUrl(e.Uri);
            };
            core.FrameNavigationStarting += (_, e) => e.Cancel = true;
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            core.DownloadStarting += (_, e) =>
            {
                e.Cancel = true;
                if (!DesktopNavigation.IsLocalDownload(e.DownloadOperation.Uri, _runtime.Port)) return;
                using var dialog = new SaveFileDialog { FileName = "companion-rclone.conf", Filter = "Companion mount configuration|*.conf", OverwritePrompt = true };
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                e.ResultFilePath = dialog.FileName;
                e.Handled = true;
                e.Cancel = false;
            };
            core.Navigate(_runtime.DashboardUrl(_ownerKey));
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or InvalidOperationException
            or System.Runtime.InteropServices.COMException or IOException or UnauthorizedAccessException)
        {
            if (IsDisposed) return;
            AppLog.Info("Desktop window could not initialize; showing recovery actions.");
            _web.Dispose();
            Controls.Clear();
            var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(32), FlowDirection = FlowDirection.TopDown, WrapContents = false };
            panel.Controls.Add(new Label { AutoSize = true, ForeColor = Color.White, MaximumSize = new Size(560, 0),
                Text = "The desktop window needs Microsoft Edge WebView2. Install or repair the WebView2 Runtime, then reopen Companion. Your media folder is still running." });
            var install = new Button { Text = "Get Microsoft WebView2", AutoSize = true };
            install.Click += (_, _) => CompanionShell.OpenUrl("https://developer.microsoft.com/microsoft-edge/webview2/");
            var browser = new Button { Text = "Open in browser", AutoSize = true };
            browser.Click += (_, _) => CompanionShell.OpenUrl(_runtime.DashboardUrl(_ownerKey));
            panel.Controls.Add(install);
            panel.Controls.Add(browser);
            Controls.Add(panel);
        }
    }
}
#endif
